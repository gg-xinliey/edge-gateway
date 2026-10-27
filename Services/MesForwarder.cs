using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// At-least-once delivery to MES. Retries anything the outbox still
// has marked unsent, until MES takes it or rejects it as invalid.
//
// Built for many machines:
//   * Each pass reads up to `ChunkSize` unsent envelopes (oldest first) and
//     sends them grouped per machine: up to `Parallel` machines at the same
//     time, but each machine's envelopes strictly in order. A machine stops
//     at its first failure, so a later envelope never overtakes an earlier one.
//   * While there is a backlog it goes straight to the next pass; when idle
//     it checks again after `IdleDelay`.
//   * MES unreachable, timing out (15 s), 5xx, 401/403/404/405/407, 408 or 429: the
//     rows stay and are retried, with a growing delay (2 s, 4 s, 8 s ... 60 s max) so
//     a down MES is not hammered; back to 2 s after MES takes something again.
//     404 / 405 mean a wrong mesUrl path or MES being redeployed, not a bad row (SVI-98).
//   * MES rejects a row as invalid (any other 4xx): it is moved to failed
//     messages with MES's answer (not retried, nothing lost), the page alerts,
//     and the machine's next rows go on. Re-send it after a fix, or discard it
//     with a reason, on the config page.
//   * MES answers 500 (not 502/503/504: down or overloaded) for the same row
//     ServerErrorTries times in a row while it takes other records: MES fails on
//     that row. It is moved to failed messages too, so the machine's next rows go
//     on (SVI-97). While MES takes nothing, rows only wait.
public class MesForwarder
{
    private const int ChunkSize = 2_000;
    private const int Parallel = 16;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private const int MaxErrorText = 1_000;
    private const int ServerErrorTries = 5;

    // Rows MES answered 500 for, by event_id: tries in a row and when the first was (UTC ticks).
    private readonly ConcurrentDictionary<string, (int Tries, long FirstTicks)> _serverErrors = new();

    private enum SendResult { Delivered, Retry, Rejected }

    private TimeSpan _retryDelay = FirstRetryDelay;

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
    private readonly string _tag;       // "[forwarder]" or "[forwarder NXT101]"
    private readonly string _outboxTag; // "[outbox]" or "[outbox NXT101]"

