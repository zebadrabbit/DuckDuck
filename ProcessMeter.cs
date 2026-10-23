using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace DuckDuck;

/// Peak meter fed by Windows process loopback (Windows 10 2004+): only what the process tree
/// actually renders, on any device. A session's IAudioMeterInformation also counts the
/// session's loopback *capture* streams, so Discord's echo-cancel tap on the output device
/// makes its session meter mirror the whole mix (anything Brave plays reads as Discord).
/// Use from the thread that created it; that thread must be MTA or the activation callback
/// deadlocks against the blocked Wait below.
public sealed class ProcessMeter : IDisposable
{
    readonly AudioClient client;
    readonly AudioCaptureClient capture;
    short[] buf = new short[4096];

    /// null when activation fails (older Windows, process already gone): caller falls back to session meters.
    public static ProcessMeter? TryCreate(uint pid)
    {
        try { return new ProcessMeter(pid); } catch { return null; }
    }

    ProcessMeter(uint pid)
    {
        var handler = new Handler();
        var riid = typeof(IAudioClient).GUID;
        // PROPVARIANT{VT_BLOB} -> AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE_TARGET_PROCESS_TREE } }
        IntPtr blob = Marshal.AllocHGlobal(12), pv = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(blob, 0, 1); Marshal.WriteInt32(blob, 4, (int)pid); Marshal.WriteInt32(blob, 8, 0);
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 0x41);     // vt = VT_BLOB
            Marshal.WriteInt32(pv, 8, 12);       // blob.cbSize
            Marshal.WriteIntPtr(pv, 16, blob);   // blob.pBlobData
            ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", ref riid, pv, handler, out var op);
            if (!handler.Done.Wait(2000)) throw new TimeoutException();
            op.GetActivateResult(out int hr, out object? unk);
            Marshal.ThrowExceptionForHR(hr);
            client = new AudioClient((IAudioClient)unk!);
        }
        finally { Marshal.FreeHGlobal(pv); Marshal.FreeHGlobal(blob); }
        try
        {
            // The virtual device has no mix format; ask for 16-bit 48 kHz stereo and the engine converts.
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 2_000_000, 0, new WaveFormat(48000, 16, 2), Guid.Empty);
            capture = client.AudioCaptureClient;
            client.Start();
        }
        catch { client.Dispose(); throw; }
    }

    /// Peak 0..1 of everything rendered since the last call. Call every tick: the buffer holds
    /// 200 ms and older audio is dropped. Reads 0 once the process has exited.
    public float Peak()
    {
        int max = 0;
        try
        {
            while (capture.GetNextPacketSize() > 0)
            {
                var p = capture.GetBuffer(out int frames, out var flags);
                int n = frames * 2;
                if ((flags & AudioClientBufferFlags.Silent) == 0 && n > 0)
                {
                    if (buf.Length < n) buf = new short[n];
                    Marshal.Copy(p, buf, 0, n);
                    for (int i = 0; i < n; i++) max = Math.Max(max, Math.Abs((int)buf[i]));
                }
                capture.ReleaseBuffer(frames);
            }
        }
        catch { /* ponytail: a dead stream reads 0 until its PID's sessions expire and the rescan drops it */ }
        return max / 32768f;
    }

    public void Dispose()
    {
        try { client.Stop(); } catch { }
        capture.Dispose();
        client.Dispose();
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object? unk); }

    [ComVisible(true)]
    sealed class Handler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }
}
