using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Streams the local controller to a server and plays back the rumble it sends.
sealed class Relay : IDisposable
{
    const int PollIntervalMs = 2;
    // Microsoft advises against polling empty XInput slots every frame; it is comparatively slow.
    const int RescanIntervalMs = 1000;
    // The server repeats rumble every FeedbackIntervalMs, so silence this long means the link is gone.
    const int LinkTimeoutMs = 1000;

    const uint ES_CONTINUOUS = 0x80000000;
    const uint ES_SYSTEM_REQUIRED = 0x00000001;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint uPeriod);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint esFlags);

    readonly Socket socket;
    readonly IPEndPoint server;
    readonly byte[] key;
    readonly uint session = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    readonly Thread pollThread, listenThread;
    readonly object rumbleGate = new();
    readonly object stateGate = new();
    volatile bool running = true;
    volatile int controllerIndex = -1;
    volatile int roundTripMs;
    long lastFeedbackAt = -1;
    GamepadState currentState;
    volatile byte rumbleLarge, rumbleSmall;
    volatile int rumbleError;

    public Relay(IPEndPoint server, byte[] key, int localPort)
    {
        this.server = server;
        this.key = key;
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Protocol.DisableConnectionReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, localPort));

        pollThread = new Thread(PollLoop) { IsBackground = true, Name = "XInput poll", Priority = ThreadPriority.AboveNormal };
        listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "Feedback listener" };
        pollThread.Start();
        listenThread.Start();
    }

    public int ControllerIndex => controllerIndex;
    public int RoundTripMs => roundTripMs;
    public byte RumbleLarge => rumbleLarge;
    public byte RumbleSmall => rumbleSmall;
    // Result of the last XInputSetState call; non-zero means the controller rejected rumble.
    public int RumbleError => rumbleError;

    // The state most recently sent to the server.
    public GamepadState State
    {
        get { lock (stateGate) return currentState; }
    }

    public bool ServerResponding
    {
        get
        {
            long last = Interlocked.Read(ref lastFeedbackAt);
            return last >= 0 && Environment.TickCount64 - last < LinkTimeoutMs;
        }
    }

    void PollLoop()
    {
        var buffer = new byte[Protocol.MaxPacketSize];
        uint seq = 0;
        GamepadState last = default;
        long lastSent = 0, lastScan = -RescanIntervalMs;

        timeBeginPeriod(1); // lets Thread.Sleep(2) actually sleep ~2 ms instead of ~15.6 ms
        // Controller input doesn't count as user activity, so without this Windows would go to sleep
        // mid-game. The display must be kept on too: on Modern Standby devices (ROG Ally) the display
        // turning off is what puts the machine to sleep. The lock screen dims it instead.
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        try
        {
            while (running)
            {
                long now = Environment.TickCount64;
                GamepadState state = default;
                int index = controllerIndex;
                if (index >= 0 && !XInput.TryGetState(index, out state))
                    controllerIndex = index = -1;
                if (index < 0 && now - lastScan >= RescanIntervalMs)
                {
                    lastScan = now;
                    controllerIndex = index = FindController(out state);
                }
                lock (stateGate) currentState = state;

                // A missing controller is sent as neutral so nothing stays pressed on the server.
                if (state != last || now - lastSent >= Protocol.KeepAliveMs)
                {
                    Send(buffer, Protocol.WriteInput(buffer, session, ++seq, (uint)now, state));
                    last = state;
                    lastSent = now;
                }

                if (!ServerResponding) SetRumble(0, 0);
                Thread.Sleep(PollIntervalMs);
            }

            // Tell the server right away instead of letting it time out; repeated in case one is lost.
            int length = Protocol.WriteBye(buffer, session, ++seq);
            for (int i = 0; i < 3; i++) Send(buffer, length);
        }
        finally
        {
            SetThreadExecutionState(ES_CONTINUOUS);
            timeEndPeriod(1);
        }
    }

    static int FindController(out GamepadState state)
    {
        for (int i = 0; i < XInput.MaxControllers; i++)
            if (XInput.TryGetState(i, out state)) return i;
        state = default;
        return -1;
    }

    void Send(byte[] buffer, int payloadLength)
    {
        int length = Protocol.Seal(key, buffer, payloadLength);
        try { socket.SendTo(buffer, length, SocketFlags.None, server); } catch (SocketException) { }
    }

    void ListenLoop()
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int length;
            try
            {
                length = socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException)
            {
                if (!running) break;
                Thread.Sleep(10);
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (Protocol.Open(key, buffer, length) != Protocol.FeedbackSize || buffer[0] != Protocol.MsgFeedback) continue;
            Protocol.ReadFeedback(buffer, out uint id, out byte large, out byte small, out uint echoTime, out ushort holdMs);
            if (id != session) continue;

            long now = Environment.TickCount64;
            roundTripMs = Math.Max(0, (int)((uint)now - echoTime - holdMs));
            Interlocked.Exchange(ref lastFeedbackAt, now);
            SetRumble(large, small);
        }
    }

    void SetRumble(byte large, byte small)
    {
        lock (rumbleGate)
        {
            int index = controllerIndex;
            if (index < 0 || (large == rumbleLarge && small == rumbleSmall)) return;
            rumbleLarge = large;
            rumbleSmall = small;
            rumbleError = XInput.SetRumble(index, large, small);
        }
    }

    // Pulses the local motors without involving the server, to tell local and network problems apart.
    public void TestRumble()
    {
        int index = controllerIndex;
        if (index < 0) return;
        lock (rumbleGate) rumbleError = XInput.SetRumble(index, 255, 255);
        Task.Delay(500).ContinueWith(_ =>
        {
            lock (rumbleGate) rumbleError = XInput.SetRumble(index, rumbleLarge, rumbleSmall);
        });
    }

    public void Dispose()
    {
        running = false;
        pollThread.Join(500);
        SetRumble(0, 0);
        socket.Dispose();
        listenThread.Join(500);
    }
}
