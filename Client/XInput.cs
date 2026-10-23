using System.Runtime.InteropServices;

static class XInput
{
    public const int MaxControllers = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
        public uint dwPaddingReserved; // written by XInputGetStateEx
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed; public ushort wRightMotorSpeed; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    // Undocumented ordinal 100 is XInputGetStateEx, which also reports the Guide button.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    static extern int XInputGetStateEx(int dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    static extern int XInputSetState(int dwUserIndex, ref XINPUT_VIBRATION pVibration);

    static bool hasGetStateEx = true;

    public static bool TryGetState(int index, out GamepadState state)
    {
        XINPUT_STATE raw;
        int result;
        if (hasGetStateEx)
        {
            try { result = XInputGetStateEx(index, out raw); }
            catch (EntryPointNotFoundException) { hasGetStateEx = false; result = XInputGetState(index, out raw); }
        }
        else
        {
            result = XInputGetState(index, out raw);
        }

        state = result == 0
            ? new GamepadState(raw.wButtons, raw.bLeftTrigger, raw.bRightTrigger, raw.sThumbLX, raw.sThumbLY, raw.sThumbRX, raw.sThumbRY)
            : default;
        return result == 0;
    }

    // Returns the Win32 error code (0 on success).
    public static int SetRumble(int index, byte large, byte small)
    {
        var vibration = new XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(large * 257), wRightMotorSpeed = (ushort)(small * 257) };
        return XInputSetState(index, ref vibration);
    }
}
