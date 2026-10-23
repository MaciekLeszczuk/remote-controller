using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

sealed class ClientSettings
{
    const string FileName = "client.json";

    public string Server { get; set; } = "";
    public string Secret { get; set; } = "secret";
    public bool AutoScreenOff { get; set; } = true;
    // Set while the lock screen has the brightness turned down; restored at the next start after a crash.
    public int? BrightnessToRestore { get; set; }

    public static ClientSettings Load() => SettingsFile.Load<ClientSettings>(FileName);
    public void Save() => SettingsFile.Save(FileName, this);
}

record DiscoveredServer(string Name, IPEndPoint EndPoint, bool Compatible)
{
    public override string ToString() =>
        $"{Name}   ({EndPoint})" + (Compatible ? "" : "   - different version, update it");
}

sealed class ServerDiscoveryForm : Form
{
    const int ScanTimeoutMs = 1500;

    readonly ClientSettings settings;
    readonly ListBox serverList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox addressBox = new() { Dock = DockStyle.Fill, PlaceholderText = "IP or host name, optionally :port" };
    readonly TextBox secretBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly CheckBox showSecret = new() { Text = "Show", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label statusLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Button scanButton = new() { Text = "Scan again", AutoSize = true };
    readonly Button connectButton = new() { Text = "Connect", AutoSize = true };

    public IPEndPoint? Target { get; private set; }
    public string TargetName { get; private set; } = "";
    public string Secret => secretBox.Text;

    public ServerDiscoveryForm(ClientSettings settings)
    {
        this.settings = settings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Controller Relay";
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(520, 380);
        MinimumSize = new Size(420, 340);
        StartPosition = FormStartPosition.CenterScreen;
        AcceptButton = connectButton;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var listLabel = new Label { Text = "Servers on your network:", AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        layout.Controls.Add(listLabel, 0, 0);
        layout.SetColumnSpan(listLabel, 3);
        layout.Controls.Add(serverList, 0, 1);
        layout.SetColumnSpan(serverList, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddField(layout, 2, "Server:", addressBox);
        layout.SetColumnSpan(addressBox, 2);
        AddField(layout, 3, "Secret:", secretBox);
        layout.Controls.Add(showSecret, 2, 3);

        layout.Controls.Add(statusLabel, 0, 4);
        layout.SetColumnSpan(statusLabel, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(connectButton);
        buttons.Controls.Add(scanButton);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(layout);
        ResumeLayout(false);
        PerformLayout();

        addressBox.Text = settings.Server;
        secretBox.Text = settings.Secret;
        showSecret.CheckedChanged += (_, _) => secretBox.UseSystemPasswordChar = !showSecret.Checked;
        serverList.SelectedIndexChanged += (_, _) =>
        {
            if (serverList.SelectedItem is DiscoveredServer s) addressBox.Text = s.EndPoint.ToString();
        };
        serverList.DoubleClick += (_, _) => { if (serverList.SelectedItem != null) connectButton.PerformClick(); };
        scanButton.Click += async (_, _) => await ScanAsync();
        connectButton.Click += async (_, _) => await ConnectAsync();
        Shown += async (_, _) => await ScanAsync();
    }

    static void AddField(TableLayoutPanel layout, int row, string caption, Control field)
    {
        layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(field, 1, row);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }

    async Task ScanAsync()
    {
        scanButton.Enabled = false;
        SetStatus("Scanning network...", SystemColors.ControlText);
        serverList.Items.Clear();
        try
        {
            var servers = await Task.Run(() => Discover(ScanTimeoutMs));
            foreach (var server in servers) serverList.Items.Add(server);
            if (servers.Count == 0)
            {
                SetStatus("No servers found. Type the server's IP above, or check its firewall (UDP 7776).", Color.DarkOrange);
            }
            else
            {
                SetStatus($"Found {servers.Count} server(s). Double-click one to connect.", Color.ForestGreen);
                var previous = servers.FirstOrDefault(s => s.EndPoint.ToString() == addressBox.Text.Trim());
                if (previous != null) serverList.SelectedItem = previous;
                else if (servers.Count == 1 && addressBox.Text.Length == 0) serverList.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Scan failed: " + ex.Message, Color.Firebrick);
        }
        scanButton.Enabled = true;
    }

    // Broadcasts on every IPv4 interface: a plain 255.255.255.255 broadcast only leaves through
    // one adapter, which misses the LAN when VPN, Hyper-V or similar adapters are present.
    static List<DiscoveredServer> Discover(int timeoutMs)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        Protocol.DisableConnectionReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = new[] { Protocol.DiscoveryRequest };
        foreach (var address in BroadcastAddresses())
        {
            try { socket.SendTo(request, new IPEndPoint(address, Protocol.DiscoveryPort)); } catch (SocketException) { }
        }

        // A server with several adapters answers once per adapter; keep the first (fastest) reply.
        var found = new Dictionary<(string, int), DiscoveredServer>();
        var buffer = new byte[512];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0 || !socket.Poll((int)remaining * 1000, SelectMode.SelectRead)) break;
            int length;
            try { length = socket.ReceiveFrom(buffer, ref from); } catch (SocketException) { continue; }
            if (!Protocol.TryReadDiscoveryResponse(buffer, length, out int port, out byte version, out string name)) continue;
            var address = ((IPEndPoint)from).Address;
            if (name.Length == 0) name = address.ToString();
            found.TryAdd((name, port), new DiscoveredServer(name, new IPEndPoint(address, port), version == Protocol.Version));
        }
        return found.Values.OrderBy(s => s.Name).ToList();
    }

    static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                uint ip = BitConverter.ToUInt32(unicast.Address.GetAddressBytes());
                uint mask = BitConverter.ToUInt32(unicast.IPv4Mask.GetAddressBytes());
                addresses.Add(new IPAddress(BitConverter.GetBytes(ip | ~mask)));
            }
        }
        return addresses;
    }

    async Task ConnectAsync()
    {
        var text = addressBox.Text.Trim();
        if (!TrySplitAddress(text, out string host, out int port))
        {
            SetStatus("Enter the server as IP, host name, or address:port.", Color.Firebrick);
            return;
        }

        connectButton.Enabled = false;
        try
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                SetStatus($"Looking up {host}...", SystemColors.ControlText);
                var addresses = await Dns.GetHostAddressesAsync(host);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            Target = new IPEndPoint(ip, port);
            TargetName = serverList.SelectedItem is DiscoveredServer s && s.EndPoint.Equals(Target) ? $"{s.Name} ({Target})" : text;
            settings.Server = text;
            settings.Secret = secretBox.Text;
            settings.Save();
            DialogResult = DialogResult.OK;
        }
        catch (SocketException ex)
        {
            SetStatus($"Cannot resolve {host}: {ex.Message}", Color.Firebrick);
        }
        finally
        {
            connectButton.Enabled = true;
        }
    }

    static bool TrySplitAddress(string text, out string host, out int port)
    {
        port = Protocol.DefaultPort;
        int colon = text.LastIndexOf(':');
        host = colon >= 0 ? text[..colon] : text;
        if (colon >= 0 && !(int.TryParse(text[(colon + 1)..], out port) && port is > 0 and <= 65535)) return false;
        return host.Length > 0;
    }

    void SetStatus(string message, Color color)
    {
        statusLabel.Text = message;
        statusLabel.ForeColor = color;
    }
}
