using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// At-least-once delivery to MES. Retries anything the outbox still
// has marked unsent, until MES takes it or rejects it as invalid.
//
// Built for many machines, and for a MES with one API per data group:
//   * A group = machine + data_source + data group (the outbox's state_key). Each group
//     goes to its own MES address (mesUrls[data group], else mesUrl); a group with no
//     address yet stays in the outbox, not read, until it gets one (Restart Gateway).
//   * Each pass reads up to `ChunkSize` unsent envelopes (oldest first) and starts one
//     sender per group: up to `Parallel` at the same time, each sending its group's
//     envelopes strictly in order and marking them itself. A group stops at its first
//     failure, so a later envelope never overtakes an earlier one. The pass doesn't wait
//     for the senders, so one MES API being slow, down or not opened yet doesn't hold up
//     the other groups.
//   * The next pass runs as soon as a sender is done (a backlog drains right away), else
//     after `IdleDelay`.
//   * MES unreachable, timing out (15 s), 5xx, 401/403/404/405/407, 408, 413 or 429: the
//     group's rows stay and are retried, with a growing delay per group (2 s, 4 s, 8 s ...
//     60 s max) so a down API is not hammered; back to 2 s once that group gets through.
//     404 / 405 mean a wrong mesUrl path or MES being redeployed, not a bad row (SVI-98).
//     413 may be a proxy / MES size limit set too low: retried too. A row that really is too
//     large is caught by the stuck-row rule below, like a 500.
//     shortcut: 413 retried until the real MES status codes are known (SVI-53), then decide again.
//   * MES rejects a row as invalid (any other 4xx): it is moved to failed
//     messages with MES's answer (not retried, nothing lost), the page alerts,
//     and the group's next rows go on. Re-send it after a fix, or discard it
//     with a reason, on the config page.
//   * MES answers 500 (not 502/503/504: down or overloaded) or 413 for the same row
//     mesStuckRowTries times in a row (5), and either took other records meanwhile or
//     kept at it for mesStuckRowMinutes (10 min): MES fails on that row. It is moved to failed
//     messages too (alert on the page), so the group's next rows go on (SVI-97). "Took others"
//     counts only records taken at the same address: another API being up says nothing about this one.
//     The time limit covers a machine alone in its own outbox: nothing else is
//     delivered there, so "MES took others" never comes.
public class MesForwarder
{
    private const int ChunkSize = 2_000;
    private const int Parallel = 16;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private const int MaxErrorText = 1_000;
    private const int DefaultStuckTries = 5;
    private static readonly TimeSpan DefaultStuckLimit = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _stuckLimit;
    private readonly int _stuckTries;

    // Rows MES answered 500 for, by event_id: tries in a row and when the first was (UTC ticks).
    private readonly ConcurrentDictionary<string, (int Tries, long FirstTicks)> _serverErrors = new();

    private enum SendResult { Delivered, Retry, Rejected }

    // Groups (state_key) whose last send failed: when to try again, and the wait after the next failure.
    private readonly ConcurrentDictionary<string, (long NextTicks, TimeSpan NextDelay)> _waiting = new();

    // MES addresses per data group (null = none yet), and the addresses failing now: since when, tries.
    private readonly Func<string, string?> _urlFor;
    private readonly Func<string, MesApiLogin>? _loginFor;
    private readonly ConcurrentDictionary<string, Uri?> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Uri, (long SinceTicks, long Tries)> _down = new();

