using System.Text;
using System.Text.Json;

namespace EdgeGateway.Services;

// At-least-once delivery to MES. Retries anything the outbox still
// has marked unsent, on a fixed interval, forever.
public class MesForwarder
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Outbox _outbox;
    private readonly TimeSpan _pollInterval;

    public MesForwarder(HttpClient http, string mesUrl, Outbox outbox, TimeSpan? pollInterval = null)
    {
        _http = http;
        _endpoint = new Uri(mesUrl);
        _outbox = outbox;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var (lineIndex, envelope) in _outbox.ReadUnsent())
            {
                try
                {
                    var json = JsonSerializer.Serialize(envelope);
                    var response = await _http.PostAsync(
                        _endpoint,
                        new StringContent(json, Encoding.UTF8, "application/json"),
                        ct);

                    // 200 or 207 from the MES application counts as delivered
                    if (response.IsSuccessStatusCode)
                    {
                        _outbox.MarkSent(lineIndex);
                        Console.WriteLine($"[forwarder] sent {envelope.event_id} ({envelope.data_group})");
                    }
                    else
                    {
                        Console.WriteLine($"[forwarder] MES returned {(int)response.StatusCode} for {envelope.event_id} — will retry");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[forwarder] send failed for {envelope.event_id}: {ex.Message} — will retry");
                }
            }

            _outbox.PurgeOldSent(); // tidy up acknowledged rows older than the keep window

            await Task.Delay(_pollInterval, ct);
        }
    }
}