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
//
// Replies ({"ack":...} once a message is on disk) go back on the same
// connection through ReplyHub; each connection has its own reply writer.
public class TcpIngestServer
{
    private const int LaneCapacity = 20_000; // per lane; when full the reader waits (back-pressure to the machine)

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly Action<string, string> _onMessage; // (line, sender = "ip:port" of the connection)
    private readonly TrafficRegistry? _traffic;
    private readonly ReplyHub? _replies;
    private readonly Channel<(string Line, string Sender)>[] _lanes;
    private readonly ThrottledLog _problems = new(TimeSpan.FromSeconds(60));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TcpClient> _clients = new();
    private int _nextLane;

    // Closes one machine connection (ip:port). Its adapter reconnects and sends
    // every message it got no reply for again (used after registering a held machine).
    public bool Disconnect(string endpoint)
    {
        if (!_clients.TryGetValue(endpoint, out var client))
            return false;
        _closing[endpoint] = 0;
        client.Close();
        return true;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _closing = new();

    public TcpIngestServer(IPAddress address, int port, Action<string, string> onMessage, TrafficRegistry? traffic = null,
        ReplyHub? replies = null)
    {
        _address = address;
        _port = port;
        _onMessage = onMessage;
        _traffic = traffic;
        _replies = replies;
        _lanes = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 1, 8))
            .Select(_ => Channel.CreateBounded<(string, string)>(
                new BoundedChannelOptions(LaneCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }))
            .ToArray();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_address, _port);
        listener.Start();
        Console.WriteLine($"[tcp] listening on {_address}:{_port} ({_lanes.Length} worker lanes)");

        // Workers drain their lane even after a stop, so lines already read are not lost.
        var workers = _lanes.Select(lane => Task.Run(() => WorkAsync(lane))).ToArray();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // One failed accept (a peer that reset while still in the backlog, a port
                    // scan, too many open handles) must not close the port for everyone (#8).
                    _problems.Write("accept", $"[tcp] accepting a connection failed, still listening: {ex.Message}");
                    await Task.Delay(100, ct); // no busy loop while it keeps failing
                    continue;
                }
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
        var sender = endpoint?.ToString() ?? "?"; // ip:port: one machine per connection, even behind one IP
        Console.WriteLine($"[tcp] client connected: {endpoint}");
        _traffic?.Opened(sender);
        _clients[sender] = client;

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var replying = _replies is null ? Task.CompletedTask : ReplyHub.WriteAsync(stream, _replies.Open(sender));
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        _traffic?.LineReceived(sender);
                        await lane.Writer.WriteAsync((line, sender), ct);
                    }
                }
                finally
                {
                    // Replies still queued get 2 s to go out; any not sent, the adapter resends.
                    _replies?.Close(sender);
                    await Task.WhenAny(replying, Task.Delay(TimeSpan.FromSeconds(2)));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            // gateway stopping or restarting
        }
        catch (Exception) when (_closing.ContainsKey(sender))
        {
            // closed on purpose (Disconnect): the adapter reconnects and resends
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[tcp] connection from {endpoint} failed: {ex.Message}");
        }

        if (_closing.TryRemove(sender, out _))
            Console.WriteLine($"[tcp] {endpoint} closed by the gateway so the adapter sends its unanswered messages again");
        _clients.TryRemove(new KeyValuePair<string, TcpClient>(sender, client));
        Console.WriteLine($"[tcp] client disconnected: {endpoint}");
        _traffic?.Closed(sender);
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
                _traffic?.Unreadable(sender);
                _problems.Write($"{sender}|{ex.GetType().Name}", $"[tcp] failed to process message from {sender}: {ex.Message}");
            }
        }
    }
}
