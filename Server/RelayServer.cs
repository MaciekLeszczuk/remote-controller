using System.Net;
using System.Net.Sockets;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// What the window shows for one connected client.
record ClientInfo(IPAddress Address, string Name, int Player, GamepadState State,
    byte RumbleLarge, byte RumbleSmall, int PacketsPerSecond, bool Silent);

// Owns one virtual Xbox 360 controller per client machine and keeps it in sync with that client.
// Runs on its own thread from construction until Dispose.
sealed class RelayServer : IDisposable
{
    sealed class Session
    {
        public required IXbox360Controller Pad;
        public required IPEndPoint EndPoint;
        public string Name = "";
        public int Player;
        public uint Id, LastSeq, EchoTime;
        public long LastSeen, EchoReceivedAt, LastFeedbackSent, TestUntil;
        public int Packets, PacketsPerSecond;
        public GamepadState Applied;
        public byte Large, Small, TestLarge, TestSmall;
        public bool Closed;

        public bool Testing(long now) => now < TestUntil;
    }

    static readonly Xbox360Button[] Buttons =
    {
        Xbox360Button.Up, Xbox360Button.Down, Xbox360Button.Left, Xbox360Button.Right,
        Xbox360Button.Start, Xbox360Button.Back, Xbox360Button.LeftThumb, Xbox360Button.RightThumb,
        Xbox360Button.LeftShoulder, Xbox360Button.RightShoulder, Xbox360Button.Guide,
        Xbox360Button.A, Xbox360Button.B, Xbox360Button.X, Xbox360Button.Y,
    };

    const int HousekeepingIntervalMs = 20;
    const int StatsIntervalMs = 1000;
    const int WarningIntervalMs = 5000;

    readonly ViGEmClient vigem;
    readonly byte[] key;
    readonly Action<string> log;
    readonly Socket socket;
    readonly Socket? discoverySocket;
    readonly Thread thread;
    // Keyed by IP so a client that crashed and restarted gets its old virtual controller back.
    readonly Dictionary<IPAddress, Session> sessions = new();
    readonly Dictionary<IPAddress, long> lastWarning = new();
    readonly object gate = new();
    readonly byte[] sendBuffer = new byte[Protocol.MaxPacketSize];
    volatile bool running = true;

