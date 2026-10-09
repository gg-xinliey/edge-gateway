using System.Text.Json;
using Microsoft.Data.Sqlite;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Local, durable outbox backed by SQLite (one file, nothing to install).
// Every envelope is inserted with sent = 0 BEFORE the gateway tries to
// send it. Only an acknowledgement from MES flips it to sent = 1.
// Sent rows are tidied up after `keepSentDays` (slide 4: "outbox, 7 d").
// Unsent rows are never deleted.
public class Outbox
{
    private readonly string _connectionString;
    private readonly TimeSpan _keepSentFor;

    public Outbox(string dbPath, int keepSentDays = 7)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
        _keepSentFor = TimeSpan.FromDays(keepSentDays);

        using var conn = Open();
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
            CREATE INDEX IF NOT EXISTS ix_outbox_sent ON outbox (sent, sent_at);";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");

    public void Write(Envelope envelope)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO outbox (event_id, data_group, body, sent, created_at)
            VALUES ($event_id, $data_group, $body, 0, $created_at);";
        cmd.Parameters.AddWithValue("$event_id", envelope.event_id);
        cmd.Parameters.AddWithValue("$data_group", envelope.data_group);
        cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(envelope));
        cmd.Parameters.AddWithValue("$created_at", Now());
        cmd.ExecuteNonQuery();
    }

    // Oldest first, in batches, so a long MES outage drains in order.
    public List<(long Id, Envelope Envelope)> ReadUnsent(int limit = 100)
    {
        var results = new List<(long, Envelope)>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, body FROM outbox WHERE sent = 0 ORDER BY id LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(reader.GetString(1));
            if (envelope is not null)
                results.Add((reader.GetInt64(0), envelope));
        }

        return results;
    }

    public void MarkSent(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE outbox SET sent = 1, sent_at = $sent_at WHERE id = $id;";
        cmd.Parameters.AddWithValue("$sent_at", Now());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // Deletes only rows that MES already acknowledged AND are older than the keep window.
    public int PurgeOldSent()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM outbox WHERE sent = 1 AND sent_at < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(_keepSentFor).ToString("o"));
        return cmd.ExecuteNonQuery();
    }
}