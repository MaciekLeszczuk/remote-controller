using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.Threading;

class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static void Main(string[] args)
    {
        int port = args.Length >= 1 ? int.Parse(args[0]) : 7777;
        string secret = args.Length >= 2 ? args[1] : "secret";
        var secretKey = Encoding.UTF8.GetBytes(secret);

        var udp = new UdpClient(port);
        Console.WriteLine($"Server listening on UDP port {port}");
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint lastClientEp = null;

        // Start discovery listener on port 7776
        var discoveryThread = new Thread(() => {
            try
            {
                var discoveryUdp = new UdpClient(7776);
                Console.WriteLine("Discovery listener started on port 7776");
                var discoveryEp = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    try
                    {
                        var discData = discoveryUdp.Receive(ref discoveryEp);
                        if (discData.Length > 0 && discData[0] == 0xAA) // Discovery request marker
                        {
                            // Send back server info: [0xBB] [port:4bytes] 
                            byte[] response = new byte[5];
                            response[0] = 0xBB;
                            Array.Copy(BitConverter.GetBytes(port), 0, response, 1, 4);
                            discoveryUdp.Send(response, response.Length, discoveryEp);
                            Console.WriteLine($"Discovery query from {discoveryEp} - sent port {port}");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Discovery listener error: " + ex.Message);
            }
        });
        discoveryThread.IsBackground = true;
        discoveryThread.Start();

        // Try to create a virtual X360 controller to capture vibration events from games.
        ViGEmClient? viClient = null;
        IXbox360Controller? x360 = null;
        // reflection-cached members for ViGEm mapping
        System.Reflection.MethodInfo? rifSetAxis = null;
        System.Reflection.MethodInfo? rifSetSlider = null;
        System.Reflection.MethodInfo? rifSetButton = null;
        object? rifLeftX = null, rifLeftY = null, rifRightX = null, rifRightY = null;
        object? rifLtSlider = null, rifRtSlider = null;
        object? rifABtn = null, rifBBtn = null, rifXBtn = null, rifYBtn = null;
        object? rifLeftShoulderBtn = null, rifRightShoulderBtn = null, rifLeftThumbBtn = null, rifRightThumbBtn = null;
        object? rifStartBtn = null, rifBackBtn = null, rifUpBtn = null, rifDownBtn = null, rifLeftBtn = null, rifRightBtn = null;
        bool vigemReady = false;
        try
        {
            viClient = new ViGEmClient();
            x360 = viClient.CreateXbox360Controller();
            x360.FeedbackReceived += (s, e) => {
                // e.SmallMotor and e.LargeMotor are bytes (0-255)
                byte small = e.SmallMotor;
                byte large = e.LargeMotor;
                ushort left = (ushort)(large * 257); // scale to 0..65535
                ushort right = (ushort)(small * 257);
                if (lastClientEp != null)
                {
                    byte[] vibPayload = new byte[5]; vibPayload[0] = 2;
                    Array.Copy(BitConverter.GetBytes(left), 0, vibPayload, 1, 2);
                    Array.Copy(BitConverter.GetBytes(right), 0, vibPayload, 3, 2);
                    using var vibHmac = new HMACSHA256(secretKey);
                    var vibFull = vibHmac.ComputeHash(vibPayload);
                    var vibMac = new byte[16]; Array.Copy(vibFull, 0, vibMac, 0, 16);
                    var vibMsg = new byte[vibPayload.Length + vibMac.Length];
                    Array.Copy(vibPayload, 0, vibMsg, 0, vibPayload.Length);
                    Array.Copy(vibMac, 0, vibMsg, vibPayload.Length, vibMac.Length);
                    try { udp.Send(vibMsg, vibMsg.Length, lastClientEp); } catch { }
                }
            };
            x360.Connect();
            // Prepare reflection lookups once to avoid per-packet exceptions
            try
            {
                var xType = x360.GetType();
                var asm = xType.Assembly;
                var axisType = asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis");
                var sliderType = asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Slider");
                var buttonType = asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button");

                rifSetAxis = xType.GetMethod("SetAxisValue", new Type[] { axisType, typeof(short) });
                rifSetSlider = xType.GetMethod("SetSliderValue", new Type[] { sliderType, typeof(byte) });
                rifSetButton = xType.GetMethod("SetButtonState", new Type[] { buttonType, typeof(bool) });

                // The ViGEm client exposes nested types for each axis/slider/button. Create instances via Activator.
                rifLeftX = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis+LeftThumbXAxis"));
                rifLeftY = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis+LeftThumbYAxis"));
                rifRightX = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis+RightThumbXAxis"));
                rifRightY = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis+RightThumbYAxis"));

                rifLtSlider = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Slider+LeftTriggerSlider"));
                rifRtSlider = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Slider+RightTriggerSlider"));

                rifABtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+AButton"));
                rifBBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+BButton"));
                rifXBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+XButton"));
                rifYBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+YButton"));
                rifLeftShoulderBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+LeftShoulderButton"));
                rifRightShoulderBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+RightShoulderButton"));
                rifLeftThumbBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+LeftThumbButton"));
                rifRightThumbBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+RightThumbButton"));
                rifStartBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+StartButton"));
                rifBackBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+BackButton"));
                rifUpBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+UpButton"));
                rifDownBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+DownButton"));
                rifLeftBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+LeftButton"));
                rifRightBtn = Activator.CreateInstance(asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button+RightButton"));

                // validate
                if (rifSetAxis == null || rifSetSlider == null || rifSetButton == null || rifLeftX == null)
                {
                    throw new Exception("Reflection lookups incomplete for ViGEm controller API.");
                }

                vigemReady = true;
            }
            catch (Exception rex)
            {
                Console.WriteLine("ViGEm reflection init failed: " + rex.ToString());
                // disable viGEm mapping, fallback to mouse mapping
                vigemReady = false;
            }

                if (vigemReady)
                {
                    Console.WriteLine("Virtual Xbox360 controller created (ViGEm) — vibration capture enabled.");
                    // show which reflection members were found (for debugging)
                    Console.WriteLine($"rifSetAxis={(rifSetAxis!=null ? rifSetAxis.Name : "null")}, rifSetSlider={(rifSetSlider!=null ? rifSetSlider.Name : "null")}, rifSetButton={(rifSetButton!=null ? rifSetButton.Name : "null")}\n");
                    Console.WriteLine($"rifLeftX={(rifLeftX!=null ? rifLeftX.ToString() : "null")}, rifLeftY={(rifLeftY!=null ? rifLeftY.ToString() : "null")}, rifRightX={(rifRightX!=null ? rifRightX.ToString() : "null")}, rifRightY={(rifRightY!=null ? rifRightY.ToString() : "null")}\n");
                    Console.WriteLine($"rifLtSlider={(rifLtSlider!=null ? rifLtSlider.ToString() : "null")}, rifRtSlider={(rifRtSlider!=null ? rifRtSlider.ToString() : "null")}\n");
                    Console.WriteLine($"rifABtn={(rifABtn!=null ? rifABtn.ToString() : "null")}, rifBBtn={(rifBBtn!=null ? rifBBtn.ToString() : "null")}, rifXBtn={(rifXBtn!=null ? rifXBtn.ToString() : "null")}, rifYBtn={(rifYBtn!=null ? rifYBtn.ToString() : "null")}\n");

                    // Quick runtime test: try toggling A button via cached reflection to verify mapping
                    if (rifSetButton == null)
                    {
                        Console.WriteLine("rifSetButton is null — cannot perform SetButtonState test.");
                    }
                    else if (rifABtn == null)
                    {
                        Console.WriteLine("rifABtn is null — A button enum value not found via reflection.");
                    }
                    else
                    {
                        try
                        {
                            Console.WriteLine("Testing ViGEm SetButtonState (A press)...");
                            rifSetButton.Invoke(x360, new object[] { rifABtn, true });
                            Thread.Sleep(150);
                            rifSetButton.Invoke(x360, new object[] { rifABtn, false });
                            Console.WriteLine("ViGEm test SetButtonState succeeded.");
                        }
                        catch (Exception tEx)
                        {
                            Console.WriteLine("ViGEm test SetButtonState failed: " + tEx.ToString());
                        }
                    }
                }
                else
                {
                    Console.WriteLine("ViGEm created but reflection init incomplete — ViGEm mapping disabled, falling back to mouse mapping.");
                    Console.WriteLine("Ensure ViGEmBus driver is installed and run the server as Administrator.");
                }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ViGEm setup failed: " + ex.Message + " — install ViGEmBus driver and Nefarius.ViGEm.Client package to enable vibration capture.");
        }
        while (true)
        {
            // last-seen state for reduced logging
            ushort lastButtons = 0;
            byte lastLt = 0, lastRt = 0;
            short lastLx = 0, lastLy = 0, lastRx = 0, lastRy = 0;
            
            var data = udp.Receive(ref remoteEp);
            // remember last client endpoint to send vibration callbacks
            lastClientEp = remoteEp;
            if (data.Length < 1) continue;
            if (data.Length < 17) continue;
            int payloadLen = data.Length - 16;
            using var hmac = new HMACSHA256(secretKey);
            var expectedFull = hmac.ComputeHash(data, 0, payloadLen);
            bool ok = true;
            for (int i=0;i<16;i++) if (expectedFull[i] != data[payloadLen + i]) { ok = false; break; }
            if (!ok) { Console.WriteLine("Received packet with invalid HMAC from " + remoteEp); continue; }

            if (data[0] == 1 && payloadLen >= 17)
            {
                int idx = 1;
                uint packet = BitConverter.ToUInt32(data, idx); idx += 4;
                ushort buttons = BitConverter.ToUInt16(data, idx); idx += 2;
                byte lt = data[idx++]; byte rt = data[idx++];
                short lx = BitConverter.ToInt16(data, idx); idx += 2;
                short ly = BitConverter.ToInt16(data, idx); idx += 2;
                short rx = BitConverter.ToInt16(data, idx); idx += 2;
                short ry = BitConverter.ToInt16(data, idx); idx += 2;

                // Debug: print incoming packet state for troubleshooting
                Console.WriteLine($"Recv from {remoteEp} - packet={BitConverter.ToUInt32(data,1)} buttons=0x{BitConverter.ToUInt16(data,5):X4} lt={data[7]} rt={data[8]} lx={BitConverter.ToInt16(data,9)} ly={BitConverter.ToInt16(data,11)} rx={BitConverter.ToInt16(data,13)} ry={BitConverter.ToInt16(data,15)} vigemReady={vigemReady}");

                // If ViGEm reflection init succeeded, use cached reflection objects
                if (vigemReady && x360 != null && rifSetAxis != null && rifSetSlider != null && rifSetButton != null && rifLeftX != null)
                {
                    try
                    {
                            // call ViGEm API via cached reflection objects
                            rifSetAxis.Invoke(x360, new object[] { rifLeftX, lx });
                            rifSetAxis.Invoke(x360, new object[] { rifLeftY, ly });
                            rifSetAxis.Invoke(x360, new object[] { rifRightX, rx });
                            rifSetAxis.Invoke(x360, new object[] { rifRightY, ry });

                            rifSetSlider.Invoke(x360, new object[] { rifLtSlider, lt });
                            rifSetSlider.Invoke(x360, new object[] { rifRtSlider, rt });

                        // Buttons mapping (XInput bitmask)
                        const ushort DPAD_UP = 0x0001; const ushort DPAD_DOWN = 0x0002; const ushort DPAD_LEFT = 0x0004; const ushort DPAD_RIGHT = 0x0008;
                        const ushort START = 0x0010; const ushort BACK = 0x0020; const ushort LEFT_THUMB = 0x0040; const ushort RIGHT_THUMB = 0x0080;
                        const ushort LEFT_SHOULDER = 0x0100; const ushort RIGHT_SHOULDER = 0x0200; const ushort A_BTN = 0x1000; const ushort B_BTN = 0x2000;
                        const ushort X_BTN = 0x4000; const ushort Y_BTN = 0x8000;

                        rifSetButton.Invoke(x360, new object[] { rifABtn, (buttons & A_BTN) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifBBtn, (buttons & B_BTN) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifXBtn, (buttons & X_BTN) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifYBtn, (buttons & Y_BTN) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifLeftShoulderBtn, (buttons & LEFT_SHOULDER) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifRightShoulderBtn, (buttons & RIGHT_SHOULDER) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifLeftThumbBtn, (buttons & LEFT_THUMB) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifRightThumbBtn, (buttons & RIGHT_THUMB) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifStartBtn, (buttons & START) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifBackBtn, (buttons & BACK) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifUpBtn, (buttons & DPAD_UP) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifDownBtn, (buttons & DPAD_DOWN) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifLeftBtn, (buttons & DPAD_LEFT) != 0 });
                        rifSetButton.Invoke(x360, new object[] { rifRightBtn, (buttons & DPAD_RIGHT) != 0 });

                        // Reduced logging: print when state changes
                        if (buttons != lastButtons || lt != lastLt || rt != lastRt || lx != lastLx || ly != lastLy || rx != lastRx || ry != lastRy)
                        {
                            Console.WriteLine($"Applied to ViGEm: buttons=0x{buttons:X4} lt={lt} rt={rt} lx={lx} ly={ly} rx={rx} ry={ry}");
                            lastButtons = buttons; lastLt = lt; lastRt = rt; lastLx = lx; lastLy = ly; lastRx = rx; lastRy = ry;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ViGEm apply state failed: " + ex.ToString());
                        vigemReady = false;
                        x360 = null;
                    }
                }
                else
                {
                    // Fallback: map left thumb to relative mouse movement
                    int moveX = (int)(lx / 1000);
                    int moveY = (int)(-ly / 1000);
                    if (moveX != 0 || moveY != 0)
                    {
                        var input = new INPUT[]{ new INPUT{ type = 0, U = new InputUnion{ mi = new MOUSEINPUT{ dx = moveX, dy = moveY, dwFlags = 0x0001 } } } };
                        SendInput((uint)input.Length, input, Marshal.SizeOf(typeof(INPUT)));
                    }
                }

                // NOTE: removed test vibration echo on A-button to avoid unwanted client vibration.
            }
        }
    }
}