    // Outbox housekeeping: hourly, remove day files past the keep window that are fully delivered.
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);

    // More unsent rows than this means MES has been unreachable for a long time.
    private const long BacklogWarning = 10_000;

    private readonly HttpClient _http;
    private readonly Outbox _outbox;
    private readonly ThrottledLog _problems = new(TimeSpan.FromSeconds(60));
    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue; // first pass runs right away

    private readonly GatewayStats? _stats;
    private readonly string _tag;       // "[forwarder]" or "[forwarder NXT101]"
    private readonly string _outboxTag; // "[outbox]" or "[outbox NXT101]"

    // One MES address for every data group.
    public MesForwarder(HttpClient http, string mesUrl, Outbox outbox, GatewayStats? stats = null, string? label = null,
        TimeSpan? stuckLimit = null, int? stuckTries = null)
        : this(http, _ => mesUrl, outbox, stats, label, stuckLimit, stuckTries) { }

    // urlFor: data group -> its MES address, null or empty = none yet (MachineConfig.MesUrlFor).
    // loginFor: data group -> its MES login (MachineConfig.MesLoginFor); null = no login.
    // label: the machine_id of the machine's own pipeline; null for the shared one.
    // stuckLimit / stuckTries: the stuck-row rule above (machine-config mesStuckRowMinutes / mesStuckRowTries).
    public MesForwarder(HttpClient http, Func<string, string?> urlFor, Outbox outbox, GatewayStats? stats = null, string? label = null,
        TimeSpan? stuckLimit = null, int? stuckTries = null, Func<string, MesApiLogin>? loginFor = null)
    {
        _loginFor = loginFor;
        _stuckLimit = stuckLimit ?? DefaultStuckLimit;
        _stuckTries = stuckTries ?? DefaultStuckTries;
        _http = http;
        _urlFor = urlFor;
        _outbox = outbox;
        _stats = stats;
        _tag = label is null ? "[forwarder]" : $"[forwarder {label}]";
        _outboxTag = label is null ? "[outbox]" : $"[outbox {label}]";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
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
        finally
        {
            await Task.WhenAll(_sending.Values); // they stop on ct; the outbox is closed after this
        }
    }

    // Groups being sent right now (each marks its own rows), by state_key.
    private readonly ConcurrentDictionary<string, Task> _sending = new();

    // One pass: read due rows (not of groups being sent, waiting to retry or without a MES
    // address) and start each group's sender. The pass doesn't wait for them: a slow or down
    // API (up to the 15 s timeout) must not hold up the other groups' next rows.
    private async Task PassAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var chunk = _outbox.ReadUnsent(ChunkSize, group => group is not null
            && (_sending.ContainsKey(group) || IsWaiting(group, now) || EndpointFor(DataGroupOf(group)) is null));

        // Rows from before state_key existed (null) go per machine, as they always did.
        foreach (var rows in chunk.GroupBy(row => row.Group ?? row.Envelope.machine_id)) // keeps oldest-first order inside each group
        {
            if (_sending.Count >= Parallel)
                break;
            // An old row's group (no state_key) can't be skipped while it is read; nor its data group without an address.
            if (_sending.ContainsKey(rows.Key) || EndpointFor(rows.First().Envelope.data_group) is null)
                continue;
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _sending[rows.Key] = SendGroupAsync(rows.Key, rows.ToList(), go.Task, ct);
            go.SetResult(); // only now: a sender done before it was listed would stay listed for good
        }

        if (DateTimeOffset.UtcNow >= _nextMaintenance)
        {
            MaintainOutbox();
            _nextMaintenance = DateTimeOffset.UtcNow + MaintenanceInterval;
        }

        // Next pass when a group is done (it may have more rows), or after IdleDelay for new rows.
        await Task.WhenAny(_sending.Values.Append(Task.Delay(IdleDelay, ct)));
        ct.ThrowIfCancellationRequested();
    }

    // One group's rows in order, up to the first that must be retried; then marks them and
    // leaves _sending (in that order, so the next read never sees them unmarked).
    private async Task SendGroupAsync(string group, List<(OutboxKey Key, Envelope Envelope, string? Group)> rows, Task listed, CancellationToken ct)
    {
        await listed;
        var delivered = new List<OutboxKey>();
        var rejected = new List<(OutboxKey Key, string Error)>();
        Uri? recoveredAt = null;
        (long SinceTicks, long Tries) down = default;
        try
        {
            foreach (var (key, envelope, _) in rows)
            {
                var endpoint = EndpointFor(envelope.data_group);
                if (endpoint is null)
                    break; // an old row of a data group with no address yet: it waits, in order
                var (result, error) = await SendAsync(endpoint, envelope, group, ct);
                if (result == SendResult.Retry)
                    break; // keep this group's order: retry from here once its wait is over
                _waiting.TryRemove(group, out _);
                _serverErrors.TryRemove(envelope.event_id, out _);
                if (result == SendResult.Rejected)
                {
                    rejected.Add((key, error!)); // set aside, the group goes on
                    continue;
                }
                delivered.Add(key);
                _stats?.MesDeliveredTo(endpoint);
                if (_down.TryRemove(endpoint, out var wasDown))
                    (recoveredAt, down) = (endpoint, wasDown);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping: mark what MES already took
        }
        finally
        {
            try
            {
                if (delivered.Count > 0)
                {
                    _outbox.MarkSent(delivered); // one transaction per file
                    _stats?.MesDelivered(delivered.Count);
                    if (recoveredAt is not null)
                        ReportRecovery(recoveredAt, down, delivered.Count);
                }
                if (rejected.Count > 0)
                    _outbox.MarkFailed(rejected);
            }
            catch (Exception ex)
            {
                // Not marked: sent again later, MES answers 409 for those it already has.
                _problems.Write("outbox", $"{_tag} outbox error, trying again in {FirstRetryDelay.TotalSeconds:0} s: {ex.Message}");
            }
            finally
            {
                _sending.TryRemove(group, out _);
            }
        }
    }

    private bool IsWaiting(string group, long nowTicks) => _waiting.TryGetValue(group, out var w) && w.NextTicks > nowTicks;

    // state_key is "machine\ndata_source\ndata group" (SavedState.KeyOf).
    private static string DataGroupOf(string group) => group[(group.LastIndexOf('\n') + 1)..];

    // The data group's MES address, null = none yet (its rows wait in the outbox). Read once per run.
    private Uri? EndpointFor(string dataGroup) => _endpoints.GetOrAdd(dataGroup, g =>
    {
        var url = _urlFor(g)?.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri;
        Console.WriteLine(string.IsNullOrEmpty(url)
            ? $"{_tag} {g} has no MES address (mesUrls / mesUrl): its records wait in the outbox until it has one (Restart Gateway)"
            : $"{_tag} {g}: MES address '{url}' is not an http(s) address: its records wait in the outbox until it is fixed (Restart Gateway)");
        return null;
    });

    // The group waits before its next try: 2 s, then twice as long each time it fails again (60 s max).
    private TimeSpan WaitBeforeRetry(string group)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        static TimeSpan Twice(TimeSpan delay) => TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        var entry = _waiting.AddOrUpdate(group,
            _ => (now + FirstRetryDelay.Ticks, Twice(FirstRetryDelay)),
            (_, old) => (now + old.NextDelay.Ticks, Twice(old.NextDelay)));
        return TimeSpan.FromTicks(entry.NextTicks - now);
    }

    // Delivered = MES accepted it (2xx). Rejected = MES says it is invalid
    // (a 4xx other than auth / timeout / too many requests): error holds MES's answer.
    // Retry = anything else (unreachable, timeout, 5xx, ...): the group waits (WaitBeforeRetry).
    private async Task<(SendResult Result, string? Error)> SendAsync(Uri endpoint, Envelope envelope, string group, CancellationToken ct)
    {
        string problemKey, problem;
        try
        {
            var json = JsonSerializer.Serialize(envelope, EnvelopeJson.Options);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            _loginFor?.Invoke(envelope.data_group).Apply(request.Headers);
            using var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return (SendResult.Delivered, null);

            var status = (int)response.StatusCode;
            if (status == 409) // Conflict: MES already has this event_id (a resend): it is delivered
                return (SendResult.Delivered, null);

            var badRow = status is >= 400 and < 500 and not (401 or 403 or 404 or 405 or 407 or 408 or 413 or 429);
            var failsOnRow = status is 413 or (>= 500 and not (502 or 503 or 504)) ? FailsOnThisRow(envelope.event_id, endpoint) : null;
            if (badRow || failsOnRow is not null)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (body.Length > MaxErrorText)
                    body = body[..MaxErrorText] + "…";
                var error = (failsOnRow is not null
                    ? $"MES {status} {response.ReasonPhrase} {failsOnRow}: {body}"
                    : $"MES {status} {response.ReasonPhrase}: {body}").Trim();
                Console.WriteLine($"{_tag} MES rejected {envelope.event_id} ({envelope.machine_id}/{envelope.data_group}) " +
                                  $"{(failsOnRow is not null ? "again and again" : "as invalid")}, moved to failed messages: {error}");
                return (SendResult.Rejected, error);
            }

            problemKey = $"status{status} {endpoint}";
            problem = $"{_tag} MES returned {status} for {envelope.event_id} ({envelope.machine_id}/{envelope.data_group}) at {endpoint}" +
                (status is 404 or 405 ? $" (check the MES address for {envelope.data_group}, mesUrls / mesUrl: MES does not know it)"
                    : status is 401 or 403 ? $" (check the MES login for {envelope.data_group}, mesLogins / mesLogin: MES refused it)"
                    : status is 413 ? " (request too large: check the MES / proxy size limit)" : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            problemKey = $"send {endpoint}";
            problem = $"{_tag} send failed for {envelope.event_id} ({envelope.data_group}) to {endpoint}: {MesHttp.Why(ex)}";
        }

        var wait = WaitBeforeRetry(group);
        _problems.Write(problemKey, $"{problem} — will retry in {wait.TotalSeconds:0} s or more");
        // Remember when the trouble at this address started, for the "reachable again" line.
        _down.AddOrUpdate(endpoint, _ => (DateTimeOffset.UtcNow.UtcTicks, 1), (_, d) => (d.SinceTicks, d.Tries + 1));
        return (SendResult.Retry, null);
    }

    // One more 500 (or 413) for this row. Returns why it counts as the row's fault (for the failed
    // message), or null to retry: needs _stuckTries of them, and MES took a record at the same
    // address (any machine) after the first, or the 500s went on for the stuck limit.
    private string? FailsOnThisRow(string eventId, Uri endpoint)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var entry = _serverErrors.AddOrUpdate(eventId, _ => (1, now), (_, old) => (old.Tries + 1, old.FirstTicks));
        if (entry.Tries < _stuckTries)
            return null;
        var why = _stats is not null && _stats.LastDeliveredTicksTo(endpoint) > entry.FirstTicks
            ? $"{entry.Tries} times in a row for this record while it took other records"
            : now - entry.FirstTicks >= _stuckLimit.Ticks
                ? $"{entry.Tries} times in a row for this record over {TimeSpan.FromTicks(now - entry.FirstTicks).TotalMinutes:0.#} min"
                : null;
        if (why is not null)
            _serverErrors.TryRemove(eventId, out _);
        return why;
    }

    // First delivery to an address after a period of failed sends there: one line saying it is over.
    // (The failures themselves are logged only once a minute, so without this
    // nobody can tell from the log when MES came back.)
    // deliveredNow = rows this pass delivered there (it can take the
    // whole backlog at once, so counting what is left afterwards said "0").
    private void ReportRecovery(Uri endpoint, (long SinceTicks, long Tries) down, int deliveredNow)
    {
        var seconds = (DateTimeOffset.UtcNow.UtcTicks - down.SinceTicks) / (double)TimeSpan.TicksPerSecond;
        long left;
        try { left = _outbox.CountUnsent(); } catch { left = -1; }

        // Worded without the log's problem words: this is the good news.
        Console.WriteLine($"{_tag} MES reachable again after {seconds:0} s ({down.Tries} tries) at {endpoint} — " +
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
