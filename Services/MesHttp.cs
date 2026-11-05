using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// HTTP to MES: https trusted by this PC, or by the company CA in mesCaFile (MqttTls.Trusted, as for
// the broker), and the config page's Test button for one MES API.
public static class MesHttp
{
    public static HttpMessageHandler Handler(string? caFile)
    {
        X509Certificate2? ca = null;
        string? caProblem = null;
        if (!string.IsNullOrWhiteSpace(caFile))
        {
            try { ca = MqttTls.LoadCa(caFile.Trim()); }
            catch (Exception ex) { caProblem = $"MES company CA file: {ex.Message}"; }
        }
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || (caProblem is null
                        ? MqttTls.Trusted(certificate, errors, ca, "MES", "the MES address", "MES: company CA file")
                        : throw new AuthenticationException(caProblem)),
            },
        };
    }

    // Why a send failed, in words: the certificate check's own reason when TLS failed
    // (instead of "The SSL connection could not be established, see inner exception").
    public static string Why(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
            if (inner is AuthenticationException)
                return inner.Message;
        return ex is TaskCanceledException ? "no answer in time" : ex.Message;
    }

    // Test button: one GET with the login, so no record is sent. Says how far it got: the address,
    // TLS, the login. Ok = MES answered in a way records can go to (not 401/403/404/5xx).
    public static async Task<(bool Ok, string Text)> TestAsync(string? url, MesApiLogin login, string? caFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "no address: its records wait in the outbox");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (false, $"'{url}' is not an http:// or https:// address");

        using var http = new HttpClient(Handler(caFile)) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        login.Apply(request.Headers);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            var answer = $"{(uri.Scheme == "https" ? "TLS OK, " : "")}{status} {response.ReasonPhrase}";
            return status switch
            {
                401 or 403 => (false, $"reached MES ({answer}): the login was refused ({login})"),
                404 => (false, $"reached the server ({answer}): it doesn't know this address, check the path"),
                405 => (true, $"reached MES ({answer}: this address only takes records). The login is checked with the first real record"),
                >= 500 => (false, $"reached the server ({answer}): MES has a problem"),
                _ => (true, $"reached MES ({answer}) with {login}"),
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, $"not reached: {Why(ex)}");
        }
    }
}
