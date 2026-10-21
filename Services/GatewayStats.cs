namespace EdgeGateway.Services;

// Throughput counters. Every part adds to them; GatewayHost turns them into
// per-second rates every 10 s, prints one [stats] line and keeps the latest
// for the config page (/api/status). Lives as long as the GatewayHost, so a
// restart from the page does not reset the totals.
public sealed class GatewayStats
{
    private long _received, _rejected, _duplicates, _mqttPublished, _mqttLost, _mesQueued, _mesDelivered;

    public void Received() => Interlocked.Increment(ref _received);
    public void Rejected() => Interlocked.Increment(ref _rejected);     // no machine_id / no message_id
    public void Duplicate() => Interlocked.Increment(ref _duplicates);  // message_id seen in the last 10 min
    public void MqttPublished() => Interlocked.Increment(ref _mqttPublished);
    public void MqttLost(long count = 1) => Interlocked.Add(ref _mqttLost, count);
    public void MesQueued() => Interlocked.Increment(ref _mesQueued);
    public void MesDelivered(long count) => Interlocked.Add(ref _mesDelivered, count);

    public StatsRates Latest { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, null);

    private long _lastSample = System.Diagnostics.Stopwatch.GetTimestamp();

    // Rates since the previous call; resets the counters. Divides by the time
    // that really passed: after a restart the window is longer than 10 s, and a
    // fixed 10 s showed e.g. 179.6/s instead of ~151/s.
    public StatsRates Sample(long backlog)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds);
        _lastSample = now;

        double Take(ref long counter) => Interlocked.Exchange(ref counter, 0) / seconds;

        Latest = new StatsRates(
            Take(ref _received), Take(ref _rejected), Take(ref _duplicates),
            Take(ref _mqttPublished), Take(ref _mqttLost),
            Take(ref _mesQueued), Take(ref _mesDelivered),
            backlog, DateTimeOffset.Now);
        return Latest;
    }
}

// Per-second rates over the last period, plus the outbox rows not yet delivered.
public sealed record StatsRates(
    double InPerSec, double RejectedPerSec, double DuplicatePerSec,
    double MqttPerSec, double MqttLostPerSec,
    double MesQueuedPerSec, double MesDeliveredPerSec,
    long Backlog, DateTimeOffset? At);
