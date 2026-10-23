static class Program
{
    // Usage: Server [port] [secret] - both optional; they override (and replace) the saved settings.
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = SettingsFile.Load<ServerSettings>(ServerSettings.FileName);
        if (args.Length >= 1 && int.TryParse(args[0], out int port) && port is > 0 and <= 65535) settings.Port = port;
        if (args.Length >= 2) settings.Secret = args[1];
        if (settings.Secret.Length == 0) settings.Secret = ServerSettings.GenerateSecret();

        Application.Run(new ServerForm(settings));
    }
}
