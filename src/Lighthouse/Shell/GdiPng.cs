using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static Lighthouse.Native.NativeMethods;

namespace Lighthouse.Shell;

/// <summary>
/// Turns GDI handles into PNG bytes for the web front-end.
///
/// Goes through GetDIBits rather than Icon.ToBitmap so the 32-bit alpha channel
/// survives, falling back to the 1-bit mask for the handful of legacy icons that
/// carry no alpha at all.
/// </summary>
internal static class GdiPng
{
    public static byte[]? FromIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        if (!GetIconInfo(hIcon, out var ii)) return null;

        try
        {
            return Convert(ii.hbmColor, ii.hbmMask);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    /// <summary>
    /// Converts a menu item bitmap. Menus use a few negative sentinel values
    /// (HBMMENU_CALLBACK and friends) in place of a real handle; those are not
    /// bitmaps and must be skipped.
    /// </summary>
    public static byte[]? FromBitmap(IntPtr hbmp)
    {
        if (hbmp == IntPtr.Zero || hbmp.ToInt64() is > -20 and < 0) return null;
        return Convert(hbmp, IntPtr.Zero);
    }

    private static unsafe byte[]? Convert(IntPtr hbmColor, IntPtr hbmMask)
    {
        if (hbmColor == IntPtr.Zero) return null;

        var info = new Native.NativeMethods.BITMAP();
        if (GetObject(hbmColor, Marshal.SizeOf<Native.NativeMethods.BITMAP>(), ref info) == 0) return null;

        int w = info.bmWidth, h = info.bmHeight;
        if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;

        int stride = w * 4;
        var pixels = new byte[stride * h];

        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (!ReadBgra(hdc, hbmColor, pixels, w, h)) return null;

            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) { hasAlpha = true; break; }
            }

            if (!hasAlpha && !ApplyMaskAlpha(hdc, hbmMask, pixels, w, h))
            {
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
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

    private static unsafe bool ReadBgra(IntPtr hdc, IntPtr hbmp, byte[] dest, int w, int h)
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

        fixed (byte* p = dest)
        {
            return GetDIBits(hdc, hbmp, 0, (uint)h, (IntPtr)p, ref bi, DIB_RGB_COLORS) != 0;
        }
    }

    /// <summary>Legacy icons carry transparency in a 1-bit mask: a set bit means transparent.</summary>
    private static bool ApplyMaskAlpha(IntPtr hdc, IntPtr hbmMask, byte[] pixels, int w, int h)
    {
        if (hbmMask == IntPtr.Zero) return false;

        var mask = new byte[w * 4 * h];
        if (!ReadBgra(hdc, hbmMask, mask, w, h)) return false;

        for (int i = 0; i < pixels.Length; i += 4)
            pixels[i + 3] = mask[i] != 0 ? (byte)0 : (byte)255;

        return true;
    }
}
