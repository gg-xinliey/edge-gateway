namespace EdgeGateway.Services;

// One MES path: an outbox folder, its MES sender, and the interval state
// (changes, spans, sendEveryMs pacing) for one mapping. The shared mapping has
// one (outbox/), every machine with its own mapping has its own
// (outbox/<machine_id>/). All of them send to the same MES.
//
// A machine whose own mapping was removed keeps its pipeline until the
// gateway restarts, and at start an outbox folder with rows gets one too, so
// whatever is in its outbox is still delivered.
public sealed class MesPipeline : IAsyncDisposable
{
    private readonly EnvelopeBuilder _envelopes;
    private readonly object _sync = new();
    private IntervalSender? _sender;
    private GatewayConfigStore? _senderStore;

    public MesPipeline(string? machineId, string outboxFolder, int keepSentDays, HttpClient http, string mesUrl,
        EnvelopeBuilder envelopes, GatewayStats stats, CancellationToken ct)
    {
        MachineId = machineId;
        _envelopes = envelopes;
        Outbox = new Outbox(outboxFolder, keepSentDays, stats);
        Running = new MesForwarder(http, mesUrl, Outbox, stats, machineId).RunAsync(ct);
    }

    public string? MachineId { get; }  // null = the shared pipeline
    public Outbox Outbox { get; }
    public Task Running { get; }        // the MES sender

    // The interval state for this mapping. A new store (mapping removed and
    // given again) starts a fresh state: last values and spans are not carried over.
    public IntervalSender SenderFor(GatewayConfigStore store)
    {
        lock (_sync)
        {
            if (_sender is null || !ReferenceEquals(_senderStore, store))
            {
                _sender = new IntervalSender(Outbox, _envelopes, () => store.Current);
                _senderStore = store;
            }
            return _sender;
        }
    }

    // Writes what is still queued (max 10 s), after the sender was stopped.
    public ValueTask DisposeAsync() => Outbox.DisposeAsync();
}
