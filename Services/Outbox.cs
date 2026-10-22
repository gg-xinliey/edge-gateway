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
//
// Machines are answered ({"ack":...}) only once their rows are committed:
// Write() takes an onSaved callback, run right after the batch's commit. The
// commit waits for the disk (synchronous = FULL), so an ack survives even a
// power cut. Each row also has send_after (UTC ms): MES gets it no earlier
// than that (the group's sendEveryMs), while it is safe on disk right away.
public sealed class Outbox : IAsyncDisposable
{
    private const int MaxBatch = 5_000;
    private const int QueueCapacity = 200_000; // ~ a minute at full load; past that Write() waits
    private const string LegacyFile = "outbox.db";

    private readonly string _dir;
    private readonly TimeSpan _keepSentFor;
    private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly record struct Item(Envelope Envelope, long SendAfterMs, Action? OnSaved, SavedState? State);
    private readonly Task _writer;

    private readonly object _filesSync = new();
    private readonly object _schemaSync = new(); // one schema creation at a time; see OpenFile
    private readonly HashSet<string> _initialised = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _delivered = new(StringComparer.OrdinalIgnoreCase); // past days with no unsent rows

    private readonly GatewayStats? _stats;

    public Outbox(string directory, int keepSentDays = 7, GatewayStats? stats = null)
    {
        _stats = stats;
        _dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(_dir);
        _keepSentFor = TimeSpan.FromDays(keepSentDays);

        // Bring every existing file up to the current columns now, so the
        // config page (which opens them read-only) can always read failed_at etc.
        foreach (var file in AllFiles())
        {
            try { OpenFile(file).Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[outbox] could not open {file}: {ex.Message}"); }
        }

        _writer = Task.Run(WriterLoopAsync);
    }

    // Envelopes waiting in memory for the writer.
    public int Queued => _queue.Reader.Count;

    // sendAfterMs: UTC unix ms before which MES must not get it (0 = right away).
    // onSaved: runs once the row is committed to disk (never, if it can't be).
    // state: the group's last values after this record (IntervalSender), saved
    // with the row so a restart carries on from them (see LatestStates).
    public void Write(Envelope envelope, long sendAfterMs = 0, Action? onSaved = null, SavedState? state = null)
    {
        _stats?.MesQueued();
        var item = new Item(envelope, sendAfterMs, onSaved, state);
        if (_queue.Writer.TryWrite(item))
            return;

        // Queue full (disk can't keep up): wait for room instead of losing data.
        _queue.Writer.WriteAsync(item).AsTask().GetAwaiter().GetResult();
    }

    // ---- writer ----

    private async Task WriterLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<Item>(MaxBatch);
        SqliteConnection? conn = null;
        var connFile = "";
        var lastErrorLog = DateTime.MinValue;

        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (batch.Count < MaxBatch && reader.TryRead(out var item))
                    batch.Add(item);

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
                            using var durable = conn.CreateCommand();
                            durable.CommandText = "PRAGMA synchronous = FULL;"; // commit = on disk: machines are answered after it
                            durable.ExecuteNonQuery();
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

                // Committed: now the senders may be told.
                foreach (var item in batch)
                {
                    try { item.OnSaved?.Invoke(); }
                    catch (Exception ex) { Console.WriteLine($"[outbox] reply after save failed: {ex.Message}"); }
                }

                batch.Clear();
            }
        }
        finally
        {
            conn?.Dispose();
        }
    }

    // INSERT OR IGNORE: event_id comes from the message_id, so a message sent
    // again after a gateway crash (its first reply never came) gives the same
    // event_id and is not stored twice in the same day file.
    private static void InsertBatch(SqliteConnection conn, List<Item> batch)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO outbox (event_id, data_group, body, sent, created_at, send_after, state_key, state)
            VALUES ($event_id, $data_group, $body, 0, $created_at, $send_after, $state_key, $state);";
        var eventId = cmd.Parameters.Add("$event_id", SqliteType.Text);
        var dataGroup = cmd.Parameters.Add("$data_group", SqliteType.Text);
        var body = cmd.Parameters.Add("$body", SqliteType.Text);
        var createdAt = cmd.Parameters.Add("$created_at", SqliteType.Text);
        var sendAfter = cmd.Parameters.Add("$send_after", SqliteType.Integer);
        var stateKey = cmd.Parameters.Add("$state_key", SqliteType.Text);
        var state = cmd.Parameters.Add("$state", SqliteType.Text);
        cmd.Prepare();

        var now = Now();
        foreach (var item in batch)
        {
            eventId.Value = item.Envelope.event_id;
            dataGroup.Value = item.Envelope.data_group;
            body.Value = JsonSerializer.Serialize(item.Envelope, EnvelopeJson.Options);
            createdAt.Value = now;
            sendAfter.Value = item.SendAfterMs;
            stateKey.Value = (object?)item.State?.Key ?? DBNull.Value;
            state.Value = item.State is null ? DBNull.Value : JsonSerializer.Serialize(item.State);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ---- reading / acknowledging (MES forwarder) ----

    // Oldest first across all day files, so a long MES outage drains in order.
    // Only rows whose send_after has come (a group's sendEveryMs); within one
    // machine + group that keeps the order, across groups the faster group goes first.
    public List<(OutboxKey Key, Envelope Envelope)> ReadUnsent(int limit = 100)
    {
        var results = new List<(OutboxKey, Envelope)>();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var file in PendingFiles())
        {
            using var conn = OpenFile(file);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, body FROM outbox WHERE sent = 0 AND failed_at IS NULL AND discarded_at IS NULL AND send_after <= $now ORDER BY id LIMIT $limit;";
            cmd.Parameters.AddWithValue("$now", nowMs);
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

            // Nothing due is not "all delivered": rows may still wait for send_after.
            if (found == 0 && !HasUnsent(conn))
                MarkDeliveredIfPast(file);
            if (results.Count >= limit)
                break;
        }

        return results;
    }

    private static bool HasUnsent(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM outbox WHERE sent = 0 AND discarded_at IS NULL);";
        return (long)cmd.ExecuteScalar()! == 1;
    }

    // MES rejected these rows as invalid: they leave the send queue (so the
    // machine's next rows go on) and wait in failed messages with MES's error,
    // until someone re-sends them (after a fix) or discards them with a reason.
    public void MarkFailed(IEnumerable<(OutboxKey Key, string Error)> rows)
    {
        var now = Now();
        foreach (var group in rows.GroupBy(r => r.Key.File))
        {
            using var conn = OpenFile(group.Key);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE outbox SET failed_at = $at, error = $error WHERE id = $id;";
            var at = cmd.Parameters.Add("$at", SqliteType.Text);
            var error = cmd.Parameters.Add("$error", SqliteType.Text);
            var id = cmd.Parameters.Add("$id", SqliteType.Integer);
            cmd.Prepare();

            at.Value = now;
            foreach (var (key, message) in group)
            {
                error.Value = message;
                id.Value = key.Id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
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

    // The newest saved state per machine + data_source + group, over all files
    // (newest file first). Read once when the gateway starts, so it carries on
    // from the last values instead of sending unchanged data again as "new".
    public Dictionary<string, SavedState> LatestStates()
    {
        var states = new Dictionary<string, SavedState>(StringComparer.Ordinal);
        foreach (var file in Enumerable.Reverse(AllFiles()))
        {
            try
            {
                using var conn = OpenFile(file);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT state_key, state FROM outbox WHERE id IN (SELECT MAX(id) FROM outbox WHERE state_key IS NOT NULL GROUP BY state_key);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    if (states.ContainsKey(key) || reader.IsDBNull(1))
                        continue; // a newer file already had it
                    var state = JsonSerializer.Deserialize<SavedState>(reader.GetString(1));
                    if (state is not null)
                        states[key] = state;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[outbox] could not read last values from {file}: {ex.Message}");
            }
        }
        return states;
    }

    public long CountUnsent()
    {
        long total = 0;
        foreach (var file in PendingFiles())
        {
            using var conn = OpenFile(file);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM outbox WHERE sent = 0 AND failed_at IS NULL AND discarded_at IS NULL;";
            var count = (long)cmd.ExecuteScalar()!;
            if (count == 0 && !HasUnsent(conn)) // failed rows can still be re-sent: not done
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

                // Undelivered rows, failed ones included, keep the file; only discarded rows don't.
                cmd.CommandText = "SELECT COUNT(*) FROM outbox WHERE sent = 0 AND discarded_at IS NULL;";
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
        if (!_writer.IsCompleted) // never acked, so the adapters send them again
            Console.WriteLine($"[outbox] {_dir}: {Queued} queued row(s) not written within 10 s (file locked or disk full?); they were not acked, the adapters send them again");
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

        // A file counts as initialised only once its schema exists: a reader that opens a
        // brand-new day file (the MES sender, just after midnight) waits here instead of
        // querying a file that has no table yet ("no such table: outbox", issue #1).
        lock (_schemaSync)
        {
            bool known;
            lock (_filesSync)
                known = _initialised.Contains(file);
            if (!known)
            {
                try { CreateSchema(conn); }
                catch { conn.Dispose(); throw; }
                lock (_filesSync)
                    _initialised.Add(file);
            }
        }

        return conn;
    }

    private static void CreateSchema(SqliteConnection conn)
    {
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
                    sent_at     TEXT,
                    send_after  INTEGER NOT NULL DEFAULT 0,
                    failed_at   TEXT,
                    error       TEXT,
                    discarded_at TEXT,
                    discard_reason TEXT,
                    state_key   TEXT,
                    state       TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_outbox_unsent ON outbox (id) WHERE sent = 0;";
            cmd.ExecuteNonQuery();

            // Files from before these columns existed: add them.
            //   send_after: 0 = send right away
            //   failed_at / error: MES rejected the row as invalid (failed messages, not retried)
            //   discarded_at / discard_reason: someone gave a failed row up, with a reason
            //   state_key / state: the group's last values after this row (restart carries on from them)
            foreach (var (column, type) in new[] { ("send_after", "INTEGER NOT NULL DEFAULT 0"), ("failed_at", "TEXT"), ("error", "TEXT"),
                                                   ("discarded_at", "TEXT"), ("discard_reason", "TEXT"), ("state_key", "TEXT"), ("state", "TEXT") })
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('outbox') WHERE name = '{column}';";
                if ((long)cmd.ExecuteScalar()! == 0)
                {
                    cmd.CommandText = $"ALTER TABLE outbox ADD COLUMN {column} {type};";
                    cmd.ExecuteNonQuery();
                }
            }
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_outbox_failed ON outbox (id) WHERE failed_at IS NOT NULL AND discarded_at IS NULL;";
            cmd.ExecuteNonQuery();
        }
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
}

// Where an outbox row lives: its day file and its id inside that file.
public readonly record struct OutboxKey(string File, long Id);

// A group's last values (before time-stamping) after a record, and the start
// of its open span (Span groups). Key: machine, data_source ("" = none sent), group.
public sealed record SavedState(string Key, Dictionary<string, JsonElement> Values, string? SpanSince)
{
    public static string KeyOf(string machineId, string dataSource, string dataGroup) => $"{machineId}\n{dataSource}\n{dataGroup}";
}
