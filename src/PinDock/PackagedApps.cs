using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PinDock;

/// <summary>
/// MSIX / Microsoft Store 应用没有可直接访问的快捷方式文件，只能通过 shell 的“应用”
/// 虚拟文件夹以 AUMID 定位。这里统一使用 <c>shell:AppsFolder\&lt;AUMID&gt;</c> 作为收藏路径，
/// 该路径同时可用于 ShellExecute 启动和图标提取。
/// </summary>
public static class PackagedApps
{
    public const string PathPrefix = @"shell:AppsFolder\";

    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    public static bool IsPackagedPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// “应用”文件夹还包含以文件路径或普通 Win32 标识注册的条目，它们已由开始菜单/桌面文件扫描覆盖；
    /// 只有带应用 ID 的 MSIX/商店应用（PackageFamilyName!ApplicationId）才用 shell:AppsFolder 路径表示。
    /// </summary>
    private static bool IsPackagedAppId(string aumid) =>
        aumid.Contains('!') && !aumid.Contains('\\') && !aumid.Contains('/');

    public static string? TryGetDisplayName(string packagedPath)
    {
        if (!IsPackagedPath(packagedPath)) return null;

        Guid iid = IidShellItem;
        int hr = SHCreateItemFromParsingName(packagedPath, IntPtr.Zero, ref iid, out IShellItem item);
        if (hr < 0) return null;
        try
        {
            hr = item.GetDisplayName(SigdnNormalDisplay, out string name);
            return hr >= 0 && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>枚举开始菜单“应用”文件夹中的 MSIX / Microsoft Store 应用。</summary>
    public static IReadOnlyList<InstalledApp> Enumerate()
    {
        Guid iid = IidShellItem;
        int hr = SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out IShellItem folder);
        if (hr < 0)
            throw new Win32Exception(hr, "无法打开 Windows“应用”文件夹。");

        try
        {
            Guid bhid = BhidEnumItems;
            Guid iidEnum = IidEnumShellItems;
            hr = folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out IntPtr enumPtr);
            if (hr < 0)
                throw new Win32Exception(hr, "无法枚举 Windows 已安装应用。");

            try
            {
                var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
                var apps = new List<InstalledApp>();
                var buffer = new IShellItem[1];
                while (items.Next(1, buffer, out uint fetched) == 0 && fetched == 1)
                {
                    IShellItem item = buffer[0];
                    buffer[0] = null!;
                    try
                    {
                        if (item.GetDisplayName(SigdnNormalDisplay, out string name) < 0 ||
                            item.GetDisplayName(SigdnDesktopAbsoluteParsing, out string aumid) < 0 ||
                            string.IsNullOrWhiteSpace(name) ||
                            !IsPackagedAppId(aumid))
                            continue;
                        apps.Add(new InstalledApp(name.Trim(), PathPrefix + aumid.Trim(), "开始菜单"));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
                return apps;
            }
            finally
            {
                Marshal.Release(enumPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IntPtr psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
