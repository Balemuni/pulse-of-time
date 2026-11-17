using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseOfTime;

/// Writes only the adaptive-trigger part of the DualSense output report. The enable bits
/// (0x04 right, 0x08 left) make the controller ignore every other field, so Lichii's own
/// rumble/haptics/lightbar writes and ours do not overwrite each other.
sealed class DualSense : IDisposable
{
    const ushort Vid = 0x054C;
    static readonly ushort[] Pids = { 0x0CE6, 0x0DF2 }; // DualSense, DualSense Edge

    readonly SafeFileHandle _h;
    readonly bool _bt;
    readonly int _len;
    byte _btSeq;
    public string Path { get; }
    public int InLen { get; init; }
    public bool Bluetooth => _bt;

    DualSense(SafeFileHandle h, string path, int outLen)
    {
        _h = h; Path = path; _len = outLen; _bt = outLen > 48;
    }

    public static DualSense? Open()
    {
        foreach (var path in Hid.EnumeratePaths())
        {
            var h = Hid.CreateFile(path, 0xC0000000 /*R|W*/, 3 /*share R|W*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var attr = new Hid.Attributes { Size = Marshal.SizeOf<Hid.Attributes>() };
            if (Hid.HidD_GetAttributes(h, ref attr) && attr.VendorID == Vid && Pids.Contains(attr.ProductID)
                && Hid.HidD_GetPreparsedData(h, out var pp))
            {
                Hid.HidP_GetCaps(pp, out var caps);
                Hid.HidD_FreePreparsedData(pp);
                if (caps.OutputReportByteLength >= 48) return new DualSense(h, path, caps.OutputReportByteLength) { InLen = caps.InputReportByteLength };
            }
            h.Dispose();
        }
        return null;
    }

    /// Lightbar colour, the five player LEDs (bits 0-4, left to right) and the mute-button LED (0 off, 1 on, 2 pulse).
    public readonly record struct Lights(byte R, byte G, byte B, byte PlayerLeds, byte Mic);

    public bool SetTriggers(byte[]? right, byte[]? left) => SetOutput(right, left, null);

    /// right/left: 11-byte effect blocks (see Effects), null = leave that trigger untouched; lights null = leave them.
    /// Only the parts whose enable bits are set change on the controller (offsets as SDL's DS5EffectsState_t).
    public bool SetOutput(byte[]? right, byte[]? left, Lights? lights)
    {
        var r = new byte[_len];
        int o; // offset of the common effects block
        if (_bt) { r[0] = 0x31; r[1] = (byte)((_btSeq++ & 0x0F) << 4 | 0x02); o = 2; }
        else { r[0] = 0x02; o = 1; }
        if (right != null) { r[o + 0] |= 0x04; right.CopyTo(r, o + 10); }
        if (left != null) { r[o + 0] |= 0x08; left.CopyTo(r, o + 21); }
        if (lights is { } li)
        {
            r[o + 1] |= 0x01 | 0x04 | 0x10; // mic LED, lightbar, player LEDs
            r[o + 8] = li.Mic;
            r[o + 42] = 0;                   // player LED brightness: high
            r[o + 43] = (byte)(li.PlayerLeds & 0x1F);
            r[o + 44] = li.R; r[o + 45] = li.G; r[o + 46] = li.B;
        }
        if (_bt)
        {
            // BT output report carries a CRC32 over 0xA2 + report[0..73] in its last 4 bytes.
            var crcIn = new byte[75]; crcIn[0] = 0xA2; Array.Copy(r, 0, crcIn, 1, 74);
            BitConverter.GetBytes(Crc32(crcIn)).CopyTo(r, 74);
        }
        return Hid.WriteFile(_h, r, r.Length, out _, IntPtr.Zero);
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) { c ^= b; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1)); }
        return ~c;
    }

    // ---- input: analog trigger positions, read on a second handle so writes never wait on reads ----
    public volatile int L2, R2; // 0..255
    /// Face/shoulder buttons: bit 0 Square, 1 Cross, 2 Circle, 3 Triangle, 4 L1, 5 R1, 6 L3, 7 R3.
    public volatile int Buttons;
    public volatile string InputError = "";
    public volatile int LX = 128, LY = 128, Dpad = 8;
    public volatile bool Options, TouchClick, TouchDown;
    public volatile int TouchY; // 0 (top) .. ~1080 (bottom)
    public volatile int TouchX; // 0 (left) .. ~1920 (right)
    public volatile float GyroX, GyroY, GyroZ; // °/s
    Thread? _reader; volatile bool _stopRead;

    public void StartInput()
    {
        var h = Hid.CreateFile(Path, 0xC0000000 /*R|W*/, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) { InputError = $"open failed ({Marshal.GetLastWin32Error()})"; return; }
        int len = InLen > 0 ? InLen : 64;
        _reader = new Thread(() =>
        {
            var buf = new byte[len];
            using (h)
                while (!_stopRead)
                {
                    if (!Hid.ReadFile(h, buf, buf.Length, out int n, IntPtr.Zero)) { InputError = $"read failed ({Marshal.GetLastWin32Error()})"; break; }
                    if (n < 11) continue;
                    // USB report 0x01 / BT 0x31 (one byte later): sticks 1-4, L2 5, R2 6, counter 7, buttons 8-10
                    int o = buf[0] == 0x31 ? 1 : 0;
                    if (buf[0] is not (0x01 or 0x31)) continue;
                    L2 = buf[5 + o]; R2 = buf[6 + o];
                    LX = buf[1 + o]; LY = buf[2 + o];
                    int b8 = buf[8 + o], b9 = buf[9 + o];
                    Dpad = b8 & 0x0F; // 0 up, clockwise to 7 up-left, 8 none
                    Options = (b9 & 0x20) != 0;
                    TouchClick = (buf[10 + o] & 0x02) != 0;
                    if (n > 36 + o)
                    {   // first touch point: byte 33 bit 7 = not touching, then 12-bit x / y
                        TouchDown = (buf[33 + o] & 0x80) == 0;
                        TouchX = buf[34 + o] | ((buf[35 + o] & 0x0F) << 8);
                        TouchY = (buf[35 + o] >> 4) | (buf[36 + o] << 4);
                    }
                    if (n > 21 + o)
                    {   // gyro pitch / yaw / roll, signed 16-bit, ±2000 °/s full scale (16.4 per °/s)
                        GyroX = (short)(buf[16 + o] | buf[17 + o] << 8) / 16.4f;
                        GyroY = (short)(buf[18 + o] | buf[19 + o] << 8) / 16.4f;
                        GyroZ = (short)(buf[20 + o] | buf[21 + o] << 8) / 16.4f;
                    }
                    Buttons = (b8 >> 4) & 0x0F                              // square, cross, circle, triangle
                            | (b9 & 0x01) << 4 | (b9 & 0x02) << 4           // L1, R1
                            | (b9 & 0x40) | (b9 & 0x80);                    // L3, R3
                }
        }) { IsBackground = true, Name = "ds-input" };
        _reader.Start();
    }

    public void Dispose() { _stopRead = true; _h.Dispose(); }
}

/// DualSense trigger effect blocks (firmware "multi-position" modes, 10 zones 0..9).
static class Effects
{
    public static byte[] Off() => B(0x05);

