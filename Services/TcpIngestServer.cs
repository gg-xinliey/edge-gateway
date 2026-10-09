using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EdgeGateway.Services;

// Raw TCP listener. Frames messages by newline: every line is either
// one JSON snapshot or one CSV tag/value pair (see PayloadParser).
// Change the framing here if your senders use something else
// (length-prefixed, a delimiter byte, etc).
public class TcpIngestServer
{
    private readonly int _port;
    private readonly Action<string> _onMessage;

    public TcpIngestServer(int port, Action<string> onMessage)
    {
        _port = port;
        _onMessage = onMessage;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"[tcp] listening on port {_port}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint;
        Console.WriteLine($"[tcp] client connected: {endpoint}");

        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    _onMessage(line);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[tcp] failed to process message from {endpoint}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"[tcp] client disconnected: {endpoint}");
    }
}
