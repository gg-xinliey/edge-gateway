using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EdgeGateway.Services;

// Replies to a machine on its own TCP connection, one JSON line each:
//   {"ack":"<message_id>"}                     the message is on disk (or was already)
//   {"nack":"<message_id>","reason":"..."}     the message will never be accepted
// The adapter keeps each message until its reply arrives, and sends it again
// otherwise. So a reply for a connection that is gone is simply dropped: the
// adapter sends that message again on its new connection and gets its reply then.
//
// Each connection has its own queue and writer, so a slow or non-reading
// client never holds up the others; past MaxQueued waiting replies, new ones
// for that connection are dropped (same effect as a lost reply).
public sealed class ReplyHub
{
    private const int MaxQueued = 10_000;

    private readonly ConcurrentDictionary<string, Channel<string>> _connections = new();

    // ---- called by TcpIngestServer ----

    public ChannelReader<string> Open(string sender)
    {
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueued)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _connections[sender] = queue;
        return queue.Reader;
    }

    public void Close(string sender)
    {
        if (_connections.TryRemove(sender, out var queue))
            queue.Writer.TryComplete();
    }

    // Writes queued replies to the socket, batching whatever is waiting.
    public static async Task WriteAsync(NetworkStream stream, ChannelReader<string> replies)
    {
        var text = new StringBuilder();
        try
        {
            while (await replies.WaitToReadAsync())
            {
                text.Clear();
                while (text.Length < 64 * 1024 && replies.TryRead(out var line))
                    text.Append(line).Append('\n');
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()));
            }
        }
        catch (Exception)
        {
            // Connection gone: the adapter sends its unanswered messages again.
        }
    }

    // ---- called by the pipeline ----

    public void Ack(string sender, string messageId) =>
        Send(sender, JsonSerializer.Serialize(new { ack = messageId }));

    public void Nack(string sender, string messageId, string reason) =>
        Send(sender, JsonSerializer.Serialize(new { nack = messageId, reason }));

    private void Send(string sender, string line)
    {
        if (_connections.TryGetValue(sender, out var queue))
            queue.Writer.TryWrite(line);
    }
}
