using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Admin;

// Read-only look into the outbox for the config page's Outbox view:
//   * which days exist (one file per day) with row / waiting counts
//   * one day's envelopes, newest first, filtered by machine, group,
//     data_source and status (sent / waiting), in pages
//   * one envelope in full
// Files are opened read-only and without pooling, so the gateway's writer and
// the daily cleanup are never blocked by the page.
public static class OutboxReader
{
    private const int MaxPage = 500;
    private static readonly Regex DayPattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private const string Legacy = "legacy"; // the old single outbox.db, if still there

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "outbox");

    public static object Days()
    {
        if (!Directory.Exists(Folder))
            return Array.Empty<object>();

        return Directory.GetFiles(Folder, "outbox*.db")
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var day = name == "outbox.db" ? Legacy : name["outbox-".Length..^".db".Length];
                long rows = 0, waiting = 0;
                try
                {
                    using var conn = Open(path);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(sent = 0), 0) FROM outbox;";
                    using var r = cmd.ExecuteReader();
                    r.Read();
                    rows = r.GetInt64(0);
                    waiting = r.GetInt64(1);
                }
                catch { /* file being created */ }

                var bytes = new FileInfo(path).Length + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);
                return new { day, rows, waiting, megabytes = Math.Round(bytes / 1048576.0, 1) };
            })
            .OrderByDescending(d => d.day == Legacy ? "" : d.day)
            .ToList();
    }

    // Newest first. beforeId pages backwards; status: "sent" | "waiting" | anything else = all.
    // Machine, group and data source match without case (NXT101 = nxt101). Null when there is no file for that day.
    public static object? Query(string day, string? machine, string? group, string? dataSource, string? status, long? beforeId, int limit)
    {
        var path = FileFor(day);
        if (path is null)
            return null;

        var where = new List<string>();
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(machine)) { where.Add("json_extract(body, '$.machine_id') = $machine COLLATE NOCASE"); cmd.Parameters.AddWithValue("$machine", machine.Trim()); }
        if (!string.IsNullOrWhiteSpace(group)) { where.Add("data_group = $group COLLATE NOCASE"); cmd.Parameters.AddWithValue("$group", group.Trim()); }
        if (!string.IsNullOrWhiteSpace(dataSource)) { where.Add("json_extract(body, '$.data_source') = $source COLLATE NOCASE"); cmd.Parameters.AddWithValue("$source", dataSource.Trim()); }
        if (status == "sent") where.Add("sent = 1");
        if (status == "waiting") where.Add("sent = 0");
        if (beforeId is { } before) { where.Add("id < $before"); cmd.Parameters.AddWithValue("$before", before); }

        cmd.CommandText = $@"
            SELECT id, created_at, sent, sent_at, data_group, length(body),
                   json_extract(body, '$.machine_id'), json_extract(body, '$.data_source'),
                   substr(json_extract(body, '$.payload'), 1, 240)
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
                preview = r.IsDBNull(8) ? "" : r.GetString(8)
            });
        }

        return new { rows };
    }

    // The full envelope JSON text, or null.
    public static string? Row(string day, long id)
    {
        var path = FileFor(day);
        if (path is null)
            return null;

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT body FROM outbox WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    // Only "yyyy-MM-dd" or "legacy" map to a file: nothing else on disk can be read.
    private static string? FileFor(string day)
    {
        var name = day == Legacy ? "outbox.db" : DayPattern.IsMatch(day) ? $"outbox-{day}.db" : null;
        if (name is null)
            return null;
        var path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }
}
