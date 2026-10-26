using System.Runtime.InteropServices;

namespace SpotifyPad;

[Flags]
public enum PadButton : uint
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftStick = 0x0040,
    RightStick = 0x0080,
    LB = 0x0100,
    RB = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    // Triggers are analog; treated as buttons above a threshold
    LT = 0x10000,
    RT = 0x20000,
}

public enum PadType { Xbox, PlayStation }

public static class PadButtons
{
    // A property so the labels follow the current language; the order and buttons never change
    public static (PadButton Button, string Xbox, string XboxLong, string Ps, string PsLong)[] All =>
    [
        (PadButton.LB, "LB", "LB", "L1", "L1"),
        (PadButton.RB, "RB", "RB", "R1", "R1"),
        (PadButton.LT, "LT", "LT", "L2", "L2"),
        (PadButton.RT, "RT", "RT", "R2", "R2"),
        (PadButton.A, "A", "A", "✕", T("✕  Kreuz", "✕  Cross")),
        (PadButton.B, "B", "B", "○", T("○  Kreis", "○  Circle")),
        (PadButton.X, "X", "X", "□", T("□  Quadrat", "□  Square")),
        (PadButton.Y, "Y", "Y", "△", T("△  Dreieck", "△  Triangle")),
        (PadButton.Back, "View", "View / Back", "Share", "Share / Create"),
        (PadButton.Start, T("Menü", "Menu"), T("Menü / Start", "Menu / Start"), "Options", "Options"),
        (PadButton.LeftStick, "L3", T("Linker Stick drücken (L3)", "Left stick click (L3)"), "L3", T("Linker Stick drücken (L3)", "Left stick click (L3)")),
        (PadButton.RightStick, "R3", T("Rechter Stick drücken (R3)", "Right stick click (R3)"), "R3", T("Rechter Stick drücken (R3)", "Right stick click (R3)")),
        (PadButton.DPadUp, "↑", T("Steuerkreuz ↑", "D-pad ↑"), "↑", T("Steuerkreuz ↑", "D-pad ↑")),
        (PadButton.DPadDown, "↓", T("Steuerkreuz ↓", "D-pad ↓"), "↓", T("Steuerkreuz ↓", "D-pad ↓")),
        (PadButton.DPadLeft, "←", T("Steuerkreuz ←", "D-pad ←"), "←", T("Steuerkreuz ←", "D-pad ←")),
        (PadButton.DPadRight, "→", T("Steuerkreuz →", "D-pad →"), "→", T("Steuerkreuz →", "D-pad →")),
    ];

    public static string Format(uint mask, PadType type) =>
        string.Join(" + ", All.Where(b => (mask & (uint)b.Button) != 0).Select(b => type == PadType.Xbox ? b.Xbox : b.Ps));

    public static string Long(int index, PadType type) => type == PadType.Xbox ? All[index].XboxLong : All[index].PsLong;

    public static string TypeName(PadType type) => type == PadType.Xbox ? "Xbox" : "PlayStation";
}

static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LX, LY, RX, RY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint Packet;
        public Gamepad Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vibration
    {
        public ushort Left;
        public ushort Right;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static extern int GetState(int index, out State state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    public static extern int SetState(int index, ref Vibration vibration);
}

/// <summary>
/// Reads Sony controllers (DualShock 4 / DualSense) through the legacy winmm joystick API,
/// since XInput only sees Xbox controllers. Buttons are mapped onto the Xbox layout.
/// </summary>
sealed class PlayStationPads
{
    const int MaxIds = 16;
    const ushort SonyVendor = 0x054C;
    const int JOY_RETURNBUTTONS = 0x80, JOY_RETURNPOV = 0x40;

    // DirectInput button order of Sony pads: □ ✕ ○ △ L1 R1 L2 R2 Share Options L3 R3
    static readonly PadButton[] ButtonMap =
    [
        PadButton.X, PadButton.A, PadButton.B, PadButton.Y,
        PadButton.LB, PadButton.RB, PadButton.LT, PadButton.RT,
        PadButton.Back, PadButton.Start, PadButton.LeftStick, PadButton.RightStick,
    ];

    readonly bool[] sony = new bool[MaxIds];

    public int Count { get; private set; }
    public string Model { get; private set; } = "";

    /// <param name="scan">Also probe ids that are not known Sony pads (slow path, done every few seconds).</param>
    public uint Poll(bool scan)
    {
        // winmm only notices hot-plugged devices after a config refresh
        if (scan && Count == 0) joyConfigChanged(0);

        uint mask = 0;
        int count = 0;
        for (int id = 0; id < MaxIds; id++)
        {
            if (!sony[id] && !scan) continue;
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS | JOY_RETURNPOV };
            if (joyGetPosEx(id, ref info) != 0)
            {
                sony[id] = false;
                continue;
            }
            if (!sony[id])
            {
                // Xbox pads also show up here (Microsoft vendor id); only take Sony ones
                var caps = new JOYCAPSW();
                if (joyGetDevCapsW(id, ref caps, Marshal.SizeOf<JOYCAPSW>()) != 0 || caps.wMid != SonyVendor) continue;
                sony[id] = true;
                Model = caps.wPid switch
                {
                    0x05C4 or 0x09CC => "DualShock 4",
                    0x0CE6 => "DualSense",
                    0x0DF2 => "DualSense Edge",
                    _ => "",
                };
            }
            count++;
            mask |= Map(info);
        }
        Count = count;
        return mask;
    }

    static uint Map(in JOYINFOEX info)
    {
        uint m = 0;
        for (int i = 0; i < ButtonMap.Length; i++)
            if ((info.dwButtons & (1 << i)) != 0) m |= (uint)ButtonMap[i];

        // POV hat in hundredths of a degree, 0 = up, clockwise; 0xFFFF = centered
        int pov = info.dwPOV;
        if (pov is >= 0 and < 36000)
        {
            if (pov > 27000 || pov < 9000) m |= (uint)PadButton.DPadUp;
            if (pov > 0 && pov < 18000) m |= (uint)PadButton.DPadRight;
            if (pov > 9000 && pov < 27000) m |= (uint)PadButton.DPadDown;
            if (pov > 18000) m |= (uint)PadButton.DPadLeft;
        }
        return m;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOYINFOEX
    {
        public int dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public int dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public int wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax;
        public int wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref JOYINFOEX info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int joyGetDevCapsW(nint id, ref JOYCAPSW caps, int size);
    [DllImport("winmm.dll")] static extern int joyConfigChanged(int flags);
}
