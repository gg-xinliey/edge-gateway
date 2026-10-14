using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Local, durable outbox backed by SQLite (nothing to install).
// Every envelope is stored with sent = 0 BEFORE the gateway tries to send it.
// Only an acknowledgement from MES flips it to sent = 1.
//
// Built for many machines:
//   * Write() only queues the envelope; one background writer stores the
//     queue in batches (one transaction per batch). Per-row writes capped
//     the old version at ~300 rows/s and blocked the TCP reader.
//   * One file per day: outbox\outbox-yyyy-MM-dd.db. After `keepSentDays`
//     a day file is deleted as a whole, but only once every row in it was
//     delivered. Deleting a file is instant; no row deletes, no compacting.
//   * The old single outbox\outbox.db (if any) is still read until all its
//     rows are delivered, then removed the same way.
// Unsent rows are never deleted. Up to one batch (~tens of ms) sits in memory
// before it is on disk; a graceful stop or restart writes it first.
public sealed class Outbox : IAsyncDisposable
{
    private const int MaxBatch = 5_000;
    private const int QueueCapacity = 200_000; // ~ a minute at full load; past that Write() waits
    private const string LegacyFile = "outbox.db";

    private readonly string _dir;
    private readonly TimeSpan _keepSentFor;
    private readonly Channel<Envelope> _queue = Channel.CreateBounded<Envelope>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writer;

    private readonly object _filesSync = new();
    private readonly HashSet<string> _initialised = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _delivered = new(StringComparer.OrdinalIgnoreCase); // past days with no unsent rows

    private readonly GatewayStats? _stats;

    public Outbox(string directory, int keepSentDays = 7, GatewayStats? stats = null)
    {
        _stats = stats;
        _dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(_dir);
        _keepSentFor = TimeSpan.FromDays(keepSentDays);
        _writer = Task.Run(WriterLoopAsync);
    }

    // Envelopes waiting in memory for the writer.
    public int Queued => _queue.Reader.Count;

    public void Write(Envelope envelope)
    {
        _stats?.MesQueued();
        if (_queue.Writer.TryWrite(envelope))
            return;

        // Queue full (disk can't keep up): wait for room instead of losing data.
        _queue.Writer.WriteAsync(envelope).AsTask().GetAwaiter().GetResult();
    }

    // ---- writer ----

    private async Task WriterLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<Envelope>(MaxBatch);
        SqliteConnection? conn = null;
        var connFile = "";
        var lastErrorLog = DateTime.MinValue;

        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (batch.Count < MaxBatch && reader.TryRead(out var envelope))
                    batch.Add(envelope);

                // Retry until stored: a full disk or a locked file must not lose envelopes.
                while (true)
                {
                    try
                    {
                        var file = DayFile(DateTime.Now);
                        if (conn is null || connFile != file)
                        {
                            conn?.Dispose(); // closing yesterday's file also folds its WAL back in
                            conn = OpenFile(file);
                            using var fast = conn.CreateCommand();
                            fast.CommandText = "PRAGMA synchronous = NORMAL;"; // WAL: no disk flush per commit
                            fast.ExecuteNonQuery();
                            connFile = file;
                        }

                        InsertBatch(conn, batch);
                        break;
                    }
                    catch (Exception ex)
                    {
                        conn?.Dispose();
                        conn = null;
                        if (DateTime.Now - lastErrorLog > TimeSpan.FromMinutes(1))
                        {
                            Console.WriteLine($"[outbox] write failed, {batch.Count} envelopes kept in memory and retried: {ex.Message}");
                            lastErrorLog = DateTime.Now;
                        }
                        await Task.Delay(1000);
                    }
                }

