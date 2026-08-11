using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lighthouse.Native;

/// <summary>
/// Win32 entry points used for raw NTFS volume access, shell icon extraction and
/// window chrome. Kept in one place so the rest of the app stays managed-looking.
/// </summary>
internal static partial class NativeMethods
{
    // ---------------------------------------------------------------- kernel32

    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_INVALID_FUNCTION = 1;

    // File attributes we care about.
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
    public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// Pack = 4 matters: FILETIME is a pair of DWORDs and is 4-byte aligned natively,
    /// so the default 8-byte alignment of the long fields would shift every timestamp
    /// and size by four bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileAttributesEx(
        string lpFileName,
        int fInfoLevelId, // GetFileExInfoStandard = 0
        out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

    // ------------------------------------------------------- USN structures

    /// <summary>Input buffer for FSCTL_ENUM_USN_DATA.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATE_USN_JOURNAL_DATA
    {
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    /// <summary>
    /// Byte offsets inside a USN_RECORD_V2. The record is variable length so we
    /// read it out of the raw buffer rather than marshalling a struct.
    /// </summary>
    public static class UsnRecordV2
    {
        public const int RecordLength = 0;   // uint
        public const int MajorVersion = 4;   // ushort
        public const int MinorVersion = 6;   // ushort
        public const int FileReferenceNumber = 8;   // ulong
        public const int ParentFileReferenceNumber = 16;  // ulong
        public const int Usn = 24;  // long
        public const int TimeStamp = 32;  // long
        public const int Reason = 40;  // uint
        public const int SourceInfo = 44;  // uint
        public const int SecurityId = 48;  // uint
        public const int FileAttributes = 52;  // uint
        public const int FileNameLength = 56;  // ushort (bytes)
        public const int FileNameOffset = 58;  // ushort (bytes from record start)
    }

    // USN change reasons used by the live monitor.
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;

    // ----------------------------------------------------------------- shell32

    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;

    // SHGetImageList sizes.
    public const int SHIL_LARGE = 0;      // 32px
    public const int SHIL_SMALL = 1;      // 16px
    public const int SHIL_EXTRALARGE = 2; // 48px
    public const int SHIL_JUMBO = 4;      // 256px

    public const int ILD_TRANSPARENT = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        int cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "SHGetImageList", SetLastError = false)]
    public static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        out IImageList ppv);

    public static Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IImageList
    {
        // Only GetIcon is called; the earlier slots must still be declared so the
        // vtable offsets line up.
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    // -------------------------------------------------------- user32 / gdi32

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetDIBits(
        IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        IntPtr lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ---------------------------------------------------------- window chrome

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCHITTEST = 0x0084;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
}
