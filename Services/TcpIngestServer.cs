using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace EdgeGateway.Services;

// Raw TCP listener. Frames messages by newline: every line is either
// one JSON snapshot or one CSV tag/value pair (see PayloadParser).
// Change the framing here if your senders use something else
// (length-prefixed, a delimiter byte, etc).
//
// Many machines: a connection's reader only reads lines and queues them, so
// slow processing never stops the gateway from reading the socket. Workers
// ("lanes", one per CPU core, max 8) process the lines. Each connection is
// tied to one lane, so lines from one machine are handled in the order sent.
public class TcpIngestServer
{
    private const int LaneCapacity = 20_000; // per lane; when full the reader waits (back-pressure to the machine)

    private readonly int _port;
    private readonly Action<string, string> _onMessage; // (line, sender ip)
    private readonly Channel<(string Line, string Sender)>[] _lanes;
    private readonly ThrottledLog _problems = new(TimeSpan.FromSeconds(60));
    private int _nextLane;

    public TcpIngestServer(int port, Action<string, string> onMessage)
    {
        _port = port;
        _onMessage = onMessage;
        _lanes = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 1, 8))
            .Select(_ => Channel.CreateBounded<(string, string)>(
                new BoundedChannelOptions(LaneCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }))
            .ToArray();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"[tcp] listening on port {_port} ({_lanes.Length} worker lanes)");

        // Workers drain their lane even after a stop, so lines already read are not lost.
        var workers = _lanes.Select(lane => Task.Run(() => WorkAsync(lane))).ToArray();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                var lane = _lanes[(uint)Interlocked.Increment(ref _nextLane) % _lanes.Length];
                _ = HandleClientAsync(client, lane, ct);
            }
        }
        finally
        {
            listener.Stop();
            foreach (var lane in _lanes)
                lane.Writer.TryComplete();
            await Task.WhenAny(Task.WhenAll(workers), Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    private async Task HandleClientAsync(TcpClient client, Channel<(string, string)> lane, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint;
        var sender = (endpoint as IPEndPoint)?.Address.ToString() ?? "?";
        Console.WriteLine($"[tcp] client connected: {endpoint}");

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        await lane.Writer.WriteAsync((line, sender), ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            // gateway stopping or restarting
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[tcp] connection from {endpoint} failed: {ex.Message}");
        }

        Console.WriteLine($"[tcp] client disconnected: {endpoint}");
    }

    private async Task WorkAsync(Channel<(string Line, string Sender)> lane)
    {
        await foreach (var (line, sender) in lane.Reader.ReadAllAsync())
        {
            try
            {
                _onMessage(line, sender);
            }
            catch (Exception ex)
            {
                _problems.Write($"{sender}|{ex.GetType().Name}", $"[tcp] failed to process message from {sender}: {ex.Message}");
            }
        }
    }
}
