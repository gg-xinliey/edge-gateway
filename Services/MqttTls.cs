using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Client;

namespace EdgeGateway.Services;

// TLS for both MQTT connections (mqtt-config.json useTls / tlsCaFile). The broker's certificate must
// be trusted by this PC / container, or come from the CA in tlsCaFile (a .crt / .pem in the data Config
// folder, or a full path) when the broker uses a company CA. Its name must match brokerAddress either way.
public static class MqttTls
{
    public static MqttClientOptionsBuilder Apply(MqttClientOptionsBuilder builder, bool useTls, string? caFile)
    {
        if (!useTls)
            return builder;
        var ca = string.IsNullOrWhiteSpace(caFile) ? null : LoadCa(caFile);
        return builder.WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(e => Trusted(e.Certificate, e.SslPolicyErrors, ca)));
    }

    public static string CaPath(string caFile) =>
        Path.IsPathRooted(caFile) ? caFile : Path.Combine(DataFolder.Root, "Config", caFile);

    private static X509Certificate2 LoadCa(string caFile)
    {
        var path = CaPath(caFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"TLS CA file not found: {path}", path);
        return new X509Certificate2(path); // .crt / .cer / .pem, DER or PEM
    }

    // Trusted by the system, or its chain ends at the CA from tlsCaFile (ca null = none given). A name
    // that doesn't match, or no certificate, is refused either way. Refused: says why (the log and
    // the Connect button show it), instead of the TLS library's "rejected by the callback".
    private static bool Trusted(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2? ca)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            throw new AuthenticationException("the broker sent no TLS certificate");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            throw new AuthenticationException($"the broker's certificate is for {((X509Certificate2)certificate).GetNameInfo(X509NameType.DnsName, false)}, " +
                                              "not for the broker address: use that name as the broker address");
        if (ca is null)
            throw new AuthenticationException("the broker's certificate is not trusted by this PC: put its CA's .crt in the data Config folder " +
                                              "and its name in \"TLS: company CA file\"");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // a company CA's revocation list is often unreachable
        return chain.Build(new X509Certificate2(certificate))
            ? true
            : throw new AuthenticationException($"the broker's certificate is not from the CA in the CA file ({ca.Subject})");
    }
}
