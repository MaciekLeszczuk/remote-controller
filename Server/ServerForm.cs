using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Nefarius.ViGEm.Client;

sealed class ServerSettings
{
    public const string FileName = "server.json";

    public int Port { get; set; } = Protocol.DefaultPort;
    public string Secret { get; set; } = "";

    public static string GenerateSecret()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(10, 0, (chars, _) =>
        {
            for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }
}

sealed class ServerForm : Form
{
    const int MaxLogLines = 1000;
    const int TestRumbleMs = 1000;

    sealed record ClientItem(IPAddress Address, string Label)
    {
        public override string ToString() => Label;
    }

    readonly ServerSettings settings;
    readonly ViGEmClient? vigem;
    RelayServer? server;
    string clientListKey = "";
    bool trayHintShown;

    readonly NumericUpDown portBox = new() { Minimum = 1, Maximum = 65535, Width = 80, Anchor = AnchorStyles.Left };
    readonly TextBox secretBox = new() { Width = 170, UseSystemPasswordChar = true, Anchor = AnchorStyles.Left };
    readonly CheckBox showSecret = new() { Text = "Show", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Button startButton = new() { AutoSize = true, MinimumSize = new Size(90, 0), Anchor = AnchorStyles.Left };
    readonly Label statusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly ListBox clientList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Label clientDetails = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly GamepadView gamepadView = new() { Dock = DockStyle.Fill };
    readonly Button testStrong = new() { Text = "Test strong motor", AutoSize = true };
    readonly Button testWeak = new() { Text = "Test weak motor", AutoSize = true };
    readonly ListBox logList = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly NotifyIcon trayIcon = new() { Text = "Controller Relay Server" };
    readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 33 };

