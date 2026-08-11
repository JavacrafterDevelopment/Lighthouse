using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static Lighthouse.Native.NativeMethods;

namespace Lighthouse.Shell;

/// <summary>
/// Friendly type descriptions ("7-Zip archive", "Application"), taken from the same
/// shell lookup Explorer's Type column uses. Cached per extension.
/// </summary>
public static class FileTypeNames
{
    private const uint SHGFI_TYPENAME = 0x000000400;

    private static readonly ConcurrentDictionary<string, string> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string ForFile(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return "File";

        return Cache.GetOrAdd(extension, static ext =>
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(
                "file" + ext, 0x80 /* FILE_ATTRIBUTE_NORMAL */, ref info,
                Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);

            string name = result != IntPtr.Zero ? info.szTypeName : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                name = ext.TrimStart('.').ToUpperInvariant() + " file";
            return name;
        });
    }

    public static string ForFolder() => "Folder";
}
