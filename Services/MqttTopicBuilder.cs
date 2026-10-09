namespace EdgeGateway.Services;

// Topics the gateway builds itself (TwinForwarder). Lines that arrive with
// their own topic are published on that topic by MqttRelay instead.
//
// Twin data must end in one of four fixed pairs:
//   svi/<area>/<machine_id>/IO/input
//   svi/<area>/<machine_id>/IO/output
//   svi/<area>/<machine_id>/motion/position
//   svi/<area>/<machine_id>/motion/status
// keepalive is the gateway's own heartbeat, kept apart from those four:
//   svi/<area>/<machine_id>/keepalive
public static class MqttTopicBuilder
{
    public static string IoInput(string area, string machineId) => $"svi/{area}/{machineId}/IO/input";
    public static string IoOutput(string area, string machineId) => $"svi/{area}/{machineId}/IO/output";
    public static string MotionPosition(string area, string machineId) => $"svi/{area}/{machineId}/motion/position";
    public static string MotionStatus(string area, string machineId) => $"svi/{area}/{machineId}/motion/status";
    public static string Keepalive(string area, string machineId) => $"svi/{area}/{machineId}/keepalive";
}