                batch.Clear();
            }
        }
        finally
        {
            conn?.Dispose();
        }
    }

    private static void InsertBatch(SqliteConnection conn, List<Envelope> batch)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO outbox (event_id, data_group, body, sent, created_at)
            VALUES ($event_id, $data_group, $body, 0, $created_at);";
        var eventId = cmd.Parameters.Add("$event_id", SqliteType.Text);
        var dataGroup = cmd.Parameters.Add("$data_group", SqliteType.Text);
        var body = cmd.Parameters.Add("$body", SqliteType.Text);
        var createdAt = cmd.Parameters.Add("$created_at", SqliteType.Text);
        cmd.Prepare();

        var now = Now();
        foreach (var envelope in batch)
        {
            eventId.Value = envelope.event_id;
            dataGroup.Value = envelope.data_group;
            body.Value = JsonSerializer.Serialize(envelope);
            createdAt.Value = now;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ---- reading / acknowledging (MES forwarder) ----

    // Oldest first across all day files, so a long MES outage drains in order.
    public List<(OutboxKey Key, Envelope Envelope)> ReadUnsent(int limit = 100)
    {
        var results = new List<(OutboxKey, Envelope)>();
        foreach (var file in PendingFiles())
        {
            using var conn = OpenFile(file);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, body FROM outbox WHERE sent = 0 ORDER BY id LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit - results.Count);

            var found = 0;
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    found++;
                    var envelope = JsonSerializer.Deserialize<Envelope>(reader.GetString(1));
                    if (envelope is not null)
                        results.Add((new OutboxKey(file, reader.GetInt64(0)), envelope));
                }
            }

            if (found == 0)
                MarkDeliveredIfPast(file);
            if (results.Count >= limit)
                break;
        }

        return results;
    }

    public void MarkSent(OutboxKey key) => MarkSent(new[] { key });

    // One transaction per file for the whole set.
    public void MarkSent(IEnumerable<OutboxKey> keys)
    {
        var now = Now();
        foreach (var group in keys.GroupBy(k => k.File))
        {
            using var conn = OpenFile(group.Key);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE outbox SET sent = 1, sent_at = $sent_at WHERE id = $id;";
            var sentAt = cmd.Parameters.Add("$sent_at", SqliteType.Text);
            var id = cmd.Parameters.Add("$id", SqliteType.Integer);
            cmd.Prepare();

            sentAt.Value = now;
            foreach (var key in group)
            {
                id.Value = key.Id;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public long CountUnsent()
    {
        long total = 0;
        foreach (var file in PendingFiles())
        {
            using var conn = OpenFile(file);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM outbox WHERE sent = 0;";
            var count = (long)cmd.ExecuteScalar()!;
            if (count == 0)
                MarkDeliveredIfPast(file);
            total += count;
        }

        return total;
    }

    // ---- housekeeping ----

    // Deletes day files older than the keep window whose rows were all delivered.
    // Returns how many files were removed.
    public int PurgeOldFiles()
    {
        var cutoff = DateTime.Now.Date - _keepSentFor;
        var removed = 0;

        foreach (var file in AllFiles())
        {
            using (var conn = OpenFile(file))
            using (var cmd = conn.CreateCommand())
            {
                if (DataDate(file, conn) >= cutoff)
                    continue;

                cmd.CommandText = "SELECT COUNT(*) FROM outbox WHERE sent = 0;";
                if ((long)cmd.ExecuteScalar()! > 0)
                    continue; // still has undelivered rows: never delete
                SqliteConnection.ClearPool(conn); // pooled connections keep the file open
            }

            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(Path.Combine(_dir, file + suffix));

            lock (_filesSync)
            {
                _initialised.Remove(file);
                _delivered.Remove(file);
            }
            removed++;
        }

        return removed;
    }

    // Stops taking envelopes and waits (max 10 s) until the queue is on disk.
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    // ---- files ----

    private static string DayFile(DateTime day) => $"outbox-{day:yyyy-MM-dd}.db";

    // Legacy file first, then day files oldest first.
    private List<string> AllFiles() =>
        Directory.GetFiles(_dir, "outbox*.db")
            .Select(path => Path.GetFileName(path))
            .OrderBy(f => f.Equals(LegacyFile, StringComparison.OrdinalIgnoreCase) ? "" : f, StringComparer.Ordinal)
            .ToList();

    private List<string> PendingFiles()
    {
        lock (_filesSync)
            return AllFiles().Where(f => !_delivered.Contains(f)).ToList();
    }

    // A past day's file (and the legacy file) gets no new rows, so once it has
    // no unsent rows it is done.
    private void MarkDeliveredIfPast(string file)
    {
        if (!TryDayOf(file, out var day) || day < DateTime.Now.Date)
            lock (_filesSync)
                _delivered.Add(file);
    }

    private static bool TryDayOf(string file, out DateTime day) =>
        DateTime.TryParseExact(file, "'outbox-'yyyy-MM-dd'.db'", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    // The day a file's data belongs to. Day files: from the name. Legacy
    // outbox.db: its newest row (not the file time, which acknowledging
    // rows keeps moving); empty or unreadable dates count as old.
    private static DateTime DataDate(string file, SqliteConnection conn)
    {
        if (TryDayOf(file, out var day))
            return day;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(created_at) FROM outbox;";
        return cmd.ExecuteScalar() is string newest && DateTimeOffset.TryParse(newest, out var at)
            ? at.LocalDateTime.Date
            : DateTime.MinValue;
    }

    private SqliteConnection OpenFile(string file)
    {
        var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, file)}");
        conn.Open();

        bool firstTime;
        lock (_filesSync)
            firstTime = _initialised.Add(file);

        if (firstTime)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS outbox (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id    TEXT NOT NULL UNIQUE,
                    data_group  TEXT NOT NULL,
                    body        TEXT NOT NULL,
                    sent        INTEGER NOT NULL DEFAULT 0,
                    created_at  TEXT NOT NULL,
                    sent_at     TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_outbox_unsent ON outbox (id) WHERE sent = 0;";
            cmd.ExecuteNonQuery();
        }

        return conn;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
}

// Where an outbox row lives: its day file and its id inside that file.
public readonly record struct OutboxKey(string File, long Id);
