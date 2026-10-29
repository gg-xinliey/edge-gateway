using System.Text;
using System.Text.Json;

namespace EdgeGateway.Services;

// #23: problems pushed to alarmWebhookUrl (machine-config.json) when one starts or clears, like the
// AOI adapter's: JSON {text, gateway, host, raised, cleared, active} to a Teams / Power Automate /
// Slack incoming webhook. GatewayHost checks with every [stats] line (10 s). A failed POST is tried
// again at the next check; a check while a POST is still going is skipped.
public sealed class AlarmWebhook
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Uri _url;
    private readonly string _gatewayId;
    private Dictionary<string, string> _notified = new(); // key -> text, as last sent
    private int _busy;

    public AlarmWebhook(Uri url, string gatewayId)
    {
        _url = url;
        _gatewayId = gatewayId;
    }

    public async Task CheckAsync(IReadOnlyList<(string Key, string Text)> problems)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var now = problems.DistinctBy(p => p.Key).ToDictionary(p => p.Key, p => p.Text);
            var raised = now.Where(p => !_notified.ContainsKey(p.Key)).Select(p => p.Value).ToList();
            var cleared = _notified.Where(p => !now.ContainsKey(p.Key)).Select(p => p.Value).ToList();
            if (raised.Count == 0 && cleared.Count == 0)
                return;

            var text = $"Edge gateway {_gatewayId} on {Environment.MachineName}: " +
                       (raised.Count > 0 ? $"PROBLEM: {string.Join(" | ", raised)}. " : "") +
                       (cleared.Count > 0 ? $"Cleared: {string.Join(" | ", cleared)}. " : "") +
                       $"Active problems: {now.Count}.";
            var json = JsonSerializer.Serialize(new { text, gateway = _gatewayId, host = Environment.MachineName, raised, cleared, active = now.Values.ToList() });
            try
            {
                using var response = await Http.PostAsync(_url, new StringContent(json, Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
                _notified = now;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                Console.WriteLine($"[alarm] webhook failed ({ex.Message}), trying again at the next check");
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
