using System.Runtime.InteropServices;
using NAudio.Wave;

namespace CurrentLyrics.Services;

/// <summary>
/// 只抓本进程声音的 WASAPI 回环（Windows 10 2004+ 的 process loopback）。
///
/// 设备回环抓的是整块声卡的混音 —— 别的播放器、网页、提示音都会让频谱跳；
/// 这里用 <c>ActivateAudioInterfaceAsync</c> + <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>
/// 拿到一个只包含目标进程树的音频客户端，再按普通 loopback 读包。
///
/// 为什么直接走 vtable 而不是 ComImport 接口：这个虚拟设备的 IAudioClient 用 RCW 包装后
/// 调 <c>GetBufferSize</c> 会读到垃圾值、<c>GetMixFormat</c> 返回 E_NOTIMPL，说明包装层没对上；
/// 拿函数指针自己调最稳。
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint BufferFlagsSilent = 0x00000002;
    private const int ActivateProcessLoopback = 1;
    private const string VirtualDeviceProcessLoopback = @"VAD\Process_Loopback";

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private IntPtr _client;
    private IntPtr _capture;
    private IntPtr _event;
    private Thread? _thread;
    private volatile bool _running;
    private int _blockAlign = 8;

    public event Action<byte[], int>? Data;
    public WaveFormat? Format { get; private set; }
    public string Error { get; private set; } = "";
    public bool Running => _running;
    /// <summary>0 = 只抓目标进程树（默认），1 = 抓除目标以外的一切（诊断用）。</summary>
    public int Mode { get; set; }
    public uint BufferFrames { get; private set; }

    public bool Start(int processId)
    {
        Stop();
        Error = "";
        try
        {
            if (!Open(processId)) return false;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "process-loopback" };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name + ":" + ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        _running = false;
        var thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(600); } catch { }
        }
        if (_client != IntPtr.Zero)
        {
            try { VoidAt(_client, 8)(_client); } catch { }   // Stop
        }
        if (_event != IntPtr.Zero)
        {
            try { CloseHandle(_event); } catch { }
            _event = IntPtr.Zero;
        }
        if (_capture != IntPtr.Zero)
        {
            try { Marshal.Release(_capture); } catch { }
            _capture = IntPtr.Zero;
        }
        if (_client != IntPtr.Zero)
        {
            try { Marshal.Release(_client); } catch { }
            _client = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private bool Open(int processId)
    {
        IntPtr activation = Marshal.AllocHGlobal(12);
        IntPtr prop = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(activation, 0, ActivateProcessLoopback);
            Marshal.WriteInt32(activation, 4, processId);
            Marshal.WriteInt32(activation, 8, Mode);

            for (int i = 0; i < 24; i++) Marshal.WriteByte(prop, i, 0);
            Marshal.WriteInt16(prop, 0, 65);            // VT_BLOB
            Marshal.WriteInt32(prop, 8, 12);            // blob.cbSize
            Marshal.WriteIntPtr(prop, 16, activation);  // blob.pBlobData

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, IidAudioClient, prop, handler,
                out var operation);
            if (!handler.Done.Wait(5000))
            {
                Error = "activation-timeout";
                return false;
            }
            if (handler.Result != 0 || handler.InterfacePtr == IntPtr.Zero)
            {
                Error = $"activate-hr=0x{handler.Result:X8}";
                return false;
            }
            IntPtr client;
            try
            {
                int qi = Marshal.QueryInterface(handler.InterfacePtr, ref IidAudioClientUnsafe(), out client);
                if (qi != 0) client = IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(handler.InterfacePtr);
            }
            if (client == IntPtr.Zero)
            {
                Error = "qi-failed";
                return false;
            }
            _client = client;

            // 先问引擎要混音格式；这个虚拟设备经常直接 E_NOTIMPL，那就用候选格式硬上。
            int hr = -1;
            IntPtr formatPtr = IntPtr.Zero;
            GetPtrAt(client, 5)(client, out formatPtr);   // GetMixFormat
            WaveFormat? format = null;
            if (formatPtr != IntPtr.Zero)
            {
                format = ReadFormat(formatPtr);
                hr = InitAt(client, 0)(client, 0, StreamFlagsLoopback | StreamFlagsEventCallback, 2000000, 0, formatPtr, IntPtr.Zero);
                Marshal.FreeCoTaskMem(formatPtr);
            }
            if (hr != 0)
            {
                foreach (var candidate in Candidates())
                {
                    format = candidate;
                    IntPtr buffer = Marshal.AllocHGlobal(18);
                    try
                    {
                        WriteFormat(buffer, candidate);
                        hr = InitAt(client, 0)(client, 0, StreamFlagsLoopback | StreamFlagsEventCallback, 2000000, 0, buffer, IntPtr.Zero);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                    if (hr == 0) break;
                }
            }
            if (hr != 0 || format == null)
            {
                Error = $"init-hr=0x{hr:X8}";
                return false;
            }
            Format = format;
            _blockAlign = Math.Max(1, format.BlockAlign);

            int sizeHr = GetU32At(client, 1)(client, out var frames);
            BufferFrames = sizeHr == 0 ? frames : 0;

            _event = CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
            if (_event == IntPtr.Zero)
            {
                Error = "no-event";
                return false;
            }
            int setHr = SetEventAt(client, 10)(client, _event);
            if (setHr != 0)
            {
                Error = $"set-event-hr=0x{setHr:X8}";
                return false;
            }

            int serviceHr = GetServiceAt(client, 11)(client, ref IidCaptureClientUnsafe(), out var capture);
            if (serviceHr != 0 || capture == IntPtr.Zero)
            {
                Error = $"service-hr=0x{serviceHr:X8}";
                return false;
            }
            _capture = capture;

            if (VoidAt(client, 7)(client) != 0)   // Start
            {
                Error = "start-failed";
                return false;
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(activation);
            Marshal.FreeHGlobal(prop);
        }
    }

    private void Loop()
    {
        IntPtr capture = _capture;
        if (capture == IntPtr.Zero) return;
        var packet = PacketAt(capture, 2);
        var getBuffer = GetBufferAt(capture, 0);
        var release = ReleaseBufferAt(capture, 1);
        var buffer = Array.Empty<byte>();
        while (_running)
        {
            try
            {
                int hr = packet(capture, out var frames);
                if (hr != 0)
                {
                    Error = $"packet-hr=0x{hr:X8}";
                    Thread.Sleep(50);
                    continue;
                }
                if (frames == 0)
                {
                    if (_event != IntPtr.Zero) WaitForSingleObject(_event, 100);
                    else Thread.Sleep(8);
                    continue;
                }
                Error = "";
                while (frames > 0 && _running)
                {
                    hr = getBuffer(capture, out var data, out var count, out var flags, out _, out _);
                    if (hr != 0)
                    {
                        Error = $"getbuffer-hr=0x{hr:X8}";
                        break;
                    }
                    int bytes = (int)count * _blockAlign;
                    if ((flags & BufferFlagsSilent) == 0 && bytes > 0)
                    {
                        if (buffer.Length < bytes) buffer = new byte[bytes];
                        Marshal.Copy(data, buffer, 0, bytes);
                        Data?.Invoke(buffer, bytes);
                    }
                    release(capture, count);
                    if (packet(capture, out frames) != 0) break;
                }
            }
            catch
            {
                Thread.Sleep(50);
            }
            Thread.Sleep(4);
        }
    }

    // ---------- 格式 ----------

    private WaveFormat? ReadFormat(IntPtr ptr)
    {
        int tag = Marshal.ReadInt16(ptr, 0);
        int channels = Marshal.ReadInt16(ptr, 2);
        int rate = Marshal.ReadInt32(ptr, 4);
        int bits = Marshal.ReadInt16(ptr, 14);
        if (rate <= 0 || channels <= 0) return null;
        return (tag == 3 || bits == 32)
            ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)
            : new WaveFormat(rate, bits, channels);
    }

    private static void WriteFormat(IntPtr buffer, WaveFormat format)
    {
        int tag = format.Encoding == WaveFormatEncoding.IeeeFloat ? 3 : 1;
        Marshal.WriteInt16(buffer, 0, (short)tag);
        Marshal.WriteInt16(buffer, 2, (short)format.Channels);
        Marshal.WriteInt32(buffer, 4, format.SampleRate);
        Marshal.WriteInt32(buffer, 8, format.AverageBytesPerSecond);
        Marshal.WriteInt16(buffer, 12, (short)format.BlockAlign);
        Marshal.WriteInt16(buffer, 14, (short)format.BitsPerSample);
        Marshal.WriteInt16(buffer, 16, 0);
    }

    private static IEnumerable<WaveFormat> Candidates()
    {
        yield return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        yield return WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        yield return new WaveFormat(48000, 16, 2);
        yield return new WaveFormat(44100, 16, 2);
    }

    // ---------- 裸 vtable ----------

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitFn(IntPtr self, int shareMode, uint flags, long duration, long period,
        IntPtr format, IntPtr sessionGuid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetU32Fn(IntPtr self, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPtrFn(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEventFn(IntPtr self, IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VoidFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetServiceFn(IntPtr self, ref Guid iid, out IntPtr service);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferFn(IntPtr self, out IntPtr data, out uint frames, out uint flags,
        out long devicePosition, out long qpcPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseBufferFn(IntPtr self, uint frames);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PacketFn(IntPtr self, out uint frames);

    private static IntPtr Slot(IntPtr instance, int index) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance, 0), IntPtr.Size * (index + 3));

    private static T Fn<T>(IntPtr instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Slot(instance, index));

    private static InitFn InitAt(IntPtr p, int _) => Fn<InitFn>(p, 0);
    private static GetU32Fn GetU32At(IntPtr p, int i) => Fn<GetU32Fn>(p, i);
    private static GetPtrFn GetPtrAt(IntPtr p, int i) => Fn<GetPtrFn>(p, i);
    private static SetEventFn SetEventAt(IntPtr p, int i) => Fn<SetEventFn>(p, i);
    private static VoidFn VoidAt(IntPtr p, int i) => Fn<VoidFn>(p, i);
    private static GetServiceFn GetServiceAt(IntPtr p, int i) => Fn<GetServiceFn>(p, i);
    private static GetBufferFn GetBufferAt(IntPtr p, int i) => Fn<GetBufferFn>(p, i);
    private static ReleaseBufferFn ReleaseBufferAt(IntPtr p, int i) => Fn<ReleaseBufferFn>(p, i);
    private static PacketFn PacketAt(IntPtr p, int i) => Fn<PacketFn>(p, i);

    // QueryInterface 需要 ref Guid；这里给个不共享可变状态的取法。
    private static ref Guid IidAudioClientUnsafe() => ref _iidAc;
    private static ref Guid IidCaptureClientUnsafe() => ref _iidCapture;
    private static Guid _iidAc = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static Guid _iidCapture = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    // ---------- P/Invoke + 激活回调 ----------

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int Result = -1;
        public IntPtr InterfacePtr;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr = operation.GetActivateResult(out var result, out var ptr);
                Result = hr != 0 ? hr : result;
                InterfacePtr = ptr;
            }
            catch (Exception ex)
            {
                Result = ex.HResult;
                InterfacePtr = IntPtr.Zero;
            }
            finally
            {
                Done.Set();
            }
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
    }
}
