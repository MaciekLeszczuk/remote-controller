using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

// Full-screen black window shown while relaying, so the handheld's screen stays dark and nothing
// on it reacts to the controller:
//  - the screen is black and the panel brightness is set to 0 (turning the display off is not an
//    option: on Modern Standby devices like the ROG Ally that puts the whole machine to sleep);
//  - the window stays in front, so apps that read the controller only while focused get nothing;
//  - all keyboard input (except volume/media keys) and all mouse input that does not come from
//    the touch screen is swallowed system-wide, which also catches controller-to-mouse emulation;
//  - it unlocks only when a finger is held on the screen for HoldMs and then lifted.
sealed class LockScreenForm : Form
{
    const int HoldMs = 5_000;
    // How long the hint stays visible after a touch before the screen goes dark again.
    const int WakeMs = 2_500;
    // Failsafe: unlock if the server stops answering, since relaying is pointless then.
    const int ServerLostMs = 15_000;
    const int RefocusIntervalMs = 2_000;

    readonly Relay relay;
    readonly ClientSettings settings;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };
    readonly HashSet<uint> touches = new();
    readonly HookProc mouseHookProc, keyboardHookProc; // fields keep the delegates alive while hooked
    readonly Font largeFont, smallFont;
    IntPtr mouseHook, keyboardHook;
    int? originalBrightness;
    // WMI calls take tens of milliseconds, so they run in order off the UI thread.
    Task brightnessTask = Task.CompletedTask;
    long holdStartedAt = -1, wokeAt, lastServerOkAt, lastRefocusAt;
    bool awake = true, unlocked;

    public LockScreenForm(Relay relay, ClientSettings settings)
    {
        this.relay = relay;
        this.settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ForeColor = Color.FromArgb(120, 120, 120);
        Font = new Font("Segoe UI", 15F);
        largeFont = new Font("Segoe UI", 36F);
        smallFont = new Font("Segoe UI", 10F);
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        mouseHookProc = MouseHook;
        keyboardHookProc = KeyboardHook;
        timer.Tick += (_, _) => Tick();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Bounds = Screen.FromControl(Owner ?? this).Bounds;
        IntPtr module = GetModuleHandle(null);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseHookProc, module, 0);
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardHookProc, module, 0);

        // Remembered in the settings too, so the next start can restore it if this process dies while dark.
        originalBrightness = Brightness.Get();
        if (originalBrightness != null)
        {
            settings.BrightnessToRestore = originalBrightness;
            settings.Save();
        }

        Cursor.Hide();
        wokeAt = lastServerOkAt = Environment.TickCount64;
        timer.Start();
    }

    void Tick()
    {
        long now = Environment.TickCount64;
        if (relay.ServerResponding) lastServerOkAt = now;
        else if (now - lastServerOkAt >= ServerLostMs)
        {
            Unlock();
            return;
        }

        if (awake && holdStartedAt < 0 && now - wokeAt >= WakeMs) GoDark();
        if (awake) Invalidate();

        if (ActiveForm != this && now - lastRefocusAt >= RefocusIntervalMs)
        {
            lastRefocusAt = now;
            Activate();
        }
    }

    void GoDark()
    {
        awake = false;
        Invalidate();
        SetBrightnessLater(0);
    }

    void WakeUp()
    {
        wokeAt = Environment.TickCount64;
        if (awake) return;
        awake = true;
        if (originalBrightness is int brightness) SetBrightnessLater(brightness);
        Invalidate();
    }

    void SetBrightnessLater(int percent)
    {
        if (originalBrightness == null) return;
        brightnessTask = brightnessTask.ContinueWith(_ => Brightness.Set(percent), TaskScheduler.Default);
    }

    void RestoreBrightness()
    {
        if (originalBrightness is not int brightness) return;
        originalBrightness = null;
        brightnessTask.Wait(2000);
        Brightness.Set(brightness);
        settings.BrightnessToRestore = null;
        settings.Save();
    }

    void TouchDown(uint id)
    {
        touches.Add(id);
        if (holdStartedAt < 0) holdStartedAt = Environment.TickCount64;
        WakeUp();
        Invalidate();
    }

    void TouchUp(uint id)
    {
        if (!touches.Remove(id) || touches.Count > 0) return;
        long now = Environment.TickCount64;
        bool complete = holdStartedAt >= 0 && now - holdStartedAt >= HoldMs;
        holdStartedAt = -1;
        wokeAt = now;
        // Unlocking on release rather than at the HoldMs mark keeps the lifted finger from
        // tapping whatever is underneath in the window that reappears.
        if (complete) Unlock();
        else Invalidate();
    }

    void Unlock()
    {
        unlocked = true;
        RemoveHooks();
        RestoreBrightness();
        DialogResult = DialogResult.OK;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_POINTERDOWN:
            case WM_POINTERUP:
            case WM_POINTERCAPTURECHANGED:
            {
                uint id = (uint)(m.WParam.ToInt64() & 0xFFFF);
                if (m.Msg != WM_POINTERDOWN) TouchUp(id);
                else if (GetPointerType(id, out uint type) && type is PT_TOUCH or PT_PEN) TouchDown(id);
                // Not passed on, so Windows doesn't turn the touch into clicks or a press-and-hold right click.
                m.Result = IntPtr.Zero;
                return;
            }
            case WM_POINTERUPDATE:
                m.Result = IntPtr.Zero;
                return;
            case WM_TABLET_QUERYSYSTEMGESTURESTATUS:
                m.Result = (IntPtr)(TABLET_DISABLE_PRESSANDHOLD | TABLET_DISABLE_PENTAPFEEDBACK | TABLET_DISABLE_PENBARRELFEEDBACK | TABLET_DISABLE_FLICKS);
                return;
        }
        base.WndProc(ref m);
    }

    IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            // Mouse events generated from touch or pen carry this signature; everything else is blocked.
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.dwExtraInfo.ToInt64() & 0xFFFFFF00) != MI_WP_SIGNATURE) return (IntPtr)1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool volumeOrMedia = info.vkCode is >= VK_VOLUME_MUTE and <= VK_MEDIA_PLAY_PAUSE;
            if (!volumeOrMedia) return (IntPtr)1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void RemoveHooks()
    {
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
        mouseHook = keyboardHook = IntPtr.Zero;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!awake) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        float cx = ClientSize.Width / 2f, cy = ClientSize.Height / 2f;

        if (holdStartedAt >= 0)
        {
            long held = Environment.TickCount64 - holdStartedAt;
            float progress = Math.Min(1f, held / (float)HoldMs);
            float radius = 70 * scale;
            var ring = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using var track = new Pen(Color.FromArgb(45, 45, 45), 10 * scale);
            using var bar = new Pen(Color.FromArgb(0, 120, 215), 10 * scale);
            g.DrawEllipse(track, ring);
            if (progress > 0) g.DrawArc(bar, ring, -90, 360 * progress);
            DrawCentered(g, progress >= 1 ? "OK" : ((HoldMs - held + 999) / 1000).ToString(), largeFont, cy);
            DrawCentered(g, progress >= 1 ? "Release to unlock" : "Keep holding to unlock", Font, cy + radius + 40 * scale);
        }
        else
        {
            DrawCentered(g, $"Touch and hold for {HoldMs / 1000} seconds to unlock", Font, cy);
        }

        string controller = relay.ControllerIndex >= 0 ? "Controller connected" : "No controller";
        string link = relay.ServerResponding ? $"server OK ({relay.RoundTripMs} ms)" : "no response from server";
        DrawCentered(g, $"{controller}  ·  {link}", smallFont, ClientSize.Height - 50 * scale);
    }

    void DrawCentered(Graphics g, string text, Font font, float centerY)
    {
        var size = g.MeasureString(text, font);
        using var brush = new SolidBrush(ForeColor);
        g.DrawString(text, font, brush, (ClientSize.Width - size.Width) / 2, centerY - size.Height / 2);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 and similar can't close the lock; only unlocking (or Windows shutting down) can.
        if (!unlocked && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        RemoveHooks();
        RestoreBrightness();
        Cursor.Show();
        largeFont.Dispose();
        smallFont.Dispose();
        base.OnFormClosed(e);
    }

    const int WH_KEYBOARD_LL = 13;
    const int WH_MOUSE_LL = 14;
    const int WM_POINTERUPDATE = 0x0245;
    const int WM_POINTERDOWN = 0x0246;
    const int WM_POINTERUP = 0x0247;
    const int WM_POINTERCAPTURECHANGED = 0x024C;
    const int WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;
    const int TABLET_DISABLE_PRESSANDHOLD = 0x00000001;
    const int TABLET_DISABLE_PENTAPFEEDBACK = 0x00000008;
    const int TABLET_DISABLE_PENBARRELFEEDBACK = 0x00000010;
    const int TABLET_DISABLE_FLICKS = 0x00010000;
    const uint PT_TOUCH = 2;
    const uint PT_PEN = 3;
    const long MI_WP_SIGNATURE = 0xFF515700;
    const uint VK_VOLUME_MUTE = 0xAD;
    const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    static extern bool GetPointerType(uint pointerId, out uint pointerType);
}
