namespace EdgeGateway.Services;

// Where the gateway keeps what it writes and the config page edits: Config\,
// outbox\, logs\, state\. Next to the exe by default (as always); "--data <folder>"
// puts it elsewhere, e.g. C:\ProgramData\EdgeGateway for the Windows service, so a
// rebuild or a new version of the exe never touches machine tabs or unsent rows.
public static class DataFolder
{
    public static string Root { get; private set; } = AppContext.BaseDirectory;

    public static void Use(string folder) => Root = Path.GetFullPath(folder);
}
