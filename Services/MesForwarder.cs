using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// At-least-once delivery to MES. Retries anything the outbox still
// has marked unsent, forever.
//
// Built for many machines:
//   * Each pass reads up to `ChunkSize` unsent envelopes (oldest first) and
//     sends them grouped per machine: up to `Parallel` machines at the same
//     time, but each machine's envelopes strictly in order. A machine stops
//     at its first failure, so a later envelope never overtakes an earlier one.
//   * While there is a backlog it goes straight to the next pass; when idle
//     it checks again after `IdleDelay`. After a pass where MES accepted
//     nothing, it waits `RetryDelay` so a down MES is not hammered.
public class MesForwarder
{
    private const int ChunkSize = 2_000;
    private const int Parallel = 16;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    // Outbox housekeeping: hourly, remove day files past the keep window that are fully delivered.
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);

    // More unsent rows than this means MES has been unreachable for a long time.
    private const long BacklogWarning = 10_000;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Outbox _outbox;
    private readonly ThrottledLog _problems = new(TimeSpan.FromSeconds(60));
    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue; // first pass runs right away
    private long _downSinceTicks;   // 0 = MES fine; else UTC ticks of the first failed send
    private long _triesWhileDown;

    private readonly GatewayStats? _stats;

    public MesForwarder(HttpClient http, string mesUrl, Outbox outbox, GatewayStats? stats = null)
    {
        _http = http;
        _endpoint = new Uri(mesUrl);
        _outbox = outbox;
        _stats = stats;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var chunk = _outbox.ReadUnsent(ChunkSize);
            var delivered = new ConcurrentBag<OutboxKey>();

            if (chunk.Count > 0)
            {
                var perMachine = chunk.GroupBy(row => row.Envelope.machine_id); // keeps oldest-first order inside each group
                await System.Threading.Tasks.Parallel.ForEachAsync(
                    perMachine,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct },
                    async (rows, token) =>
                    {
                        foreach (var (key, envelope) in rows)
                        {
                            if (!await SendAsync(envelope, token))
                                break; // keep this machine's order: retry from here next pass
                            delivered.Add(key);
                        }
                    });

                if (!delivered.IsEmpty)
                {
                    _outbox.MarkSent(delivered); // one transaction per file for the whole pass
                    _stats?.MesDelivered(delivered.Count);
                    ReportRecovery(delivered.Count);
                }
            }

            if (DateTimeOffset.UtcNow >= _nextMaintenance)
            {
                MaintainOutbox();
                _nextMaintenance = DateTimeOffset.UtcNow + MaintenanceInterval;
            }

            if (chunk.Count == 0)
                await Task.Delay(IdleDelay, ct);           // nothing to send
            else if (delivered.IsEmpty)
                await Task.Delay(RetryDelay, ct);          // MES took nothing: don't hammer it
            // else: backlog, go again right away
        }
    }

    // true = MES accepted it (200 or 207).
    private async Task<bool> SendAsync(Envelope envelope, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(envelope, EnvelopeJson.Options);
            using var response = await _http.PostAsync(
                _endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (response.IsSuccessStatusCode)
                return true;

            _problems.Write($"status{(int)response.StatusCode}",
                $"[forwarder] MES returned {(int)response.StatusCode} for {envelope.event_id} ({envelope.machine_id}/{envelope.data_group}) — will retry");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _problems.Write("send", $"[forwarder] send failed for {envelope.event_id}: {ex.Message} — will retry");
        }

        // Remember when the trouble started, for the "reachable again" line.
        Interlocked.CompareExchange(ref _downSinceTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        Interlocked.Increment(ref _triesWhileDown);
        return false;
    }

    // First delivery after a period of failed sends: one line saying it is over.
    // (The failures themselves are logged only once a minute, so without this
    // nobody can tell from the log when MES came back.)
    // deliveredNow = rows the first successful pass delivered (it can take the
    // whole backlog at once, so counting what is left afterwards said "0").
    private void ReportRecovery(int deliveredNow)
    {
        var since = Interlocked.Exchange(ref _downSinceTicks, 0);
        if (since == 0)
            return;

        var tries = Interlocked.Exchange(ref _triesWhileDown, 0);
        var seconds = (DateTimeOffset.UtcNow.UtcTicks - since) / (double)TimeSpan.TicksPerSecond;
        long left;
        try { left = _outbox.CountUnsent(); } catch { left = -1; }

        // Worded without the log's problem words: this is the good news.
        Console.WriteLine($"[forwarder] MES reachable again after {seconds:0} s ({tries} tries) — " +
                          $"{deliveredNow:N0} waiting rows delivered" +
                          (left > 0 ? $", {left:N0} still to go" : ""));
    }

    private void MaintainOutbox()
    {
        try
        {
            var removed = _outbox.PurgeOldFiles();
            if (removed > 0)
                Console.WriteLine($"[outbox] removed {removed} day file(s) past the keep window, all rows delivered");

            var unsent = _outbox.CountUnsent();
            if (unsent > BacklogWarning)
                Console.WriteLine($"[outbox] warning: {unsent} rows still not delivered to MES — check the MES connection");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[outbox] maintenance failed: {ex.Message}");
        }
    }
}
