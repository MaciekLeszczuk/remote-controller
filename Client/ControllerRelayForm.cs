using System.Runtime.InteropServices;

// Status window shown while relaying; closing it stops the relay. Locks the screen after
// AutoLockSeconds without touch, mouse or keyboard input (controller input does not count).
sealed class ControllerRelayForm : Form
{
    const int AutoLockSeconds = 10;

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    readonly Relay relay;
    readonly ClientSettings settings;
    readonly GamepadView gamepadView = new() { Dock = DockStyle.Fill };
    readonly Label controllerValue = ValueLabel();
    readonly Label linkValue = ValueLabel();
    readonly Label rumbleValue = ValueLabel();
    readonly CheckBox autoLock = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 33 };
    bool locked;

    public bool BackToServerList { get; private set; }

    public ControllerRelayForm(Relay relay, string serverName, ClientSettings settings)
    {
        this.relay = relay;
        this.settings = settings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Controller Relay";
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(560, 540);
        MinimumSize = new Size(440, 460);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(gamepadView, 0, 0);
        layout.SetColumnSpan(gamepadView, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var serverValue = ValueLabel();
        serverValue.Text = serverName;
        AddRow(layout, "Server:", serverValue);
        AddRow(layout, "Controller:", controllerValue);
        AddRow(layout, "Connection:", linkValue);
        AddRow(layout, "Rumble:", rumbleValue);

        autoLock.Checked = settings.AutoScreenOff;
        autoLock.CheckedChanged += (_, _) => { settings.AutoScreenOff = autoLock.Checked; settings.Save(); };
        layout.Controls.Add(autoLock, 0, layout.RowStyles.Count);
        layout.SetColumnSpan(autoLock, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(TouchButton("Exit", (_, _) => Close()));
        buttons.Controls.Add(TouchButton("Disconnect", (_, _) => { BackToServerList = true; Close(); }));
        buttons.Controls.Add(TouchButton("Test rumble", (_, _) => relay.TestRumble()));
        buttons.Controls.Add(TouchButton("Screen off", (_, _) => LockScreen()));
        layout.Controls.Add(buttons, 0, layout.RowStyles.Count);
        layout.SetColumnSpan(buttons, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(layout);
        ResumeLayout(false);
        PerformLayout();

        refreshTimer.Tick += (_, _) => RefreshStatus();
        refreshTimer.Start();
        RefreshStatus();
    }

    static Label ValueLabel() => new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };

    static Button TouchButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        button.Click += click;
        return button;
    }

    static void AddRow(TableLayoutPanel layout, string caption, Label value)
    {
        int row = layout.RowStyles.Count;
        layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(value, 1, row);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
    }

    void RefreshStatus()
    {
        int index = relay.ControllerIndex;
        bool responding = relay.ServerResponding;
        gamepadView.ShowState(relay.State, relay.RumbleLarge, relay.RumbleSmall, index >= 0, index >= 0 ? "" : "No controller");

        SetLabel(controllerValue, index >= 0 ? $"Connected (XInput slot {index + 1})" : "Not detected - connect a controller",
            index >= 0 ? Color.ForestGreen : Color.Firebrick);
        SetLabel(linkValue, responding ? $"OK - {relay.RoundTripMs} ms round trip" : "No response - check secret and firewall",
            responding ? Color.ForestGreen : Color.Firebrick);

        int error = relay.RumbleError;
        byte large = relay.RumbleLarge, small = relay.RumbleSmall;
        SetLabel(rumbleValue,
            error != 0 ? $"Controller rejected rumble (error {error})"
            : large == 0 && small == 0 ? "Off"
            : $"Strong {large * 100 / 255}%, weak {small * 100 / 255}%",
            error != 0 ? Color.Firebrick : SystemColors.ControlText);

        string autoLockText = $"Turn the screen off after {AutoLockSeconds} s without touch";
        if (autoLock.Checked && !locked)
        {
            if (!responding)
            {
                autoLockText += " (waiting for the server)";
            }
            else
            {
                int remaining = AutoLockSeconds - IdleSeconds();
                if (remaining <= 0) LockScreen();
                else autoLockText += $" (in {remaining} s)";
            }
        }
        if (autoLock.Text != autoLockText) autoLock.Text = autoLockText;
    }

    static void SetLabel(Label label, string text, Color color)
    {
        if (label.Text != text) label.Text = text;
        if (label.ForeColor != color) label.ForeColor = color;
    }

    static int IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? (int)(((uint)Environment.TickCount - info.dwTime) / 1000) : 0;
    }

    void LockScreen()
    {
        if (locked) return;
        locked = true;
        // Deferred so the modal loop doesn't run inside a timer tick or click handler.
        BeginInvoke(new Action(() =>
        {
            using (var lockScreen = new LockScreenForm(relay, settings)) lockScreen.ShowDialog(this);
            locked = false;
            Activate();
        }));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refreshTimer.Stop();
        relay.Dispose();
        base.OnFormClosed(e);
    }
}
