using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Admin;

// Look into the outbox for the config page's Outbox view:
//   * which days exist (one file per day) with row / waiting / failed counts
//   * one day's envelopes, newest first, filtered by machine, group,
//     data_source and status (sent / waiting / failed / discarded), in pages
//   * one envelope in full
// and the failed messages (rows MES rejected as invalid), across all outboxes:
//   * list them with MES's error, count them (the page's alert)
//   * re-send (after a fix: back in the send queue) or discard with a reason
// Files are opened without pooling, read-only except for re-send / discard
// (a short write), so the gateway's writer and the daily cleanup are never
// blocked by the page.
public static class OutboxReader
{
    private const int MaxPage = 500;
    private static readonly Regex DayPattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private const string Legacy = "legacy"; // the old single outbox.db, if still there

    public static string Folder => Path.Combine(EdgeGateway.Services.DataFolder.Root, "outbox");

    // "" = the shared outbox (outbox/), then one per machine with its own
    // outbox folder (outbox/<machine_id>/).
    public static IReadOnlyList<string> Scopes()
    {
        var scopes = new List<string> { "" };
        if (Directory.Exists(Folder))
            scopes.AddRange(Directory.GetDirectories(Folder).Select(Path.GetFileName)
                .Where(name => EdgeGateway.Services.MachineRegistry.IsValidId(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)!);
        return scopes;
    }

    // scope: null/"" = shared, else a machine_id with its own outbox folder.
    // Only a valid machine_id that exists as a folder maps to one (no path tricks).
    private static string? ScopeFolder(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return Folder;
        if (!EdgeGateway.Services.MachineRegistry.IsValidId(scope))
            return null;
        var folder = Path.Combine(Folder, scope);
        return Directory.Exists(folder) ? folder : null;
    }

    // null = no such outbox folder. A machine that has not saved anything yet has no folder: no days.
    public static object? Days(string? scope)
    {
        if (!string.IsNullOrWhiteSpace(scope) && EdgeGateway.Services.MachineRegistry.IsValidId(scope)
            && !Directory.Exists(Path.Combine(Folder, scope)))
            return Array.Empty<object>();

        var folder = ScopeFolder(scope);
        if (folder is null)
            return null;
        if (!Directory.Exists(folder))
            return Array.Empty<object>();

        return Directory.GetFiles(folder, "outbox*.db")
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var day = name == "outbox.db" ? Legacy : name["outbox-".Length..^".db".Length];
                long rows = 0, waiting = 0, failed = 0;
                try
                {
                    using var conn = Open(path);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM({Waiting}), 0), COALESCE(SUM({Failed}), 0) FROM outbox;";
                    using var r = cmd.ExecuteReader();
                    r.Read();
                    rows = r.GetInt64(0);
                    waiting = r.GetInt64(1);
                    failed = r.GetInt64(2);
                }
                catch { /* file being created, or from before failed messages existed */ }

                var bytes = new FileInfo(path).Length + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);
                return new { day, rows, waiting, failed, megabytes = Math.Round(bytes / 1048576.0, 1) };
            })
            .OrderByDescending(d => d.day == Legacy ? "" : d.day)
            .ToList();
    }

    // Row states (the forwarder sets failed_at; the page sets discarded_at).
    private const string Waiting = "sent = 0 AND failed_at IS NULL AND discarded_at IS NULL";
    private const string Failed = "sent = 0 AND failed_at IS NOT NULL AND discarded_at IS NULL";
    private const string Discarded = "sent = 0 AND discarded_at IS NOT NULL";

    // Newest first. beforeId pages backwards; status: "sent" | "waiting" | "failed" | "discarded" | anything else = all.
    // Machine, group and data source match without case (NXT101 = nxt101). Null when there is no file for that day.
    public static object? Query(string? scope, string day, string? machine, string? group, string? dataSource, string? status, long? beforeId, int limit)
    {
        var path = FileFor(scope, day);
        if (path is null)
            return null;

        var where = new List<string>();
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(machine)) { where.Add("json_extract(body, '$.machine_id') = $machine COLLATE NOCASE"); cmd.Parameters.AddWithValue("$machine", machine.Trim()); }
        if (!string.IsNullOrWhiteSpace(group)) { where.Add("data_group = $group COLLATE NOCASE"); cmd.Parameters.AddWithValue("$group", group.Trim()); }
        if (!string.IsNullOrWhiteSpace(dataSource)) { where.Add("json_extract(body, '$.data_source') = $source COLLATE NOCASE"); cmd.Parameters.AddWithValue("$source", dataSource.Trim()); }
        if (status == "sent") where.Add("sent = 1");
        if (status == "waiting") where.Add(Waiting);
        if (status == "failed") where.Add(Failed);
        if (status == "discarded") where.Add(Discarded);
        if (beforeId is { } before) { where.Add("id < $before"); cmd.Parameters.AddWithValue("$before", before); }

        cmd.CommandText = $@"
            SELECT id, created_at, sent, sent_at, data_group, length(body),
                   json_extract(body, '$.machine_id'), json_extract(body, '$.data_source'),
                   substr(json_extract(body, '$.payload'), 1, 240),
                   failed_at, error, discarded_at, discard_reason
            FROM outbox {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxPage));

        var rows = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new
            {
                id = r.GetInt64(0),
                createdAt = r.GetString(1),
                sent = r.GetInt64(2) == 1,
                sentAt = r.IsDBNull(3) ? null : r.GetString(3),
                group = r.GetString(4),
                size = r.GetInt64(5),
                machineId = r.IsDBNull(6) ? null : r.GetString(6),
                dataSource = r.IsDBNull(7) ? null : r.GetString(7),
                preview = r.IsDBNull(8) ? "" : r.GetString(8),
                failedAt = r.IsDBNull(9) ? null : r.GetString(9),
                error = r.IsDBNull(10) ? null : r.GetString(10),
                discardedAt = r.IsDBNull(11) ? null : r.GetString(11),
                discardReason = r.IsDBNull(12) ? null : r.GetString(12)
            });
        }

        return new { rows };
    }

    // ---- failed messages (MES rejected them as invalid) ----

    // Every outbox file of every scope: (scope, day, path).
    private static IEnumerable<(string Scope, string Day, string Path)> AllFiles()
    {
        foreach (var scope in Scopes())
        {
            var folder = ScopeFolder(scope);
            if (folder is null || !Directory.Exists(folder))
                continue;
            foreach (var path in Directory.GetFiles(folder, "outbox*.db"))
            {
                var name = Path.GetFileName(path);
                yield return (scope, name == "outbox.db" ? Legacy : name["outbox-".Length..^".db".Length], path);
            }
        }
    }

    // How many failed messages wait for someone (re-send or discard). For the page's alert.
    public static long CountFailed()
    {
        long total = 0;
        foreach (var (_, _, path) in AllFiles())
        {
            try
            {
                using var conn = Open(path);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM outbox WHERE {Failed};";
                total += (long)cmd.ExecuteScalar()!;
            }
            catch { /* file being created, or from before failed messages existed */ }
        }
        return total;
    }

    // Oldest first, up to `limit` across all outboxes, with MES's error.
    public static object ListFailed(int limit)
    {
        limit = Math.Clamp(limit, 1, MaxPage);
        var rows = new List<object>();
        long total = 0;
        foreach (var (scope, day, path) in AllFiles().OrderBy(f => f.Day == Legacy ? "" : f.Day, StringComparer.Ordinal))
        {
            try
            {
                using var conn = Open(path);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT id, event_id, data_group, created_at, failed_at, error,
                           json_extract(body, '$.machine_id'), json_extract(body, '$.data_source'),
                           json_extract(body, '$.event_time'), substr(json_extract(body, '$.payload'), 1, 240)
                    FROM outbox WHERE {Failed} ORDER BY id;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    total++;
                    if (rows.Count >= limit)
                        continue; // keep counting
                    rows.Add(new
                    {
                        scope, day,
                        id = r.GetInt64(0),
                        eventId = r.GetString(1),
                        group = r.GetString(2),
                        createdAt = r.GetString(3),
                        failedAt = r.GetString(4),
                        error = r.IsDBNull(5) ? "" : r.GetString(5),
                        machineId = r.IsDBNull(6) ? null : r.GetString(6),
                        dataSource = r.IsDBNull(7) ? null : r.GetString(7),
                        eventTime = r.IsDBNull(8) ? null : r.GetString(8),
                        preview = r.IsDBNull(9) ? "" : r.GetString(9)
                    });
                }
            }
            catch { /* file being created, or from before failed messages existed */ }
        }
        return new { total, rows };
    }

    public readonly record struct FailedRef(string? Scope, string Day, long Id);

    // Back into the send queue (MES side fixed, or the mapping fixed and the
    // row is still fine): the forwarder sends it on its next pass. The last
    // error stays on the row until it is delivered or fails again.
    // all = every failed message. Returns how many rows were put back.
    public static int Resend(IReadOnlyCollection<FailedRef> rows, bool all) =>
        Change(rows, all, "failed_at = NULL", reason: null);

    // Given up, with a reason (required): never sent; kept in its day file
    // (with the reason) until the file is cleaned up. Returns how many rows.
    public static int Discard(IReadOnlyCollection<FailedRef> rows, bool all, string reason) =>
        Change(rows, all, "discarded_at = $at, discard_reason = $reason", reason);

    private static int Change(IReadOnlyCollection<FailedRef> rows, bool all, string set, string? reason)
    {
        var targets = all
            ? AllFiles().Select(f => (f.Path, Ids: (List<long>?)null))
            : rows.GroupBy(row => FileFor(row.Scope, row.Day))
                  .Where(g => g.Key is not null)
                  .Select(g => (Path: g.Key!, Ids: (List<long>?)g.Select(row => row.Id).ToList()));

        var changed = 0;
        var at = DateTimeOffset.UtcNow.ToString("o");
        foreach (var (path, ids) in targets)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
                conn.Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("$at", at);
                cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                if (ids is null)
                {
                    cmd.CommandText = $"UPDATE outbox SET {set} WHERE {Failed};";
                    changed += cmd.ExecuteNonQuery();
                }
                else
                {
                    cmd.CommandText = $"UPDATE outbox SET {set} WHERE id = $id AND {Failed};"; // only rows still failed
                    var id = cmd.Parameters.Add("$id", SqliteType.Integer);
                    foreach (var rowId in ids)
                    {
                        id.Value = rowId;
                        changed += cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            catch (SqliteException) when (all) { /* file from before failed messages existed: nothing failed there */ }
        }
        return changed;
    }

    // The full envelope JSON text, or null.
    public static string? Row(string? scope, string day, long id)
    {
        var path = FileFor(scope, day);
        if (path is null)
            return null;

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT body FROM outbox WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    // Only "yyyy-MM-dd" or "legacy" map to a file: nothing else on disk can be read.
    private static string? FileFor(string? scope, string day)
    {
        var folder = ScopeFolder(scope);
        var name = day == Legacy ? "outbox.db" : DayPattern.IsMatch(day) ? $"outbox-{day}.db" : null;
        if (folder is null || name is null)
            return null;
        var path = Path.Combine(folder, name);
        return File.Exists(path) ? path : null;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }
}
