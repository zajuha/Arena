using System.Runtime.InteropServices;

namespace WinRepair.Services.Infrastructure;

/// <summary>
/// Нативные вызовы Win32 API (P/Invoke) для работы с корзиной, рабочим набором памяти,
/// точками восстановления и параметрами визуальных эффектов Windows 10/11.
/// </summary>
internal static partial class Win32NativeMethods
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    internal const uint SHERB_NOCONFIRMATION = 0x00000001;
    internal const uint SHERB_NOPROGRESSUI = 0x00000002;
    internal const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBinW(nint hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(nint hProcess);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFirmwareEnvironmentVariableW(
        string lpName,
        string lpGuid,
        nint pBuffer,
        uint nSize);

    internal const int ERROR_INVALID_FUNCTION = 1;

    /// <summary>
    /// Определяет режим загрузки ОС (UEFI или Legacy BIOS) через Win32 API GetFirmwareEnvironmentVariableW.
    /// </summary>
    public static bool IsUefiBootMode()
    {
        try
        {
            _ = GetFirmwareEnvironmentVariableW(string.Empty, "{00000000-0000-0000-0000-000000000000}", nint.Zero, 0);
            return Marshal.GetLastWin32Error() != ERROR_INVALID_FUNCTION;
        }
        catch
        {
            return true;
        }
    }
}