    // label: the machine_id of the machine's own pipeline; null for the shared one.
    public MesForwarder(HttpClient http, string mesUrl, Outbox outbox, GatewayStats? stats = null, string? label = null)
    {
        _http = http;
        _endpoint = new Uri(mesUrl);
        _outbox = outbox;
        _stats = stats;
        _tag = label is null ? "[forwarder]" : $"[forwarder {label}]";
        _outboxTag = label is null ? "[outbox]" : $"[outbox {label}]";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PassAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // An outbox error (file locked past the busy timeout, disk full ...) must not
                // stop the gateway: rows stay on disk and are sent on a later pass (issue #1).
                _problems.Write("outbox", $"{_tag} outbox error, trying again in {FirstRetryDelay.TotalSeconds:0} s: {ex.Message}");
                await Task.Delay(FirstRetryDelay, ct);
            }
        }
    }

    // One pass: read due rows, send them per machine in order, mark them, then wait as needed.
    private async Task PassAsync(CancellationToken ct)
    {
        {
            var chunk = _outbox.ReadUnsent(ChunkSize);
            var delivered = new ConcurrentBag<OutboxKey>();
            var rejected = new ConcurrentBag<(OutboxKey Key, string Error)>();
            var retrying = 0;

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
                            var (result, error) = await SendAsync(envelope, token);
                            if (result == SendResult.Retry)
                            {
                                Interlocked.Increment(ref retrying);
                                break; // keep this machine's order: retry from here next pass
                            }
                            _serverErrors.TryRemove(envelope.event_id, out _);
                            if (result == SendResult.Rejected)
                                rejected.Add((key, error!)); // set aside, the machine goes on
                            else
                                delivered.Add(key);
                        }
                    });

                if (!delivered.IsEmpty)
                {
                    _outbox.MarkSent(delivered); // one transaction per file for the whole pass
                    _stats?.MesDelivered(delivered.Count);
                    ReportRecovery(delivered.Count);
                }

                if (!rejected.IsEmpty)
                    _outbox.MarkFailed(rejected);
            }

            if (DateTimeOffset.UtcNow >= _nextMaintenance)
            {
                MaintainOutbox();
                _nextMaintenance = DateTimeOffset.UtcNow + MaintenanceInterval;
            }

            if (chunk.Count == 0)
            {
                await Task.Delay(IdleDelay, ct);           // nothing to send
            }
            else if (retrying > 0 && delivered.IsEmpty && rejected.IsEmpty)
            {
                // MES took nothing: wait, and longer each time it keeps failing.
                await Task.Delay(_retryDelay, ct);
                _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
            }
            else
            {
                _retryDelay = FirstRetryDelay;             // MES answered: back to the short delay
                if (retrying > 0)
                    await Task.Delay(FirstRetryDelay, ct); // some machines still failing: don't spin on them
                // else: backlog, go again right away
            }
        }
    }

    // Delivered = MES accepted it (2xx). Rejected = MES says it is invalid
    // (a 4xx other than auth / timeout / too many requests): error holds MES's answer.
    // Retry = anything else (unreachable, timeout, 5xx, ...).
    private async Task<(SendResult Result, string? Error)> SendAsync(Envelope envelope, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(envelope, EnvelopeJson.Options);
            using var response = await _http.PostAsync(
                _endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (response.IsSuccessStatusCode)
                return (SendResult.Delivered, null);

            var status = (int)response.StatusCode;
            if (status == 409) // Conflict: MES already has this event_id (a resend): it is delivered
                return (SendResult.Delivered, null);

            var badRow = status is >= 400 and < 500 and not (401 or 403 or 404 or 405 or 407 or 408 or 429);
            var failsOnRow = status is >= 500 and not (502 or 503 or 504) && FailsOnThisRow(envelope.event_id);
            if (badRow || failsOnRow)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (body.Length > MaxErrorText)
                    body = body[..MaxErrorText] + "…";
                var error = (failsOnRow
                    ? $"MES {status} {response.ReasonPhrase} {ServerErrorTries} times in a row for this record while it took other records: {body}"
                    : $"MES {status} {response.ReasonPhrase}: {body}").Trim();
                Console.WriteLine($"{_tag} MES rejected {envelope.event_id} ({envelope.machine_id}/{envelope.data_group}) " +
                                  $"{(failsOnRow ? "again and again" : "as invalid")}, moved to failed messages: {error}");
                return (SendResult.Rejected, error);
            }

            _problems.Write($"status{status}",
                $"{_tag} MES returned {status} for {envelope.event_id} ({envelope.machine_id}/{envelope.data_group}) — will retry in {_retryDelay.TotalSeconds:0} s or more" +
                (status is 404 or 405 ? " (check mesUrl: MES does not know this address)" : ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _problems.Write("send", $"{_tag} send failed for {envelope.event_id}: {ex.Message} — will retry in {_retryDelay.TotalSeconds:0} s or more");
        }

        // Remember when the trouble started, for the "reachable again" line.
        Interlocked.CompareExchange(ref _downSinceTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        Interlocked.Increment(ref _triesWhileDown);
        return (SendResult.Retry, null);
    }

    // One more 500 for this row. True once it had ServerErrorTries of them and MES took a
    // record (any machine) after the first: then MES is up and fails on this row itself.
    // Without stats (no view of the other machines) it never counts as the row's fault.
    private bool FailsOnThisRow(string eventId)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var entry = _serverErrors.AddOrUpdate(eventId, _ => (1, now), (_, old) => (old.Tries + 1, old.FirstTicks));
        if (entry.Tries < ServerErrorTries || _stats is null || _stats.LastDeliveredTicks <= entry.FirstTicks)
            return false;
        _serverErrors.TryRemove(eventId, out _);
        return true;
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
        Console.WriteLine($"{_tag} MES reachable again after {seconds:0} s ({tries} tries) — " +
                          $"{deliveredNow:N0} waiting rows delivered" +
                          (left > 0 ? $", {left:N0} still to go" : ""));
    }

    private void MaintainOutbox()
    {
        try
        {
            var removed = _outbox.PurgeOldFiles();
            if (removed > 0)
                Console.WriteLine($"{_outboxTag} removed {removed} day file(s) past the keep window, all rows delivered");

            var unsent = _outbox.CountUnsent();
            if (unsent > BacklogWarning)
                Console.WriteLine($"{_outboxTag} warning: {unsent} rows still not delivered to MES — check the MES connection");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{_outboxTag} maintenance failed: {ex.Message}");
        }
    }
}
