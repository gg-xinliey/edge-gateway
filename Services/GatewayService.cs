using System.Runtime.Versioning;
using System.ServiceProcess;

namespace EdgeGateway.Services;

// The gateway as the Windows service "EdgeGateway" (Program.cs "--service",
// installed by service\install-service.ps1). A service stop, or Windows shutting
// down, cancels the run like Ctrl+C: the outbox writes what it holds and the
// message_ids are saved. If the run ends by itself with an error, the process
// exits with code 1, so Windows restarts the service (its failure actions).
[SupportedOSPlatform("windows")]
public sealed class GatewayService : ServiceBase
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30); // the gateway needs up to ~15 s

    private readonly Func<CancellationToken, Task> _run;
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;

    public GatewayService(Func<CancellationToken, Task> run)
    {
        _run = run;
        ServiceName = "EdgeGateway";
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _running = Task.Run(() => _run(_stop.Token));
        _ = _running.ContinueWith(t =>
        {
            Console.WriteLine($"[gateway] stopped by an error, Windows restarts the service: {t.Exception?.GetBaseException().Message}");
            Environment.Exit(1);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    protected override void OnStop() => StopRun();

    protected override void OnShutdown() => StopRun();

    private void StopRun()
    {
        RequestAdditionalTime((int)StopWait.TotalMilliseconds);
        _stop.Cancel();
        try { _running?.Wait(StopWait); }
        catch (AggregateException) { /* already logged by the run */ }
    }
}
