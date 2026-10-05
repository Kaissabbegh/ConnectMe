using System.Runtime.InteropServices;

namespace ConnectMe;

public sealed record DisplayInfo(int OutputIndex, string DeviceName, string MonitorName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public override string ToString()
    {
        var tag = IsPrimary ? "main screen, mirror" : "extended";
        return $"{OutputIndex + 1}: {MonitorName} {Width}x{Height} ({tag})";
    }
}

/// <summary>
/// Lists the outputs of DXGI adapter 0, in the same order FFmpeg's ddagrab uses for output_idx.
/// </summary>
public static class Displays
{
    public static List<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factoryObj) != 0) return result;
        var factory = (IDXGIFactory1)factoryObj;
        try
        {
            if (factory.EnumAdapters1(0, out var adapter) != 0) return result;
            try
            {
                for (uint i = 0; adapter.EnumOutputs(i, out var output) == 0; i++)
                {
                    try
                    {
                        output.GetDesc(out var desc);
                        var r = desc.DesktopCoordinates;
                        bool primary = r.Left == 0 && r.Top == 0;
                        result.Add(new DisplayInfo((int)i, desc.DeviceName, MonitorName(desc.DeviceName),
                            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, primary));
                    }
                    finally { Marshal.ReleaseComObject(output); }
                }
            }
            finally { Marshal.ReleaseComObject(adapter); }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return result;
    }

    static string MonitorName(string deviceName)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(deviceName, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString))
            return dd.DeviceString.Trim();
        return deviceName;
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    // COM vtables must list every inherited method in order; unused slots are placeholders.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation();
        void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        void IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
        void GetDesc(); void CheckInterfaceSupport();
        void GetDesc1();
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void GetDesc(out DXGI_OUTPUT_DESC desc);
    }
}