    // Throws SocketException when the port is already in use.
    public RelayServer(ViGEmClient vigem, int port, byte[] key, Action<string> log)
    {
        this.vigem = vigem;
        this.key = key;
        this.log = log;

        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            Protocol.DisableConnectionReset(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        try
        {
            discoverySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Protocol.DisableConnectionReset(discoverySocket);
            discoverySocket.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
            var response = Protocol.WriteDiscoveryResponse(port, Environment.MachineName);
            var discoveryThread = new Thread(() => AnswerDiscovery(discoverySocket, response)) { IsBackground = true, Name = "Discovery" };
            discoveryThread.Start();
        }
        catch (SocketException ex)
        {
            discoverySocket?.Dispose();
            discoverySocket = null;
            Log($"Discovery disabled (UDP {Protocol.DiscoveryPort}): {ex.Message}. Clients can still connect by IP.");
        }

        thread = new Thread(Run) { IsBackground = true, Name = "Relay server" };
        thread.Start();
    }

    public List<ClientInfo> GetClients()
    {
        long now = Environment.TickCount64;
        lock (gate)
        {
            return sessions.Values
                .Select(s => new ClientInfo(s.EndPoint.Address, s.Name, s.Player, s.Applied,
                    s.Testing(now) ? s.TestLarge : s.Large, s.Testing(now) ? s.TestSmall : s.Small,
                    s.PacketsPerSecond, now - s.LastSeen >= Protocol.NeutralTimeoutMs))
                .OrderBy(c => c.Address.ToString())
                .ToList();
        }
    }

    // Overrides the game's rumble for a while, to check the motors on the client.
    public void TestRumble(IPAddress address, byte large, byte small, int durationMs)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(address, out var session)) return;
            long now = Environment.TickCount64;
            session.TestLarge = large;
            session.TestSmall = small;
            session.TestUntil = now + durationMs;
            SendFeedback(session, now);
        }
    }

    void AnswerDiscovery(Socket discovery, byte[] response)
    {
        var buffer = new byte[64];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                int length = discovery.ReceiveFrom(buffer, ref from);
                if (length >= 1 && buffer[0] == Protocol.DiscoveryRequest)
                    discovery.SendTo(response, from);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { break; }
        }
    }

    void Run()
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        long lastHousekeeping = 0, lastStats = 0;
        while (running)
        {
            if (socket.Poll(HousekeepingIntervalMs * 1000, SelectMode.SelectRead))
            {
                try
                {
                    int length = socket.ReceiveFrom(buffer, ref from);
                    HandlePacket(buffer, length, (IPEndPoint)from);
                }
                catch (SocketException) { }
            }

            long now = Environment.TickCount64;
            if (now - lastHousekeeping >= HousekeepingIntervalMs)
            {
                lastHousekeeping = now;
                bool stats = now - lastStats >= StatsIntervalMs;
                if (stats) lastStats = now;
                Housekeeping(now, stats);
            }
        }
    }

    void HandlePacket(byte[] buffer, int length, IPEndPoint from)
    {
        int payload = Protocol.Open(key, buffer, length);
        if (payload < 0)
        {
            Warn(from, "rejected a packet with an invalid signature - does the client use the same secret?");
        }
        else if (buffer[0] == Protocol.MsgInput && payload == Protocol.InputSize)
        {
            Protocol.ReadInput(buffer, out uint id, out uint seq, out uint time, out var state);
            OnInput(from, id, seq, time, state);
        }
        else if (buffer[0] == Protocol.MsgBye && payload == Protocol.ByeSize)
        {
            Session? session;
            lock (gate)
            {
                if (sessions.TryGetValue(from.Address, out session) && session.Id == Protocol.ReadSession(buffer))
                    Detach(session);
                else
                    session = null;
            }
            if (session != null) Unplug(session, "client disconnected");
        }
        else
        {
            Warn(from, $"unexpected packet (type {buffer[0]}, {payload} bytes) - are client and server the same version?");
        }
    }

    void OnInput(IPEndPoint from, uint id, uint seq, uint time, GamepadState state)
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (!sessions.TryGetValue(from.Address, out var session))
            {
                session = Plug(from, id);
                if (session == null) return;
            }
            else if (session.Id != id)
            {
                Log($"{from.Address}: client restarted");
                session.Id = id;
            }
            else if (!Protocol.IsNewer(seq, session.LastSeq))
            {
                return; // late or duplicated datagram - a newer state was already applied
            }

            session.LastSeq = seq;
            session.EndPoint = from;
            session.LastSeen = now;
            session.EchoTime = time;
            session.EchoReceivedAt = now;
            session.Packets++;
            Apply(session, state);
        }
    }

    // Caller holds the lock.
    Session? Plug(IPEndPoint from, uint id)
    {
        IXbox360Controller pad;
        try
        {
            pad = vigem.CreateXbox360Controller();
            // Submit each packet as one report instead of one per button/axis.
            pad.AutoSubmitReport = false;
            pad.Connect();
        }
        catch (Exception ex)
        {
            Warn(from, "could not create a virtual controller: " + ex.Message);
            return null;
        }

        var session = new Session { Pad = pad, EndPoint = from, Id = id };
        pad.FeedbackReceived += (_, e) => OnRumble(session, e.LargeMotor, e.SmallMotor);
        sessions[from.Address] = session;
        Log($"{from.Address}: connected, virtual controller plugged in ({sessions.Count} active)");

        // Best effort: show the client's computer name instead of just its IP.
        Dns.GetHostEntryAsync(from.Address).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) { _ = t.Exception; return; }
            string name = t.Result.HostName.Split('.')[0];
            if (name.Length == 0 || name == from.Address.ToString()) return;
            lock (gate) session.Name = name;
        }, TaskScheduler.Default);
        return session;
    }

    // Called on ViGEm's notification thread whenever a game changes rumble.
    void OnRumble(Session session, byte large, byte small)
    {
        lock (gate)
        {
            if (session.Closed) return;
            session.Large = large;
            session.Small = small;
            SendFeedback(session, Environment.TickCount64);
        }
    }

    void Housekeeping(long now, bool stats)
    {
        List<Session>? expired = null;
        lock (gate)
        {
            foreach (var session in sessions.Values)
            {
                if (stats)
                {
                    session.PacketsPerSecond = session.Packets;
                    session.Packets = 0;
                    if (session.Player == 0)
                    {
                        try { session.Player = session.Pad.UserIndex + 1; } catch (Exception) { }
                    }
                }

                if (session.TestUntil != 0 && !session.Testing(now))
                {
                    session.TestUntil = 0;
                    SendFeedback(session, now); // back to the game's rumble
                }

                long idle = now - session.LastSeen;
                if (idle >= Protocol.DisconnectTimeoutMs)
                {
                    (expired ??= new()).Add(session);
                }
                else if (idle >= Protocol.NeutralTimeoutMs)
                {
                    if (session.Applied != default)
                    {
                        Log($"{session.EndPoint.Address}: no input for {idle} ms, releasing all buttons");
                        Apply(session, default);
                    }
                }
                else if (now - session.LastFeedbackSent >= Protocol.FeedbackIntervalMs)
                {
                    SendFeedback(session, now);
                }
            }
            if (expired != null)
                foreach (var session in expired) Detach(session);
        }
        // Disconnect outside the lock: it waits for ViGEm's notification thread, which may be blocked in OnRumble.
        if (expired != null)
            foreach (var session in expired) Unplug(session, "timed out");
    }

    // Caller holds the lock.
    void SendFeedback(Session session, long now)
    {
        bool testing = session.Testing(now);
        ushort hold = (ushort)Math.Min(now - session.EchoReceivedAt, ushort.MaxValue);
        int length = Protocol.WriteFeedback(sendBuffer, session.Id,
            testing ? session.TestLarge : session.Large, testing ? session.TestSmall : session.Small, session.EchoTime, hold);
        length = Protocol.Seal(key, sendBuffer, length);
        try { socket.SendTo(sendBuffer, length, SocketFlags.None, session.EndPoint); } catch (SocketException) { }
        session.LastFeedbackSent = now;
    }

    // Caller holds the lock.
    void Apply(Session session, GamepadState state)
    {
        if (state == session.Applied) return;
        var pad = session.Pad;
        try
        {
            foreach (var button in Buttons)
                pad.SetButtonState(button, (state.Buttons & button.Value) != 0);
            pad.SetAxisValue(Xbox360Axis.LeftThumbX, state.LeftX);
            pad.SetAxisValue(Xbox360Axis.LeftThumbY, state.LeftY);
            pad.SetAxisValue(Xbox360Axis.RightThumbX, state.RightX);
            pad.SetAxisValue(Xbox360Axis.RightThumbY, state.RightY);
            pad.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
            pad.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);
            pad.SubmitReport();
            session.Applied = state;
        }
        catch (Exception ex)
        {
            Log($"{session.EndPoint.Address}: failed to update the virtual controller: {ex.Message}");
        }
    }

    // Caller holds the lock.
    void Detach(Session session)
    {
        sessions.Remove(session.EndPoint.Address);
        session.Closed = true;
    }

    void Unplug(Session session, string reason)
    {
        try { session.Pad.Disconnect(); } catch (Exception) { }
        Log($"{session.EndPoint.Address}: {reason}, virtual controller unplugged");
    }

    void Warn(IPEndPoint from, string message)
    {
        long now = Environment.TickCount64;
        lock (gate)
        {
            if (lastWarning.TryGetValue(from.Address, out long last) && now - last < WarningIntervalMs) return;
            lastWarning[from.Address] = now;
        }
        Log($"{from}: {message}");
    }

    void Log(string message) => log($"[{DateTime.Now:HH:mm:ss}] {message}");

    public void Dispose()
    {
        running = false;
        thread.Join();
        discoverySocket?.Dispose();
        List<Session> all;
        lock (gate)
        {
            all = sessions.Values.ToList();
            foreach (var session in all) Detach(session);
        }
        foreach (var session in all) Unplug(session, "server stopped");
        socket.Dispose();
    }
}
