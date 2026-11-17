using System.Runtime.InteropServices;

namespace PulseOfTime;

/// Captures only one process's audio (Windows 10 2004+ process loopback), as 48 kHz stereo float.
/// NAudio 2.2.1 has the activation structs but no capture class for it, so this is the raw COM path.
sealed class ProcessLoopback : IDisposable
{
    public const int Rate = 48000;
    readonly IAudioClient _client;
    readonly IAudioCaptureClient _capture;
    readonly AutoResetEvent _ready = new(false);
    readonly Thread _thread;
    volatile bool _stop;
    public event Action<float[], int>? Data; // interleaved stereo, frame count

    ProcessLoopback(IAudioClient client)
    {
        _client = client;
        var fmt = new WaveFormatEx { wFormatTag = 3 /*IEEE float*/, nChannels = 2, nSamplesPerSec = Rate, wBitsPerSample = 32, nBlockAlign = 8, nAvgBytesPerSec = Rate * 8 };
        const uint flags = 0x00020000 /*LOOPBACK*/ | 0x00040000 /*EVENTCALLBACK*/ | 0x80000000 /*AUTOCONVERTPCM*/ | 0x08000000 /*SRC_DEFAULT_QUALITY*/;
        Check(_client.Initialize(0 /*shared*/, flags, 100_000 /*10 ms*/, 0, ref fmt, IntPtr.Zero));
        Check(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()));
        var iid = typeof(IAudioCaptureClient).GUID;
        Check(_client.GetService(ref iid, out var cap));
        _capture = (IAudioCaptureClient)cap;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "loopback" };
    }

    public static ProcessLoopback Open(int pid)
    {
        var p = new ActivationParams { ActivationType = 1 /*PROCESS_LOOPBACK*/, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0 /*include tree*/ };
        IntPtr blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        IntPtr pv = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(p, blob, false);
            // PROPVARIANT { vt = VT_BLOB, blob.cbSize at +8, blob.pBlobData at +16 }
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65);
            Marshal.WriteInt32(pv, 8, Marshal.SizeOf<ActivationParams>());
            Marshal.WriteIntPtr(pv, 16, blob);
            var handler = new Completion();
            var iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out _));
            if (!handler.Done.Wait(5000)) throw new TimeoutException("process loopback activation timed out");
            handler.Op!.GetActivateResult(out int hr, out object iface);
            Check(hr);
            return new ProcessLoopback((IAudioClient)iface);
        }
        finally { Marshal.FreeHGlobal(blob); Marshal.FreeHGlobal(pv); }
    }

    public void Start() { Check(_client.Start()); _thread.Start(); }

    void Loop()
    {
        var buf = new float[48000];
        while (!_stop)
        {
            _ready.WaitOne(50);
            while (_capture.GetNextPacketSize(out uint n) == 0 && n > 0)
            {
                if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint fl, out _, out _) != 0) break;
                int count = (int)Math.Min(frames, (uint)(buf.Length / 2));
                if ((fl & 0x2) != 0 /*SILENT*/) Array.Clear(buf, 0, count * 2);
                else Marshal.Copy(data, buf, 0, count * 2);
                _capture.ReleaseBuffer(frames);
                Data?.Invoke(buf, count);
            }
        }
    }

    public void Dispose() { _stop = true; _thread.Join(500); _client.Stop(); Marshal.ReleaseComObject(_capture); Marshal.ReleaseComObject(_client); }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx { public ushort wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync(string path, ref Guid riid, IntPtr activationParams,
        IActivateCompletion handler, out IActivateOperation op);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateCompletion { void ActivateCompleted(IActivateOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    sealed class Completion : IActivateCompletion, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public IActivateOperation? Op;
        public void ActivateCompleted(IActivateOperation op) { Op = op; Done.Set(); }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
