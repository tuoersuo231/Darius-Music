using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CurrentLyrics.Services;

/// <summary>
/// D 方案（本地逐字对齐）的硬件探测：这台机器有没有能吃 GPU 推理的 D3D12 适配器、驱动版本、
/// 显存，以及它能到的最高 FeatureLevel。
///
/// 只读，不改任何状态、不申请显存（D3D12CreateDevice 拿到设备后立刻 Release），所以可以在主程序
/// 正在播放时跑，也可以在安装器里跑。
/// </summary>
public static class GpuProbe
{
    public sealed class Adapter
    {
        public int Index;
        public string Name = "";
        public uint VendorId;
        public uint DeviceId;
        public long DedicatedVramBytes;
        public long SharedSystemBytes;
        public string Luid = "";
        public bool IsSoftware;
        /// <summary>none / 11_0 / 11_1 / 12_0 / 12_1</summary>
        public string D3D12 = "none";
        public string DriverVersion = "";
        public string DriverDate = "";

        public long DedicatedVramMB => DedicatedVramBytes / (1024 * 1024);
    }

    private const int D3D_FEATURE_LEVEL_11_0 = 0xB000;
    private const int D3D_FEATURE_LEVEL_11_1 = 0xB100;
    private const int D3D_FEATURE_LEVEL_12_0 = 0xC000;
    private const int D3D_FEATURE_LEVEL_12_1 = 0xC100;

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidD3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel,
        ref Guid riid, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);

    private static T Slot<T>(IntPtr com, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), slot * IntPtr.Size));

    /// <summary>DXGI 的适配器枚举：EnumAdapters1 会把软件适配器（Microsoft Basic Render Driver）
    /// 一起列出来，这里保留但标记，由调用方决定要不要用。</summary>
    public static List<Adapter> ReadAdapters()
    {
        var list = new List<Adapter>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = IidDxgiFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return list;

            var enumAdapters = Slot<EnumAdapters1Fn>(factory, 12);   // IDXGIFactory1::EnumAdapters1
            var drivers = ReadDriverTable();

            for (uint i = 0; i < 16; i++)
            {
                if (enumAdapters(factory, i, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    list.Add(Describe(adapter, (int)i, drivers));
                }
                finally
                {
                    Slot<ReleaseFn>(adapter, 2)(adapter);
                }
            }
            return list;
        }
        catch
        {
            return list;
        }
        finally
        {
            if (factory != IntPtr.Zero) Slot<ReleaseFn>(factory, 2)(factory);
        }
    }

    private static Adapter Describe(IntPtr adapter, int index, Dictionary<string, (string, string)> drivers)
    {
        // DXGI_ADAPTER_DESC1 是 312 字节：WCHAR[128] + 4×UINT + 3×SIZE_T + LUID + UINT。
        // 直接读偏移，免得为一个只读结构去定义 Marshal 布局。
        var buf = Marshal.AllocHGlobal(312);
        try
        {
            var getDesc = Slot<GetDesc1Fn>(adapter, 10);   // IDXGIAdapter1::GetDesc1
            var info = new Adapter { Index = index };
            if (getDesc(adapter, buf) >= 0)
            {
                info.Name = Marshal.PtrToStringUni(buf, 128)?.TrimEnd('\0') ?? "";
                info.VendorId = (uint)Marshal.ReadInt32(buf, 256);
                info.DeviceId = (uint)Marshal.ReadInt32(buf, 260);
                info.DedicatedVramBytes = Marshal.ReadInt64(buf, 272);
                info.SharedSystemBytes = Marshal.ReadInt64(buf, 288);
                long luid = Marshal.ReadInt64(buf, 296);
                info.Luid = luid.ToString("X16");
                info.IsSoftware = (Marshal.ReadInt32(buf, 304) & 2) != 0;
            }
            info.D3D12 = ProbeFeatureLevel(adapter);
            if (drivers.TryGetValue(info.Name, out var d))
            {
                info.DriverVersion = d.Item1;
                info.DriverDate = d.Item2;
            }
            return info;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>能建出设备就算支持。逐个降级试，最高能到哪档就是哪档；全失败返回 none。
    /// 注意：D3D12CreateDevice 失败时 ppDevice 的内容是未定义的，只有 HRESULT 成功才能碰它。</summary>
    private static string ProbeFeatureLevel(IntPtr adapter)
    {
        foreach (var (level, name) in new[]
                 {
                     (D3D_FEATURE_LEVEL_12_1, "12_1"),
                     (D3D_FEATURE_LEVEL_12_0, "12_0"),
                     (D3D_FEATURE_LEVEL_11_1, "11_1"),
                     (D3D_FEATURE_LEVEL_11_0, "11_0"),
                 })
        {
            try
            {
                var iid = IidD3D12Device;
                if (D3D12CreateDevice(adapter, level, ref iid, out var device) >= 0 && device != IntPtr.Zero)
                {
                    Slot<ReleaseFn>(device, 2)(device);
                    return name;
                }
            }
            catch { }
        }
        return "none";
    }

    /// <summary>DXGI 不给驱动版本，注册表里显示适配器那一栏才有（DisplayClassName\0000）。
    /// 键名 = DriverDesc，值 = DriverVersion / DriverDate。</summary>
    private static Dictionary<string, (string, string)> ReadDriverTable()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root == null) return map;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is not string desc || desc.Length == 0) continue;
                var version = key.GetValue("DriverVersion") as string ?? "";
                var date = key.GetValue("DriverDate") as string ?? "";
                map[desc] = (version, date);
            }
        }
        catch { }
        return map;
    }

    /// <summary>给 --gpu-probe 用的 JSON。检测项都带"为什么"，出问题时能一眼看出是哪一层不行。</summary>
    public static string ToJson(string? modelPath, string? extraJson)
    {
        var adapters = ReadAdapters();
        var best = adapters.FirstOrDefault(a => !a.IsSoftware && a.D3D12 != "none");
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = best != null,
            ["reason"] = best != null ? "ok" : (adapters.Count == 0 ? "no-dxgi-adapter" : "no-d3d12-device"),
            ["os"] = Environment.OSVersion.VersionString,
            ["processBitness"] = Environment.Is64BitProcess ? 64 : 32,
            ["adapters"] = adapters.Select(a => new Dictionary<string, object?>
            {
                ["index"] = a.Index,
                ["name"] = a.Name,
                ["vendorId"] = "0x" + a.VendorId.ToString("X4"),
                ["deviceId"] = "0x" + a.DeviceId.ToString("X4"),
                ["vramMB"] = a.DedicatedVramMB,
                ["sharedMB"] = a.SharedSystemBytes / (1024 * 1024),
                ["luid"] = a.Luid,
                ["software"] = a.IsSoftware,
                ["d3d12"] = a.D3D12,
                ["driverVersion"] = a.DriverVersion,
                ["driverDate"] = a.DriverDate,
            }).ToList(),
            ["chosen"] = best?.Index,
            ["model"] = modelPath ?? "",
            ["modelBytes"] = SafeLength(modelPath),
        };
        if (extraJson != null) payload["run"] = System.Text.Json.JsonSerializer.Deserialize<object>(extraJson);
        return System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static long SafeLength(string? path)
    {
        try { return path != null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
