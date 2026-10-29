using System.Collections.Concurrent;
using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Turns MES messages into records, and paces them per data group.
//
// For each machine + data_source + data group it keeps the latest values (the
// snapshot). Different data_sources never mix: the same group from two sources
// gives two separate envelopes, even when both arrive at the same time.
//   * A message that changes nothing is ignored.
//   * A message that changes something becomes one record: the full group
//     snapshot after the change, time-stamped (TimeStamper) with the machine's
//     own time. It goes into the outbox file RIGHT AWAY, so the machine can be
//     answered ({"ack":...}) within milliseconds and nothing accepted is lost
//     in a crash.
//   * sendEveryMs (timestamp-rules.json, read live) only paces MES: each record
//     gets a send_after time. The first change after a quiet interval may go at
//     once; changes within the next sendEveryMs wait for that time and then all
//     go, oldest first. So every change reaches MES, at most one interval late.
//   * Nothing changes, nothing is sent: no periodic repeats. (A 60 s
//     "full snapshot" used to repeat unchanged data with a new event_id and
//     a new record_timestamp, which MES could count as new events.)
//   * The snapshot is saved with every record (outbox state column), and read
//     back after a restart or crash: the first message afterwards is compared
//     with the last values saved, so unchanged data is not sent to MES again,
//     and an open span keeps its start time.
public sealed class IntervalSender
{
    public const int DefaultSendEveryMs = 1000;

    private readonly Outbox _outbox;
    private readonly EnvelopeBuilder _envelopes;
    private readonly Func<ConfigSnapshot> _config;
    // DataSource "" = the message had no data_source (the configured one is used).
    private readonly ConcurrentDictionary<(string Machine, string DataSource, string Group), GroupState> _states = new();
    private readonly Lazy<Dictionary<string, SavedState>> _saved; // last values from the outbox, read on first use
    private readonly Lazy<Dictionary<string, long>> _pendingDue;  // latest send_after still waiting per group, ditto