    /// Constant resistance from zone `start` to the end. strength 1..8.
    public static byte[] Feedback(int start, int strength)
    {
        if (strength <= 0) return Off();
        uint zones = 0, force = 0;
        for (int i = Math.Clamp(start, 0, 9); i < 10; i++) { zones |= 1u << i; force |= (uint)((strength - 1) & 7) << (3 * i); }
        return B(0x21, (byte)zones, (byte)(zones >> 8), (byte)force, (byte)(force >> 8), (byte)(force >> 16), (byte)(force >> 24));
    }

    /// Resistance ramp per zone (0..8 each, 10 values): bow-string style build-up.
    public static byte[] Slope(int start, int end, int from, int to)
    {
        start = Math.Clamp(start, 0, 9); end = Math.Clamp(end, start, 9);
        uint zones = 0, force = 0;
        for (int i = start; i <= end; i++)
        {
            int s = end == start ? to : from + (to - from) * (i - start) / (end - start);
            if (s <= 0) continue;
            zones |= 1u << i; force |= (uint)((s - 1) & 7) << (3 * i);
        }
        return zones == 0 ? Off() : B(0x21, (byte)zones, (byte)(zones >> 8), (byte)force, (byte)(force >> 8), (byte)(force >> 16), (byte)(force >> 24));
    }

