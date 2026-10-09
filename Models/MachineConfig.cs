namespace EdgeGateway.Models;

// Per-machine settings, read from Config/machine-config.json every time the
// gateway (re)starts. The defaults are the values Program.cs used to
// hard-code, so a missing file behaves exactly like before.
public class MachineConfig
{
    public string GatewayId { get; set; } = "TEST";

    // TODO: read from the message itself once more than one machine shares this port
    public string MachineId { get; set; } = "ASSY-L01-FCT-01";

    // Port the machine connects to. Machines must reconnect after a restart.
    public int TcpPort { get; set; } = 9000;

    public string DataSource { get; set; } = "TCP-BRIDGE-01";
    public string ProtocolLabel { get; set; } = "TCP/JSON-CSV";
    public string MesUrl { get; set; } = "http://localhost:5000/api/v1/machine-events";
}
