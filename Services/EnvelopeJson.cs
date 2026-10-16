using System.Text.Encodings.Web;
using System.Text.Json;

namespace EdgeGateway.Services;

// How envelopes are written, both in the outbox and to MES: plain characters,
// so "+07:00" stays "+07:00" instead of the (valid but odd) "+07:00",
// and Thai / other text stays readable.
public static class EnvelopeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
