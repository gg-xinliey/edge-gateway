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

    // How an incoming "timestamp" without a zone is read: "UTC" (default) or
    // "local". A timestamp that carries Z or +07:00 is always taken as written.
    public string TimestampsWithoutZone { get; set; } = "UTC";

    [System.Text.Json.Serialization.JsonIgnore] // helper, not a setting (keeps it off the config page)
    public bool ZonelessIsUtc => !TimestampsWithoutZone.Trim().Equals("local", StringComparison.OrdinalIgnoreCase);
    public string ProtocolLabel { get; set; } = "TCP/JSON-CSV";
    public string MesUrl { get; set; } = "http://localhost:5000/api/v1/machine-events";

    // A machine_id that has no tab (no own mapping) sends MES data:
    //   "hold"   (default) not processed, not answered, shown on the config page:
    //            the adapter keeps it and sends it again once the machine has a tab
    //   "shared" processed with the shared (Template) mapping, as before tabs existed
    public string UnregisteredMachines { get; set; } = "hold";

    [System.Text.Json.Serialization.JsonIgnore] // helper, not a setting
    public bool HoldUnregistered => !UnregisteredMachines.Trim().Equals("shared", StringComparison.OrdinalIgnoreCase);

    // Rows MES has acknowledged are deleted after this many days.
    // Rows not yet delivered are never deleted.
    public int OutboxKeepSentDays { get; set; } = 7;
}
