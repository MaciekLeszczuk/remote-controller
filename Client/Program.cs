using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_GAMEPAD {
    public ushort wButtons;
    public byte bLeftTrigger;
    public byte bRightTrigger;
    public short sThumbLX;
    public short sThumbLY;
    public short sThumbRX;
    public short sThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed; public ushort wRightMotorSpeed; }

// SetupDi structures
[StructLayout(LayoutKind.Sequential)]
struct SP_DEVINFO_DATA
{
    public uint cbSize;
    public Guid ClassGuid;
    public uint DevInst;
    public IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
struct SP_DEVICE_INTERFACE_DATA
{
    public uint cbSize;
    public Guid InterfaceClassGuid;
    public uint Flags;
    public IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct SP_DEVICE_INTERFACE_DETAIL_DATA
{
    public uint cbSize;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string DevicePath;
}

// Discovery form
class ServerDiscoveryForm : Form
{
    private Label label1;
    private ListBox serverList;
    private TextBox secretTextBox;
    private Button scanButton;
    private Button connectButton;
    private Button manualButton;
    private Label statusLabel;
    private string selectedServer = "";
    private int selectedPort = 7777;

    public ServerDiscoveryForm()
    {
        this.Text = "Controller Relay - Server Discovery";
        this.Width = 450;
        this.Height = 350;
        this.StartPosition = FormStartPosition.CenterScreen;

        label1 = new Label() { Text = "Available Servers:", Left = 10, Top = 10, Width = 150 };
        this.Controls.Add(label1);

        serverList = new ListBox() { Left = 10, Top = 35, Width = 410, Height = 150 };
        this.Controls.Add(serverList);

        Label secretLabel = new Label() { Text = "Secret (password):", Left = 10, Top = 195, Width = 150 };
        this.Controls.Add(secretLabel);

        secretTextBox = new TextBox() { Left = 10, Top = 215, Width = 410, Height = 25, Text = "secret" };
        this.Controls.Add(secretTextBox);

        statusLabel = new Label() { Text = "Click 'Scan Network' to find servers...", Left = 10, Top = 245, Width = 410, Height = 20, ForeColor = System.Drawing.Color.Blue };
        this.Controls.Add(statusLabel);

        scanButton = new Button() { Text = "Scan Network", Left = 10, Top = 270, Width = 95 };
        scanButton.Click += ScanButton_Click;
        this.Controls.Add(scanButton);

        connectButton = new Button() { Text = "Connect", Left = 115, Top = 270, Width = 95, Enabled = false };
        connectButton.Click += ConnectButton_Click;
        this.Controls.Add(connectButton);

        manualButton = new Button() { Text = "Manual Entry", Left = 220, Top = 270, Width = 95 };
        manualButton.Click += ManualButton_Click;
        this.Controls.Add(manualButton);

        this.FormClosing += (s, e) => { Application.Exit(); };
    }