    /// Resistance between start (2..7) and end (start+1..8) that breaks like a trigger. strength 1..8.
    public static byte[] Weapon(int start, int end, int strength)
    {
        if (strength <= 0) return Off();
        start = Math.Clamp(start, 2, 7); end = Math.Clamp(end, start + 1, 8);
        uint mask = (1u << start) | (1u << end);
        return B(0x25, (byte)mask, (byte)(mask >> 8), (byte)(Math.Clamp(strength, 1, 8) - 1));
    }

    /// Trigger buzz from zone `start` on. amplitude 1..8, frequency in Hz (1..255).
    public static byte[] Vibration(int start, int amplitude, int frequency)
    {
        if (amplitude <= 0 || frequency <= 0) return Off();
        uint zones = 0, amp = 0;
        for (int i = Math.Clamp(start, 0, 9); i < 10; i++) { zones |= 1u << i; amp |= (uint)((amplitude - 1) & 7) << (3 * i); }
        return B(0x26, (byte)zones, (byte)(zones >> 8), (byte)amp, (byte)(amp >> 8), (byte)(amp >> 16), (byte)(amp >> 24), 0, 0, (byte)Math.Clamp(frequency, 1, 255));
    }

    /// Real bow: resistance from start to end (0..8), then it gives way with snapForce. strength/snap 1..8.
    public static byte[] Bow(int start, int end, int strength, int snap)
    {
        start = Math.Clamp(start, 0, 8); end = Math.Clamp(end, start + 1, 8);
        uint zones = (1u << start) | (1u << end);
        uint force = (uint)((Math.Clamp(strength, 1, 8) - 1) & 7) | (uint)(((Math.Clamp(snap, 1, 8) - 1) & 7) << 3);
        return B(0x22, (byte)zones, (byte)(zones >> 8), (byte)force, (byte)(force >> 8));
    }

    /// Two hoof beats per cycle between start and end (firstFoot 0..6 < secondFoot 1..7), frequency Hz.
    public static byte[] Galloping(int start, int end, int firstFoot, int secondFoot, int frequency)
    {
        start = Math.Clamp(start, 0, 8); end = Math.Clamp(end, start + 1, 9);
        firstFoot = Math.Clamp(firstFoot, 0, 6); secondFoot = Math.Clamp(secondFoot, firstFoot + 1, 7);
        uint zones = (1u << start) | (1u << end);
        uint ratio = (uint)(secondFoot & 7) | (uint)((firstFoot & 7) << 3);
        return B(0x23, (byte)zones, (byte)(zones >> 8), (byte)ratio, (byte)Math.Clamp(frequency, 1, 255));
    }

    /// Alternates two amplitudes (0..7) at frequency Hz, switching every `period` (tenths of a second).
    public static byte[] Machine(int start, int end, int ampA, int ampB, int frequency, int period)
    {
        start = Math.Clamp(start, 0, 8); end = Math.Clamp(end, start + 1, 9);
        uint zones = (1u << start) | (1u << end);
        uint amps = (uint)(Math.Clamp(ampA, 0, 7) & 7) | (uint)((Math.Clamp(ampB, 0, 7) & 7) << 3);
        return B(0x27, (byte)zones, (byte)(zones >> 8), (byte)amps, (byte)Math.Clamp(frequency, 1, 255), (byte)Math.Clamp(period, 0, 255));
    }

    static byte[] B(params byte[] p) { var b = new byte[11]; p.CopyTo(b, 0); return b; }
}

static partial class Hid
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Attributes { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Caps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DevIfData { public int cbSize; public Guid Class; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes a);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out Caps c);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ov);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid g, int idx, ref DevIfData d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DevIfData d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    public static IEnumerable<string> EnumeratePaths()
    {
        HidD_GetHidGuid(out var g);
        var set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12 /*PRESENT|DEVICEINTERFACE*/);
        var list = new List<string>();
        try
        {
            var d = new DevIfData { cbSize = Marshal.SizeOf<DevIfData>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, 8); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out _, IntPtr.Zero))
                        list.Add(Marshal.PtrToStringUni(buf + 4)!);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }
}