    public IntervalSender(Outbox outbox, EnvelopeBuilder envelopes, Func<ConfigSnapshot> config)
    {
        _outbox = outbox;
        _envelopes = envelopes;
        _config = config;
        _saved = new Lazy<Dictionary<string, SavedState>>(() =>
        {
            try { return outbox.LatestStates(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[outbox] last values not read back, the first change of each group is sent as new: {ex.Message}");
                return new Dictionary<string, SavedState>();
            }
        });
        _pendingDue = new Lazy<Dictionary<string, long>>(() =>
        {
            try { return outbox.PendingDueMs(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[outbox] waiting send times not read back, the first record of each group may be sent at once " +
                                  $"(the outbox still keeps each group's order): {ex.Message}");
                return new Dictionary<string, long>();
            }
        });
    }

    // Called by the ingest workers for every MES-bound bucket of a message.
    // dataSource = the message's "data_source": part of the key, so each
    // source has its own snapshot, changes and envelopes.
    // messageKey: what makes the message unique (GatewayHost.RepeatKey); becomes the
    // envelope's event_id (with the group).
    // onSaved: runs once the record is on disk.
    // Returns false when nothing changed (no record, onSaved never runs).
    public bool Accept(string machineId, string dataGroup, Dictionary<string, object?> fields, DateTimeOffset? eventTime,
        string? dataSource = null, string? messageKey = null, Action? onSaved = null)
    {
        var config = _config();
        var source = dataSource?.Trim() ?? "";
        var at = eventTime ?? DateTimeOffset.Now; // the record's time and the envelope's event_time
        var state = _states.GetOrAdd((machineId, source, dataGroup), key => Restored(key, config));
        Dictionary<string, object?> record;
        SavedState saved;
        long sendAfter;

        lock (state)
        {
            // Values of fields the current mapping no longer produces (tag remapped or set
            // to ignore, also when restored after a restart) must not keep going to MES (#6).
            var produced = config.Normalizer.FieldsOf(dataGroup);
            foreach (var stale in state.Snapshot.Keys.Where(k => !produced.Contains(k)).ToList())
                state.Snapshot.Remove(stale);

            // A config save built a new TimeStamper, which knows no open span: carry this
            // group's over (trigger value before this message, and its start), else the
            // span would restart now, with no end or previous value (#7).
            if (!ReferenceEquals(state.Stamper, config.TimeStamper))
            {
                RestoreSpan(config, machineId, source, dataGroup, state.Snapshot, state.SpanSince);
                state.Stamper = config.TimeStamper;
            }

            // Changes go to a copy, kept only once saved: a value that can't be saved must not
            // stay in the snapshot and fail every later message of the group.
            var snapshot = new Dictionary<string, object?>(state.Snapshot);
            if (!ApplyChanges(snapshot, fields, MesColumns(config, dataGroup)))
            {
                state.Snapshot = snapshot; // extra fields still kept up to date
                return false; // nothing new for MES (same values, or only fields MES never gets)
            }

            record = new Dictionary<string, object?>(snapshot);
            config.TimeStamper.Stamp(machineId, dataGroup, record, at, source);
            saved = Save(machineId, source, dataGroup, snapshot, record, config);
            sendAfter = SendAfter(state, SendEveryMs(config, dataGroup));
            state.Snapshot = snapshot;
            state.SpanSince = saved.SpanSince;
        }

        // Lines of one machine come from one connection and one worker, in order,
        // so its records still reach the outbox in the order they were made.
        _outbox.Write(_envelopes.Build(machineId, dataGroup, record, source, at, MesColumns(config, dataGroup), messageKey),
            sendAfter, onSaved, saved);
        return true;
    }

    // A group seen for the first time since the start: carry on from the values
    // saved in the outbox (and the span they had open), if there are any.
    private GroupState Restored((string Machine, string DataSource, string Group) key, ConfigSnapshot config)
    {
        var stateKey = SavedState.KeyOf(key.Machine, key.DataSource, key.Group);
        // A record still waiting from before the restart: the next one is not due before it (SVI-99).
        var state = new GroupState { Stamper = config.TimeStamper, NextDueMs = _pendingDue.Value.GetValueOrDefault(stateKey) };
        if (!_saved.Value.TryGetValue(stateKey, out var saved))
            return state;

        foreach (var (name, value) in saved.Values)
            state.Snapshot[name] = PayloadParser.ParseValue(value);
        state.SpanSince = saved.SpanSince;
        RestoreSpan(config, key.Machine, key.DataSource, key.Group, state.Snapshot, saved.SpanSince);
        return state;
    }

    // Tells the TimeStamper the open span of a Span group: its trigger value and since when.
    private static void RestoreSpan(ConfigSnapshot config, string machineId, string source, string dataGroup,
        Dictionary<string, object?> snapshot, string? since)
    {
        var rule = config.TimeStamper.RuleFor(dataGroup);
        if (rule is { Mode: TimestampMode.Span, TriggerField: { } trigger } && since is not null
            && DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var sinceAt))
            config.TimeStamper.Restore(machineId, dataGroup, source, snapshot.GetValueOrDefault(trigger), sinceAt);
    }

