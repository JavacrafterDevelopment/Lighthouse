using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static Lighthouse.Native.NativeMethods;

namespace Lighthouse.Shell;

/// <summary>
/// Produces the icon Explorer would show for a path, as PNG bytes.
///
/// Association-based types (.zip, .txt, ...) are resolved from a fake path with
/// SHGFI_USEFILEATTRIBUTES so no disk access happens and the result can be cached
/// per extension — that is what makes ".zip shows the 7-Zip icon" fall out for
/// free, because we are asking the shell the same question Explorer asks.
/// Types whose icon lives inside the file itself (.exe, .lnk, .ico) are resolved
/// per path and cached per path.
///
/// All shell calls run on one dedicated STA thread; COM shell extensions are not
/// reliably callable from arbitrary pool threads.
/// </summary>
public sealed class IconProvider : IDisposable
{
    private sealed record Job(string Key, string Path, bool IsDirectory, int Size, TaskCompletionSource<byte[]> Completion);

    /// <summary>Extensions whose icon is embedded in the file rather than in the association.</summary>
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".cur", ".ani", ".scr", ".cpl", ".msc", ".url", ".appref-ms",
    };

    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<byte[]>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
    private readonly Thread _worker;
    private byte[]? _fallback;

    public IconProvider()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Lighthouse.Icons",
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>
    /// Stable cache key for a path. Everything that shares an icon shares a key, so
    /// a folder full of .zip files costs exactly one shell lookup.
    /// </summary>
    public static string KeyFor(string path, bool isDirectory)
    {
        if (isDirectory) return "dir";
        string ext = System.IO.Path.GetExtension(path);
        if (ext.Length == 0) return "file:";
        return PerFileExtensions.Contains(ext) ? "path:" + path.ToLowerInvariant() : "ext:" + ext.ToLowerInvariant();
    }

    public bool TryGetCached(string key, out byte[] png) => _cache.TryGetValue(key, out png!);

    public Task<byte[]> GetAsync(string key, string path, bool isDirectory, int size)
    {
        if (_cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);

        return _inFlight.GetOrAdd(key, _ =>
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(new Job(key, path, isDirectory, size, tcs));
            }
            catch (InvalidOperationException)
            {
                tcs.TrySetResult(Fallback());
            }
            return tcs.Task;
        });
    }

    private void WorkerLoop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            byte[] png;
            try
            {
                png = Render(job) ?? Fallback();
            }
            catch
            {
                png = Fallback();
            }

            _cache[job.Key] = png;
            _inFlight.TryRemove(job.Key, out _);
            job.Completion.TrySetResult(png);
        }
    }

    private byte[]? Render(Job job)
    {
        int iconIndex = GetSystemIconIndex(job.Key, job.Path, job.IsDirectory);
        if (iconIndex < 0) return null;

        int shil = job.Size switch
        {
            <= 16 => SHIL_SMALL,
            <= 32 => SHIL_LARGE,
            <= 48 => SHIL_EXTRALARGE,
            _ => SHIL_JUMBO,
        };

        IntPtr hIcon = IntPtr.Zero;
        var iid = IID_IImageList;
        if (SHGetImageList(shil, ref iid, out var list) == 0 && list is not null)
        {
            list.GetIcon(iconIndex, ILD_TRANSPARENT, out hIcon);
            Marshal.ReleaseComObject(list);
        }

        if (hIcon == IntPtr.Zero) return null;

        try
        {
            return IconToPng(hIcon);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static int GetSystemIconIndex(string key, string path, bool isDirectory)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_SYSICONINDEX;
        string queryPath;
        uint attributes;

        if (isDirectory)
        {
            // Generic folder icon, resolved without touching the directory.
            queryPath = "folder";
            attributes = FILE_ATTRIBUTE_DIRECTORY;
            flags |= SHGFI_USEFILEATTRIBUTES;
        }
        else if (key.StartsWith("path:", StringComparison.Ordinal) && File.Exists(path))
        {
            // Icon is inside the file (an .exe's own artwork, a shortcut's target).
            queryPath = path;
            attributes = 0;
        }
        else
        {
            // Ask the shell what this extension looks like, exactly as Explorer would,
            // without requiring the file to exist.
            string ext = System.IO.Path.GetExtension(path);
            queryPath = string.IsNullOrEmpty(ext) ? "file" : "file" + ext;
            attributes = 0x80; // FILE_ATTRIBUTE_NORMAL
            flags |= SHGFI_USEFILEATTRIBUTES;
        }

        IntPtr result = SHGetFileInfo(queryPath, attributes, ref info, Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero)
        {
            // Fall back to the association lookup when the per-file read failed
            // (file vanished, access denied, broken shortcut).
            if (attributes == 0)
            {
                string ext = System.IO.Path.GetExtension(path);
                info = new SHFILEINFO();
                result = SHGetFileInfo(
                    string.IsNullOrEmpty(ext) ? "file" : "file" + ext, 0x80, ref info,
                    Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
            }
            if (result == IntPtr.Zero) return -1;
        }

        return info.iIcon;
    }

    /// <summary>
    /// Converts an HICON to PNG. Goes through GetDIBits rather than Icon.ToBitmap so
    /// the 32-bit alpha channel survives, with a fall back to the 1-bit mask for the
    /// handful of legacy icons that have no alpha.
    /// </summary>
    private static unsafe byte[]? IconToPng(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var ii)) return null;

        try
        {
            var bmp = new Native.NativeMethods.BITMAP();
            if (GetObject(ii.hbmColor, Marshal.SizeOf<Native.NativeMethods.BITMAP>(), ref bmp) == 0) return null;

            int w = bmp.bmWidth, h = bmp.bmHeight;
            if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;

            int stride = w * 4;
            var pixels = new byte[stride * h];

            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                var bi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // negative => top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                };

                fixed (byte* p = pixels)
                {
                    if (GetDIBits(hdc, ii.hbmColor, 0, (uint)h, (IntPtr)p, ref bi, DIB_RGB_COLORS) == 0)
                        return null;
                }

                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0) { hasAlpha = true; break; }
                }

                if (!hasAlpha && !ApplyMaskAlpha(hdc, ii.hbmMask, pixels, w, h))
                {
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }

            using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * stride, data.Scan0 + y * data.Stride, stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            using var ms = new MemoryStream(4096);
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    /// <summary>Legacy icons carry transparency in a 1-bit mask: set bit means transparent.</summary>
    private static unsafe bool ApplyMaskAlpha(IntPtr hdc, IntPtr hbmMask, byte[] pixels, int w, int h)
    {
        if (hbmMask == IntPtr.Zero) return false;

        int stride = w * 4;
        var mask = new byte[stride * h];
        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };

        fixed (byte* p = mask)
        {
            if (GetDIBits(hdc, hbmMask, 0, (uint)h, (IntPtr)p, ref bi, DIB_RGB_COLORS) == 0) return false;
        }

        for (int i = 0; i < pixels.Length; i += 4)
            pixels[i + 3] = mask[i] != 0 ? (byte)0 : (byte)255;

        return true;
    }

    /// <summary>Neutral parchment-toned document glyph, used when the shell gives us nothing.</summary>
    private byte[] Fallback()
    {
        if (_fallback is not null) return _fallback;

        using var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(Color.FromArgb(255, 232, 223, 205));
            using var edge = new Pen(Color.FromArgb(255, 168, 150, 122), 2f);
            var body = new Rectangle(9, 5, 30, 38);
            g.FillRectangle(fill, body);
            g.DrawRectangle(edge, body);
            using var line = new Pen(Color.FromArgb(255, 168, 150, 122), 1.5f);
            for (int y = 14; y <= 34; y += 7) g.DrawLine(line, 15, y, 33, y);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return _fallback = ms.ToArray();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