    private void ScanButton_Click(object sender, EventArgs e)
    {
        scanButton.Enabled = false;
        statusLabel.Text = "Scanning network...";
        statusLabel.ForeColor = System.Drawing.Color.Blue;
        serverList.Items.Clear();

        var scanThread = new Thread(() => {
            Dictionary<string, int> discoveredServers = new Dictionary<string, int>();
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.EnableBroadcast = true;

                    // Send discovery broadcast
                    byte[] discoveryPacket = new byte[] { 0xAA };
                    udp.Send(discoveryPacket, discoveryPacket.Length, new IPEndPoint(IPAddress.Broadcast, 7776));

                    // Listen for responses (timeout 2 seconds)
                    udp.Client.ReceiveTimeout = 2000;
                    var ep = new IPEndPoint(IPAddress.Any, 0);

                    while (true)
                    {
                        try
                        {
                            var response = udp.Receive(ref ep);
                            if (response.Length >= 5 && response[0] == 0xBB)
                            {
                                int port = BitConverter.ToInt32(response, 1);
                                string serverAddr = ep.Address.ToString();
                                if (!discoveredServers.ContainsKey(serverAddr))
                                {
                                    discoveredServers[serverAddr] = port;
                                }
                            }
                        }
                        catch (SocketException)
                        {
                            break; // Timeout - no more responses
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus("Scan error: " + ex.Message, System.Drawing.Color.Red);
            }

            this.Invoke((MethodInvoker)(() => {
                serverList.Items.Clear();
                if (discoveredServers.Count == 0)
                {
                    SetStatus("No servers found. Try 'Manual Entry'.", System.Drawing.Color.Orange);
                }
                else
                {
                    foreach (var srv in discoveredServers)
                    {
                        serverList.Items.Add($"{srv.Key}:{srv.Value}");
                    }
                    SetStatus($"Found {discoveredServers.Count} server(s).", System.Drawing.Color.Green);
                    connectButton.Enabled = true;
                }
                scanButton.Enabled = true;
            }));
        });
        scanThread.IsBackground = true;
        scanThread.Start();
    }

    private void ConnectButton_Click(object sender, EventArgs e)
    {
        if (serverList.SelectedIndex < 0)
        {
            MessageBox.Show("Select a server first!");
            return;
        }

        string selected = serverList.SelectedItem.ToString();
        var parts = selected.Split(':');
        selectedServer = parts[0];
        selectedPort = int.Parse(parts[1]);
        string secret = secretTextBox.Text;

        this.Hide();
        StartRelay(selectedServer, selectedPort, secret);
    }

    private void ManualButton_Click(object sender, EventArgs e)
    {
        var manualForm = new Form() { Text = "Manual Server Entry", Width = 300, Height = 150, StartPosition = FormStartPosition.CenterParent };

        Label ipLabel = new Label() { Text = "Server IP:", Left = 10, Top = 10, Width = 100 };
        manualForm.Controls.Add(ipLabel);

        TextBox ipBox = new TextBox() { Left = 110, Top = 10, Width = 160, Text = "127.0.0.1" };
        manualForm.Controls.Add(ipBox);

        Label portLabel = new Label() { Text = "Port:", Left = 10, Top = 40, Width = 100 };
        manualForm.Controls.Add(portLabel);

        TextBox portBox = new TextBox() { Left = 110, Top = 40, Width = 160, Text = "7777" };
        manualForm.Controls.Add(portBox);

        Button okBtn = new Button() { Text = "OK", Left = 110, Top = 70, Width = 80, DialogResult = DialogResult.OK };
        manualForm.Controls.Add(okBtn);

        if (manualForm.ShowDialog() == DialogResult.OK)
        {
            selectedServer = ipBox.Text;
            selectedPort = int.Parse(portBox.Text);
            string secret = secretTextBox.Text;
            this.Hide();
            StartRelay(selectedServer, selectedPort, secret);
        }
    }

    private void SetStatus(string msg, System.Drawing.Color color)
    {
        statusLabel.Text = msg;
        statusLabel.ForeColor = color;
    }

    private void StartRelay(string serverIp, int serverPort, string secret)
    {
        int localPort = 7778;
        var udp = new UdpClient(localPort);
        var secretKey = Encoding.UTF8.GetBytes(secret);

        var relayForm = new ControllerRelayForm();
        relayForm.Initialize(udp, serverIp, serverPort, secretKey);
        
        this.Hide();  // Hide discovery form
        relayForm.Show();  // Show relay form - Application.Run loop continues
    }
}

// Overlay window to show status and block clicks
class ClickBlockerOverlay : Form
{
    private Label statusLabel;

    public ClickBlockerOverlay()
    {
        this.Text = "Controller Active";
        this.FormBorderStyle = FormBorderStyle.None;
        this.BackColor = System.Drawing.Color.Black;
        this.TopMost = true;
        this.ShowInTaskbar = true;
        this.ControlBox = false;

        statusLabel = new Label()
        {
            Text = "🎮 Controller Relay Active 🎮\nClick Capture: BLOCKED",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = System.Drawing.Color.Lime,
            Font = new System.Drawing.Font("Arial", 14, System.Drawing.FontStyle.Bold),
            Dock = DockStyle.Fill
        };
        this.Controls.Add(statusLabel);

        this.Width = 380;
        this.Height = 90;
        this.StartPosition = FormStartPosition.Manual;
        this.Left = Screen.PrimaryScreen.Bounds.Width - 400;
        this.Top = 10;
        this.Opacity = 0.85;
    }

    protected override void WndProc(ref Message m)
    {
        // Block mouse clicks (WM_LBUTTONDOWN, WM_LBUTTONUP, WM_RBUTTONDOWN, WM_RBUTTONUP, WM_LBUTTONDBLCLK)
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_RBUTTONDOWN = 0x0204;
        const int WM_RBUTTONUP = 0x0205;
        const int WM_LBUTTONDBLCLK = 0x0203;

        if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONUP || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_RBUTTONUP || m.Msg == WM_LBUTTONDBLCLK)
        {
            // Block the click by returning without calling base
            return;
        }

        base.WndProc(ref m);
    }

    public void UpdateStatus(string message)
    {
        if (statusLabel.InvokeRequired)
        {
            statusLabel.Invoke(() => statusLabel.Text = message);
        }
        else
        {
            statusLabel.Text = message;
        }
    }
}

// Main relay form
class ControllerRelayForm : Form
{
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_EXCLUSIVE = 0x00010000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;

    [DllImport("setupapi.dll")]
    static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("xinput1_4.dll")]
    static extern int XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll")]
    static extern int XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

    private UdpClient? udp;
    private string? serverIp;
    private int serverPort;
    private byte[]? secretKey;
    private uint lastPacketNumber = 0;
    private IntPtr exclusiveHandle = IntPtr.Zero;
    private ClickBlockerOverlay? overlay;

    public ControllerRelayForm()
    {
        this.Text = "Controller Relay";
        this.WindowState = FormWindowState.Minimized;
        this.ShowInTaskbar = false;
    }