    public ServerForm(ServerSettings settings)
    {
        this.settings = settings;
        BuildLayout();

        portBox.Value = settings.Port;
        secretBox.Text = settings.Secret;
        showSecret.CheckedChanged += (_, _) => secretBox.UseSystemPasswordChar = !showSecret.Checked;
        startButton.Click += (_, _) => { if (server == null) StartServer(); else StopServer(); };
        testStrong.Click += (_, _) => TestRumble(255, 0);
        testWeak.Click += (_, _) => TestRumble(0, 255);
        refreshTimer.Tick += (_, _) => RefreshClients();

        trayIcon.Icon = Icon;
        trayIcon.ContextMenuStrip = new ContextMenuStrip();
        trayIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => RestoreFromTray());
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Close());
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) MinimizeToTray(); };

        try
        {
            vigem = new ViGEmClient();
        }
        catch (Exception ex)
        {
            AppendLog("Cannot connect to the ViGEmBus driver: " + ex.Message);
            AppendLog("Install it from https://github.com/nefarius/ViGEmBus/releases, reboot and start this program again.");
            SetStatus("ViGEmBus driver not found - see the log below", Color.Firebrick);
        }

        UpdateControls();
        Shown += (_, _) =>
        {
            ActiveControl = clientList;
            if (vigem != null) StartServer();
        };
        refreshTimer.Start();
    }

    void BuildLayout()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Controller Relay Server";
        Font = new Font("Segoe UI", 9.75F);
        ClientSize = new Size(920, 640);
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        top.Controls.AddRange(new Control[] { Caption("Port:"), portBox, Caption("Secret:"), secretBox, showSecret, startButton, statusLabel });

        var clientsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        clientsPanel.Controls.Add(Caption("Connected clients:"), 0, 0);
        clientsPanel.Controls.Add(clientList, 0, 1);
        clientsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        clientsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var testButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        testButtons.Controls.Add(testStrong);
        testButtons.Controls.Add(testWeak);

        var detailPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        detailPanel.Controls.Add(clientDetails, 0, 0);
        detailPanel.Controls.Add(gamepadView, 0, 1);
        detailPanel.Controls.Add(testButtons, 0, 2);
        detailPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        middle.Controls.Add(clientsPanel, 0, 0);
        middle.Controls.Add(detailPanel, 1, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(middle, 0, 1);
        root.Controls.Add(Caption("Log:"), 0, 2);
        root.Controls.Add(logList, 0, 3);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };

    void StartServer()
    {
        if (vigem == null) return;
        if (secretBox.Text.Length == 0)
        {
            SetStatus("Enter a secret first", Color.Firebrick);
            return;
        }

        settings.Port = (int)portBox.Value;
        settings.Secret = secretBox.Text;
        SettingsFile.Save(ServerSettings.FileName, settings);

        try
        {
            server = new RelayServer(vigem, settings.Port, Encoding.UTF8.GetBytes(settings.Secret), AppendLog);
        }
        catch (SocketException ex)
        {
            SetStatus($"Cannot listen on UDP {settings.Port}: {ex.Message}", Color.Firebrick);
            return;
        }

        string addresses = string.Join(", ", LocalAddresses());
        SetStatus($"Listening on {addresses} : {settings.Port}", Color.ForestGreen);
        AppendLog($"[{DateTime.Now:HH:mm:ss}] Server started on UDP port {settings.Port} ({addresses})");
        UpdateControls();
    }

    void StopServer()
    {
        server?.Dispose();
        server = null;
        SetStatus("Stopped", SystemColors.GrayText);
        UpdateControls();
        RefreshClients();
    }

    void UpdateControls()
    {
        bool running = server != null;
        startButton.Text = running ? "Stop" : "Start";
        startButton.Enabled = vigem != null;
        portBox.Enabled = !running;
        secretBox.ReadOnly = running;
    }

    void RefreshClients()
    {
        var clients = server?.GetClients() ?? new List<ClientInfo>();

        // Rebuild the list only when clients come or go, so the selection survives the 30 Hz refresh.
        string key = string.Join("|", clients.Select(c => $"{c.Address}/{c.Name}/{c.Player}"));
        if (key != clientListKey)
        {
            clientListKey = key;
            var previous = (clientList.SelectedItem as ClientItem)?.Address;
            clientList.BeginUpdate();
            clientList.Items.Clear();
            foreach (var c in clients) clientList.Items.Add(new ClientItem(c.Address, Describe(c)));
            clientList.EndUpdate();
            int index = clients.FindIndex(c => c.Address.Equals(previous));
            clientList.SelectedIndex = index >= 0 ? index : clients.Count > 0 ? 0 : -1;
        }

        var selectedAddress = (clientList.SelectedItem as ClientItem)?.Address;
        var selected = clients.FirstOrDefault(c => c.Address.Equals(selectedAddress));
        if (selected == null)
        {
            gamepadView.ShowState(default, 0, 0, false, server == null ? "Server stopped" : "Waiting for a client to connect...");
            SetText(clientDetails, "");
        }
        else
        {
            gamepadView.ShowState(selected.State, selected.RumbleLarge, selected.RumbleSmall, !selected.Silent,
                selected.Silent ? "No input - connection lost?" : "");
            string player = selected.Player > 0 ? $"player {selected.Player}" : "player ?";
            SetText(clientDetails, $"{Describe(selected)}   ·   {player}   ·   {selected.PacketsPerSecond} packets/s");
        }
        testStrong.Enabled = testWeak.Enabled = selected != null;
    }

    static string Describe(ClientInfo c) => c.Name.Length > 0 ? $"{c.Name} ({c.Address})" : c.Address.ToString();

    void TestRumble(byte large, byte small)
    {
        if (clientList.SelectedItem is ClientItem item) server?.TestRumble(item.Address, large, small, TestRumbleMs);
    }

    // Called from the server's threads.
    void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AppendLog(line))); } catch (InvalidOperationException) { }
            return;
        }
        logList.Items.Add(line);
        while (logList.Items.Count > MaxLogLines) logList.Items.RemoveAt(0);
        logList.TopIndex = logList.Items.Count - 1;
    }

    void SetStatus(string text, Color color)
    {
        statusLabel.Text = text;
        statusLabel.ForeColor = color;
    }

    static void SetText(Control control, string text)
    {
        if (control.Text != text) control.Text = text;
    }

    void MinimizeToTray()
    {
        Hide();
        trayIcon.Visible = true;
        if (!trayHintShown)
        {
            trayHintShown = true;
            trayIcon.ShowBalloonTip(3000, "Controller Relay Server", "Still running. Double-click this icon to open it again.", ToolTipIcon.Info);
        }
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        trayIcon.Visible = false;
    }

    static IEnumerable<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString());

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refreshTimer.Stop();
        server?.Dispose();
        server = null;
        vigem?.Dispose();
        trayIcon.Dispose();
        base.OnFormClosed(e);
    }
}
