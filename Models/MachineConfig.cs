namespace EdgeGateway.Models;

// The gateway's own settings ("GatewayConfig" on the config page), read from
// Config/machine-config.json every time the gateway (re)starts. The defaults
// are the values Program.cs used to hard-code, so a missing file behaves like before.
// Which machine a message is from comes from the message itself (machine_id).
public class MachineConfig
{
    // Also names the gateway's own MQTT heartbeat: svi/<area>/<gatewayId>/keepalive.
    public string GatewayId { get; set; } = "TEST";

    // Address + port the machines connect to. Machines must reconnect after a restart.
    // "0.0.0.0" = every address of this PC (incl. 127.0.0.1); a specific IP
    // (e.g. "192.168.250.78") = only that network card, so 127.0.0.1 is refused.
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int TcpPort { get; set; } = 9000;

    public string DataSource { get; set; } = "TCP-BRIDGE-01";

    public string ProtocolLabel { get; set; } = "TCP/JSON-CSV";
    // Where MES records go: MesUrls[data group] when that group has its own address
    // (MES: one API per data group), else MesUrl. MesUrl empty = a group without its own
    // address is kept in the outbox, not sent, until it gets one. Read at start.
    public string MesUrl { get; set; } = "http://localhost:5000/api/v1/machine-events";
    public Dictionary<string, string> MesUrls { get; set; } = new();

    // The address for one data group, or null = keep it in the outbox (no address yet).
    public string? MesUrlFor(string dataGroup)
    {
        var own = (MesUrls ?? new()).FirstOrDefault(p => p.Key.Trim().Equals(dataGroup, StringComparison.OrdinalIgnoreCase)).Value;
        var url = string.IsNullOrWhiteSpace(own) ? MesUrl : own;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
    }

    // How the gateway logs in to MES: MesLogins[data group] when that group has its own
    // (type not empty), else MesLogin. Secrets are masked on the config page. Read at start.
    public MesApiLogin MesLogin { get; set; } = new();
    public Dictionary<string, MesApiLogin> MesLogins { get; set; } = new();

    public MesApiLogin MesLoginFor(string dataGroup)
    {
        var own = (MesLogins ?? new()).FirstOrDefault(p => p.Key.Trim().Equals(dataGroup, StringComparison.OrdinalIgnoreCase)).Value;
        return own is not null && !string.IsNullOrWhiteSpace(own.Type) ? own : MesLogin ?? new();
    }

    // https to MES with a company certificate: its CA's .crt / .pem in the data Config folder
    // (or a full path). Empty = only CAs this PC trusts. Read at start.
    public string MesCaFile { get; set; } = "";

    // A machine_id that has no tab (no own mapping) sends MES data:
    //   "hold"   (default) not processed, not answered, shown on the config page:
    //            the adapter keeps it and sends it again once the machine has a tab
    //   "shared" processed with the shared (Template) mapping, as before tabs existed
    public string UnregisteredMachines { get; set; } = "hold";

    [System.Text.Json.Serialization.JsonIgnore] // helper, not a setting
    public bool HoldUnregistered => !UnregisteredMachines.Trim().Equals("shared", StringComparison.OrdinalIgnoreCase);

    // A machine with a tab sends a tag its mapping doesn't know:
    //   "send" (default) the tags it knows go to MES as usual and the message is
    //          answered; the unknown ones are left out of MES, listed under the tab's
    //          Unidentified (with their value) and logged, until mapped or ignored
    //   "hold" the whole message is held (not processed, not answered) until then
    public string UnmappedTags { get; set; } = "send";

    [System.Text.Json.Serialization.JsonIgnore] // helper, not a setting
    public bool HoldUnmappedTags => UnmappedTags.Trim().Equals("hold", StringComparison.OrdinalIgnoreCase);

    // Rows MES has acknowledged are deleted after this many days.
    // Rows not yet delivered are never deleted.
    public int OutboxKeepSentDays { get; set; } = 7;

    // A record MES keeps answering 500 or 413 moves to failed messages after this many
    // tries in a row, once MES took other records meanwhile or after MesStuckRowMinutes
    // (MesForwarder). Read at start: applied by Restart Gateway.
    public int MesStuckRowTries { get; set; } = 5;
    public int MesStuckRowMinutes { get; set; } = 10;

    // Name matching for tags the point map doesn't know (NameMatcher): a tag that matches
    // one MES column at least this well (0.5-1, 1 = the same name) is mapped automatically,
    // marked "auto" in the point map; below it, or when the column is ambiguous or already
    // filled, it is only suggested on the Unidentified list. Read at start (Restart Gateway).
    public double NameMatchThreshold { get; set; } = 0.9;

    // Words that mean the same in tag names: "short=long", comma-separated.
    public string NameMatchSynonyms { get; set; } = "qty=quantity, sn=serial number, temp=temperature, pcs=quantity";

    // Problems (MES unreachable, MES failures, held machines, auto tabs, time warnings, a big
    // backlog) are POSTed here as JSON when one starts or clears (#23). Empty = off. Read at start.
    public string AlarmWebhookUrl { get; set; } = "";

    // Password to change a confirmed tab's mapping (⇄ on the config page, #31). Empty = none asked.
    // Never sent back to the page (masked). Read at start.
    public string MappingPassword { get; set; } = "";
}

// The login for one MES API, sent with every record:
//   "" / "none"  no login (in mesLogins "" = the default login, mesLogin)
//   "bearer"     Authorization: Bearer <secret>          (a token)
//   "basic"      Authorization: Basic <username:secret>  (username + password)
//   "header"     <header>: <secret>                      (an API key, e.g. X-API-Key)
public class MesApiLogin
{
    public static readonly string[] Types = { "", "none", "bearer", "basic", "header" };

    public string Type { get; set; } = "";
    public string Username { get; set; } = "";
    public string Header { get; set; } = "";
    public string Secret { get; set; } = "";

    public void Apply(System.Net.Http.Headers.HttpRequestHeaders headers)
    {
        switch (Type.Trim().ToLowerInvariant())
        {
            case "bearer":
                headers.Authorization = new("Bearer", Secret);
                break;
            case "basic":
                headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Username}:{Secret}")));
                break;
            case "header":
                headers.TryAddWithoutValidation(Header.Trim(), Secret);
                break;
        }
    }

    // For logs and the page: the kind of login, never the secret.
    public override string ToString() => Type.Trim().ToLowerInvariant() switch
    {
        "bearer" => "bearer token",
        "basic" => $"username {Username} + password",
        "header" => $"{Header.Trim()} key",
        _ => "no login",
    };
}
