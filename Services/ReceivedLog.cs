using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Services;

// Every line the gateway receives except IO / motion (those go live to MQTT), whole, as it
// arrived: to prove what a machine sent and to debug a mapping. One SQLite file per day, next
// to the outbox:
//   received/<machine_id>/received-yyyy-MM-dd.db   machines with their own tab (as outbox/<machine_id>/)
//   received/received-yyyy-MM-dd.db                the rest: no tab (held / shared), no machine_id, unreadable
// so a sender spraying random machine_ids (port 9000 has no login) can't make folders without end.
// Written in batches by one background writer, so it never slows a line or its ack down. The cost:
// a crash can lose the last moment of lines, and a disk too slow skips lines (said once a minute).
// Day files older than KeepDays (outboxKeepSentDays) are deleted, checked every hour.
// Lives as long as the process (survives a restart from the config page).
public sealed class ReceivedLog : IAsyncDisposable
{
    private const int QueueCapacity = 20_000; // lines of 1-3 KB: up to ~60 MB waiting
    private const int MaxBatch = 5_000;
    private const int MaxPage = 500;
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);
    private static readonly Regex DayPattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    public static string Folder => Path.Combine(DataFolder.Root, "received");

    private readonly string _dir;
    private readonly Channel<Entry> _queue;
    private readonly Task _writer;
    private readonly ThrottledLog _problems = new(TimeSpan.FromSeconds(60));
    private long _lost;

    // outboxKeepSentDays, set at every gateway start.
    public int KeepDays { get; set; } = 7;

    private readonly record struct Entry(long AtMs, string Folder, string Machine, string Sender, string Text);

    public ReceivedLog()
    {
        _dir = Folder;
        Directory.CreateDirectory(_dir);
        _queue = Channel.CreateBounded<Entry>(
            new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite },
            _ => Interlocked.Increment(ref _lost));
        _writer = Task.Run(WriterLoopAsync);
    }

    // folder: the machine_id when it has its own tab, else null (the shared file).
    public void Record(string? folder, string machine, string sender, string line) =>
        _queue.Writer.TryWrite(new Entry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), folder ?? "", machine, sender, line));

    private async Task WriterLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<Entry>(MaxBatch);
        var nextPurge = DateTimeOffset.MinValue;
        while (await reader.WaitToReadAsync())
        {
            while (batch.Count < MaxBatch && reader.TryRead(out var line))
                batch.Add(line);
            foreach (var file in batch.GroupBy(l => (l.Folder, Day: DayOf(l.AtMs))))
            {
                try { Write(file.Key.Folder, file.Key.Day, file); }
                catch (Exception ex)
                {
                    _problems.Write("write", $"[received] {file.Count()} lines not recorded, {PathOf(_dir, file.Key.Folder, file.Key.Day)} could not be written: {ex.Message}");
                }
            }
            batch.Clear();

            if (Interlocked.Exchange(ref _lost, 0) is var lost and > 0)
                _problems.Write("lost", $"[received] recorder behind: {lost} lines not recorded (disk too slow)");
            if (DateTimeOffset.UtcNow >= nextPurge)
            {
                Purge();
                nextPurge = DateTimeOffset.UtcNow + PurgeEvery;
            }
        }
    }

    private void Write(string folder, DateTime day, IEnumerable<Entry> lines)
    {
        var path = PathOf(_dir, folder, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;" +
                                "CREATE TABLE IF NOT EXISTS lines (id INTEGER PRIMARY KEY, at INTEGER NOT NULL, machine TEXT NOT NULL, sender TEXT NOT NULL, line TEXT NOT NULL);";
            setup.ExecuteNonQuery();
        }
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO lines (at, machine, sender, line) VALUES ($at, $machine, $sender, $line);";
        var at = cmd.Parameters.Add("$at", SqliteType.Integer);
        var machine = cmd.Parameters.Add("$machine", SqliteType.Text);
        var sender = cmd.Parameters.Add("$sender", SqliteType.Text);
        var text = cmd.Parameters.Add("$line", SqliteType.Text);
        cmd.Prepare();
        foreach (var line in lines)
        {
            at.Value = line.AtMs;
            machine.Value = line.Machine;
            sender.Value = line.Sender;
            text.Value = line.Text;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // Day files older than KeepDays go (their -wal / -shm too), in every folder.
    public int Purge()
    {
        var removed = 0;
        var oldest = DateTime.Now.Date.AddDays(-KeepDays);
        foreach (var folder in Directory.GetDirectories(_dir).Prepend(_dir))
            foreach (var file in Directory.GetFiles(folder, "received-*.db"))
            {
                if (!TryDayOf(file, out var day) || day >= oldest)
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
                    _problems.Write("purge", $"[received] could not delete {file}: {ex.Message}");
                }
            }
        return removed;
    }

    // Writes what is queued (briefly) on a clean stop.
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    // ---- read-only, for the config page's Received view (same scopes as the outbox view) ----

    // "" = the shared file, then one per machine folder.
    public static IReadOnlyList<string> Scopes()
    {
        var scopes = new List<string> { "" };
        if (Directory.Exists(Folder))
            scopes.AddRange(Directory.GetDirectories(Folder).Select(Path.GetFileName)
                .Where(MachineRegistry.IsValidId).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)!);
        return scopes;
    }

    // Days with their line count and size, newest first; null = not a valid scope.
    public static object? Days(string? scope)
    {
        if (!string.IsNullOrWhiteSpace(scope) && !MachineRegistry.IsValidId(scope))
            return null;
        var folder = string.IsNullOrWhiteSpace(scope) ? Folder : Path.Combine(Folder, scope);
        if (!Directory.Exists(folder))
            return Array.Empty<object>();
        return Directory.GetFiles(folder, "received-*.db")
            .Where(path => TryDayOf(path, out _))
            .Select(path =>
            {
                long lines = 0;
                try
                {
                    using var conn = OpenRead(path);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM lines;";
                    lines = (long)cmd.ExecuteScalar()!;
                }
                catch { /* being created */ }
                var bytes = new FileInfo(path).Length + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);
                return new { day = Path.GetFileNameWithoutExtension(path)["received-".Length..], lines, megabytes = Math.Round(bytes / 1048576.0, 1) };
            })
            .OrderByDescending(d => d.day)
            .ToList();
    }

    // One day's lines, newest first, in pages (beforeId); machine matches without case, text = the line
    // contains it (without case). Null when there is no file for that day.
    public static object? Query(string? scope, string day, string? machine, string? text, long? beforeId, int limit)
    {
        var path = FileFor(scope, day);
        if (path is null)
            return null;
        using var conn = OpenRead(path);
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(machine)) { where.Add("machine = $machine COLLATE NOCASE"); cmd.Parameters.AddWithValue("$machine", machine.Trim()); }
        if (!string.IsNullOrWhiteSpace(text)) { where.Add("instr(lower(line), lower($text)) > 0"); cmd.Parameters.AddWithValue("$text", text.Trim()); }
        if (beforeId is { } before) { where.Add("id < $before"); cmd.Parameters.AddWithValue("$before", before); }
        cmd.CommandText = $"SELECT id, at, machine, sender, length(line), substr(line, 1, 240) FROM lines " +
                          $"{(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxPage));
        var rows = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new { id = r.GetInt64(0), at = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), machine = r.GetString(2), sender = r.GetString(3), size = r.GetInt64(4), preview = r.GetString(5) });
        return new { rows };
    }

    // One line in full; null = not found.
    public static string? Line(string? scope, string day, long id)
    {
        var path = FileFor(scope, day);
        if (path is null)
            return null;
        using var conn = OpenRead(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT line FROM lines WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    // Only a valid machine_id and a yyyy-MM-dd day map to a file (no path tricks).
    private static string? FileFor(string? scope, string day)
    {
        if (!DayPattern.IsMatch(day) || (!string.IsNullOrWhiteSpace(scope) && !MachineRegistry.IsValidId(scope)))
            return null;
        var path = Path.Combine(string.IsNullOrWhiteSpace(scope) ? Folder : Path.Combine(Folder, scope), $"received-{day}.db");
        return File.Exists(path) ? path : null;
    }

    private static SqliteConnection OpenRead(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }

    private static bool TryDayOf(string path, out DateTime day) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path)["received-".Length..], "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static DateTime DayOf(long atMs) => DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToLocalTime().Date;

    private static string PathOf(string dir, string folder, DateTime day) =>
        Path.Combine(folder.Length == 0 ? dir : Path.Combine(dir, folder), string.Create(CultureInfo.InvariantCulture, $"received-{day:yyyy-MM-dd}.db"));
}
