using System.Net;
using System.Net.Sockets;
using System.Text;

static class Program
{
    const string Usage = "Usage: Client <serverIp> <serverPort> [localPort] [secret]";

    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = ClientSettings.Load();
        if (settings.BrightnessToRestore is int brightness)
        {
            Brightness.Set(brightness);
            settings.BrightnessToRestore = null;
            settings.Save();
        }

        if (args.Length >= 2)
        {
            // Command-line mode: connect straight away, exit when the window closes.
            int localPort = 0;
            if (!IPAddress.TryParse(args[0], out var ip)
                || !(int.TryParse(args[1], out int port) && port is > 0 and <= 65535)
                || (args.Length >= 3 && !int.TryParse(args[2], out localPort)))
            {
                MessageBox.Show(Usage, "Controller Relay");
                return;
            }
            string secret = args.Length >= 4 ? args[3] : "secret";
            RunRelay(new IPEndPoint(ip, port), secret, localPort, $"{ip}:{port}", settings);
            return;
        }

        while (true)
        {
            using var discovery = new ServerDiscoveryForm(settings);
            if (discovery.ShowDialog() != DialogResult.OK) return;
            if (!RunRelay(discovery.Target!, discovery.Secret, 0, discovery.TargetName, settings)) return;
        }
    }

    // Returns true when the user asked to go back to the server list.
    static bool RunRelay(IPEndPoint server, string secret, int localPort, string serverName, ClientSettings settings)
    {
        Relay relay;
        try
        {
            relay = new Relay(server, Encoding.UTF8.GetBytes(secret), localPort);
        }
        catch (SocketException ex)
        {
            MessageBox.Show($"Cannot open a UDP socket on port {localPort}: {ex.Message}", "Controller Relay");
            return true;
        }

        using var form = new ControllerRelayForm(relay, serverName, settings);
        form.ShowDialog();
        return form.BackToServerList;
    }
}