    private static SavedState Save(string machineId, string source, string dataGroup, Dictionary<string, object?> snapshot,
        Dictionary<string, object?> record, ConfigSnapshot config)
    {
        var rule = config.TimeStamper.RuleFor(dataGroup);
        var since = rule is { Mode: TimestampMode.Span, StartField: { } start } ? record.GetValueOrDefault(start) as string : null;
        return new SavedState(SavedState.KeyOf(machineId, source, dataGroup),
            snapshot.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)), since);
    }

    // When MES may get this record (UTC ms): now, if the group's last send moment
    // is more than sendEveryMs ago (that starts a new interval); otherwise the
    // end of the current interval. sendEveryMs 0 = always now.
    private static long SendAfter(GroupState state, int sendEveryMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (sendEveryMs <= 0)
            return now;
        if (now >= state.NextDueMs)
        {
            state.NextDueMs = now + sendEveryMs;
            return now;
        }
        return state.NextDueMs;
    }

    // #29: a message with its cycle's start and end gets PRODUCTION cycle_time = end - start in
    // seconds, unless the machine sent its own. Start / end: the startField / endField of PRODUCTION,
    // else of MACHINE_STATUS (MES needs them there, e.g. LDM's start_time / cycle_end_time),
    // both from this message, before any stamping. Only when MES has the column.
    // buckets: one message, sorted into data groups (Normalizer.Sort); a PRODUCTION one is added if needed.
    public static void AddCycleTime(ConfigSnapshot config, Dictionary<string, Dictionary<string, object?>> buckets)
    {
        Dictionary<string, object?>? Bucket(string group) => buckets.FirstOrDefault(b => b.Key.Equals(group, StringComparison.OrdinalIgnoreCase)).Value;
        var production = Bucket("PRODUCTION");
        if (MesColumns(config, "PRODUCTION")?.Contains("cycle_time", StringComparer.OrdinalIgnoreCase) != true
            || production?.Keys.Contains("cycle_time", StringComparer.OrdinalIgnoreCase) == true)
            return;

        foreach (var group in new[] { "PRODUCTION", "MACHINE_STATUS" })
            if (config.TimeStamper.RuleFor(group) is { StartField: { } start, EndField: { } end } && Bucket(group) is { } fields
                && Time(fields.GetValueOrDefault(start)) is { } from && Time(fields.GetValueOrDefault(end)) is { } to && to >= from)
            {
                if (production is null)
                    buckets["PRODUCTION"] = production = new Dictionary<string, object?>();
                production["cycle_time"] = Math.Round((to - from).TotalSeconds, 3);
                return;
            }
    }

    private static DateTimeOffset? Time(object? value) =>
        value is string s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static IReadOnlyList<string>? MesColumns(ConfigSnapshot config, string dataGroup) =>
        config.MesColumns.TryGetValue(dataGroup, out var columns) ? columns : null;

    private static int SendEveryMs(ConfigSnapshot config, string dataGroup) =>
        config.TimestampRules.TryGetValue(dataGroup, out var rule) && rule.SendEveryMs is { } ms
            ? ms
            : DefaultSendEveryMs;

    // Copies incoming values into the snapshot; true when a value MES gets changed.
    // A field with no MES column (an extra field, e.g. event_time) is kept up to date
    // but its change alone makes no record: MES would get a row that looks unchanged.
    // columns null = a group with no MES table: every field counts.
    private static bool ApplyChanges(Dictionary<string, object?> snapshot, Dictionary<string, object?> incoming,
        IReadOnlyList<string>? columns)
    {
        var changed = false;
        foreach (var (name, value) in incoming)
        {
            if (snapshot.TryGetValue(name, out var old) && SameValue(old, value))
                continue;
            snapshot[name] = value;
            if (columns is null || columns.Count == 0 || columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                changed = true;
        }

        return changed;
    }

    // Values come from PayloadParser: double, bool, string, null, or nested
    // dictionaries / lists. Nested ones are compared by their JSON text.
    private static bool SameValue(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is Dictionary<string, object?> || a is List<object?> || b is Dictionary<string, object?> || b is List<object?>)
            return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
        return a.Equals(b);
    }

    private sealed class GroupState
    {
        public Dictionary<string, object?> Snapshot = new();
        public long NextDueMs; // end of the current sendEveryMs interval (UTC ms)
        public TimeStamper? Stamper; // the config's TimeStamper that last saw this group's span
        public string? SpanSince;    // the open span's start (Span groups), as in the last record
    }
}
