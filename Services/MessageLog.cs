using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Services;

// Every line the gateway receives (MES, IO, motion; also repeats and broken lines), as
// time + machine, without its content: "how many arrived from when to when, from whom".
// One SQLite file per day (data/traffic/traffic-yyyy-MM-dd.db, local day), written in
// batches by one writer so counting never slows the lines down. Files older than KeepDays
// are deleted. Lives as long as the process (survives a restart from the config page).
public sealed class MessageLog : IAsyncDisposable
{
    public const int KeepDays = 7;
    private const int QueueCapacity = 200_000;
    private const int MaxBatch = 20_000;
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    private readonly string _dir;
    private readonly Channel<(long AtMs, string Machine)> _queue;
    private readonly Task _writer;
    private readonly ThrottledLog _problems = new(TimeSpan.FromSeconds(60));
    private long _lost;

    public MessageLog(string directory)
    {
        _dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(_dir);
        // Disk too slow: lines are not counted (said once a minute) rather than slowing the gateway.
        _queue = Channel.CreateBounded<(long, string)>(
            new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite },
            _ => Interlocked.Increment(ref _lost));
        _writer = Task.Run(WriterLoopAsync);
    }

    // One line arrived from this machine ("(unreadable)", "(no machine_id)" when unknown).
    public void Record(string machine) => _queue.Writer.TryWrite((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), machine));

    private async Task WriterLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<(long AtMs, string Machine)>(MaxBatch);
        var nextPurge = DateTimeOffset.MinValue;
        while (await reader.WaitToReadAsync())
        {
            while (batch.Count < MaxBatch && reader.TryRead(out var line))
                batch.Add(line);
            try
            {
                foreach (var day in batch.GroupBy(l => DayOf(l.AtMs)))
                    Write(day.Key, day);
            }
            catch (Exception ex)
            {
                _problems.Write("write", $"[messages] {batch.Count} lines not counted, the message log could not be written: {ex.Message}");
            }
            batch.Clear();

            if (Interlocked.Exchange(ref _lost, 0) is var lost and > 0)
                _problems.Write("lost", $"[messages] message log behind: {lost} lines not counted (disk too slow)");
            if (DateTimeOffset.UtcNow >= nextPurge)
            {
                Purge();
                nextPurge = DateTimeOffset.UtcNow + PurgeEvery;
            }
        }
    }

    private void Write(DateTime day, IEnumerable<(long AtMs, string Machine)> lines)
    {
        using var conn = Open(day, readOnly: false);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO lines (at, machine) VALUES ($at, $machine);";
        var at = cmd.Parameters.Add("$at", SqliteType.Integer);
        var machine = cmd.Parameters.Add("$machine", SqliteType.Text);
        cmd.Prepare();
        foreach (var line in lines)
        {
            at.Value = line.AtMs;
            machine.Value = line.Machine;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // How many lines arrived in [from, to), local time; machine null = all.
    public MessageCounts Count(DateTime from, DateTime to, string? machine)
    {
        long fromMs = new DateTimeOffset(from).ToUnixTimeMilliseconds(), toMs = new DateTimeOffset(to).ToUnixTimeMilliseconds();
        var perMachine = new Dictionary<string, long>(StringComparer.Ordinal);
        var perHour = new SortedDictionary<long, long>();
        for (var day = from.Date; day <= to.Date && day < to; day = day.AddDays(1))
        {
            if (!File.Exists(PathOf(day)))
                continue;
            using var conn = Open(day, readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT machine, at / 3600000, COUNT(*) FROM lines WHERE at >= $from AND at < $to AND ($machine IS NULL OR machine = $machine) GROUP BY 1, 2;";
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);
            cmd.Parameters.AddWithValue("$machine", (object?)machine ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var count = reader.GetInt64(2);
                perMachine[reader.GetString(0)] = perMachine.GetValueOrDefault(reader.GetString(0)) + count;
                perHour[reader.GetInt64(1)] = perHour.GetValueOrDefault(reader.GetInt64(1)) + count;
            }
        }
        return new MessageCounts(
            perMachine.Values.Sum(),
            perMachine.OrderByDescending(m => m.Value).ThenBy(m => m.Key).Select(m => new MachineCount(m.Key, m.Value)).ToList(),
            perHour.Select(h => new HourCount(DateTimeOffset.FromUnixTimeMilliseconds(h.Key * 3600000).ToLocalTime().DateTime, h.Value)).ToList(),
            StoredBytes());
    }

    // Day files older than KeepDays go (their -wal / -shm too).
    public int Purge()
    {
        var removed = 0;
        var oldest = DateTime.Now.Date.AddDays(-KeepDays);
        foreach (var file in Directory.GetFiles(_dir, "traffic-*.db"))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)["traffic-".Length..], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day >= oldest)
                continue;
            try
            {
                foreach (var part in new[] { file, file + "-wal", file + "-shm" })
                    if (File.Exists(part))
                        File.Delete(part);
                removed++;
            }
            catch (IOException ex)
            {
                _problems.Write("purge", $"[messages] could not delete {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return removed;
    }

    public long StoredBytes() => Directory.GetFiles(_dir, "traffic-*").Sum(f => new FileInfo(f).Length);

    private static DateTime DayOf(long atMs) => DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToLocalTime().Date;

    private string PathOf(DateTime day) => Path.Combine(_dir, string.Create(CultureInfo.InvariantCulture, $"traffic-{day:yyyy-MM-dd}.db"));

    private SqliteConnection Open(DateTime day, bool readOnly)
    {
        var conn = new SqliteConnection($"Data Source={PathOf(day)};Pooling=False{(readOnly ? ";Mode=ReadOnly" : "")}");
        conn.Open();
        if (!readOnly)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;" +
                              "CREATE TABLE IF NOT EXISTS lines (at INTEGER NOT NULL, machine TEXT NOT NULL);" +
                              "CREATE INDEX IF NOT EXISTS ix_lines_at ON lines (at);";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    // Writes what is queued (briefly) on a clean stop.
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(5)));
    }
}

public sealed record MachineCount(string Machine, long Count);
public sealed record HourCount(DateTime Hour, long Count);
public sealed record MessageCounts(long Total, IReadOnlyList<MachineCount> Machines, IReadOnlyList<HourCount> Hours, long StoredBytes);