    public void Initialize(UdpClient udpClient, string serverIp, int serverPort, byte[] secretKey)
    {
        this.udp = udpClient;
        this.serverIp = serverIp;
        this.serverPort = serverPort;
        this.secretKey = secretKey;

        // Create and show overlay
        overlay = new ClickBlockerOverlay();
        overlay.Show();

        TryGetExclusiveGamepadAccess();

        var listenThread = new Thread(() => {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                try
                {
                    var remote = udp.Receive(ref ep);
                    if (remote.Length >= 21 && remote[0] == 2)
                    {
                        int payloadLen = remote.Length - 16;
                        using var hmac = new HMACSHA256(secretKey);
                        var expectedFull = hmac.ComputeHash(remote, 0, payloadLen);
                        bool ok = true;
                        for (int i = 0; i < 16; i++) if (expectedFull[i] != remote[payloadLen + i]) { ok = false; break; }
                        if (!ok) { Console.WriteLine("Vibration packet HMAC invalid"); continue; }

                        ushort left = BitConverter.ToUInt16(remote, 1);
                        ushort right = BitConverter.ToUInt16(remote, 3);
                        var vib = new XINPUT_VIBRATION { wLeftMotorSpeed = left, wRightMotorSpeed = right };
                        XInputSetState(0, ref vib);
                    }
                }
                catch (Exception ex) { Console.WriteLine("Vib listener: " + ex.Message); }
            }
        });
        listenThread.IsBackground = true;
        listenThread.Start();

        var pollThread = new Thread(() => {
            while (true)
            {
                try
                {
                    XINPUT_STATE state;
                    int res = XInputGetState(0, out state);
                    if (res == 0)
                    {
                        if (state.dwPacketNumber != lastPacketNumber)
                        {
                            lastPacketNumber = state.dwPacketNumber;
                            SendGamepadData(state);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Poll error: " + ex.Message);
                }
                Thread.Sleep(10);
            }
        });
        pollThread.IsBackground = true;
        pollThread.Start();

        Console.WriteLine($"Relaying controller input to {serverIp}:{serverPort}");
    }

    private void TryGetExclusiveGamepadAccess()
    {
        try
        {
            Guid hidGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x10);
            if (deviceInfoSet == IntPtr.Zero)
                return;

            try
            {
                SP_DEVICE_INTERFACE_DATA ifaceData = new SP_DEVICE_INTERFACE_DATA();
                ifaceData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));

                for (uint i = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, i, ref ifaceData); i++)
                {
                    SP_DEVICE_INTERFACE_DETAIL_DATA ifaceDetail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                    ifaceDetail.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DETAIL_DATA));

                    if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData, ref ifaceDetail, (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DETAIL_DATA)), out uint reqSize, IntPtr.Zero))
                    {
                        IntPtr handle = CreateFile(ifaceDetail.DevicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_EXCLUSIVE, IntPtr.Zero);
                        if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                        {
                            exclusiveHandle = handle;
                            Console.WriteLine($"Got exclusive access to HID device");
                            return;
                        }
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error getting exclusive gamepad access: " + ex.Message);
        }
    }

    private void SendGamepadData(XINPUT_STATE state)
    {
        try
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)1);
            bw.Write(state.dwPacketNumber);
            bw.Write(state.Gamepad.wButtons);
            bw.Write(state.Gamepad.bLeftTrigger);
            bw.Write(state.Gamepad.bRightTrigger);
            bw.Write(state.Gamepad.sThumbLX);
            bw.Write(state.Gamepad.sThumbLY);
            bw.Write(state.Gamepad.sThumbRX);
            bw.Write(state.Gamepad.sThumbRY);

            var payload = ms.ToArray();
            using var hmac = new HMACSHA256(secretKey!);
            var fullMac = hmac.ComputeHash(payload);
            var mac = new byte[16];
            Array.Copy(fullMac, 0, mac, 0, 16);
            var data = new byte[payload.Length + mac.Length];
            Array.Copy(payload, 0, data, 0, payload.Length);
            Array.Copy(mac, 0, data, payload.Length, mac.Length);
            udp!.Send(data, data.Length, serverIp, serverPort);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error sending gamepad data: " + ex.Message);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (exclusiveHandle != IntPtr.Zero && exclusiveHandle.ToInt64() != -1)
            CloseHandle(exclusiveHandle);
        if (overlay != null)
            overlay.Close();
        base.OnFormClosing(e);
    }
}

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2)
        {
            // Command-line mode: Client <serverIp> <serverPort> [localPort] [secret]
            var serverIp = args[0];
            var serverPort = int.Parse(args[1]);
            int localPort = args.Length >= 3 ? int.Parse(args[2]) : 7778;
            string secret = args.Length >= 4 ? args[3] : "secret";

            var udp = new UdpClient(localPort);
            var secretKey = Encoding.UTF8.GetBytes(secret);

            var form = new ControllerRelayForm();
            form.Initialize(udp, serverIp, serverPort, secretKey);

            Application.Run(form);
        }
        else
        {
            // GUI mode: show discovery form
            Application.EnableVisualStyles();
            var discoveryForm = new ServerDiscoveryForm();
            Application.Run(discoveryForm);
        }
    }
}
