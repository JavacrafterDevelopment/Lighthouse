using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lighthouse;

/// <summary>
/// One Lighthouse per folder, and a way for other programs to ask the running one
/// to come forward or to quit.
///
/// The running copy owns two named events, "Show" and "Quit", and waits on them.
/// A second launch finds "Show" already exists, sets it and exits, so the first
/// copy comes forward instead of a second index being built. <c>Lighthouse.exe
/// --quit</c> sets "Quit", which exits the running copy as tray → Exit would; the
/// Javacrafter Launcher's Stop button uses it.
///
/// Lighthouse usually runs as administrator, and Windows stops a normal program
/// from touching an administrator's objects by default. So the events are created
/// with an explicit descriptor that lets any signed-in user set them, labelled
/// medium integrity so an unelevated launcher may. Setting is all it allows: an
/// event can only be rung, not used to make Lighthouse do anything else.
/// </summary>
internal static partial class Instance
{
    // SYSTEM and Administrators: everything. Authenticated users: set and wait
    // (EVENT_MODIFY_STATE | SYNCHRONIZE). Mandatory label: medium, no write-up.
    private const string Descriptor = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100002;;;AU)S:(ML;;NW;;;ME)";

    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const int ERROR_ALREADY_EXISTS = 183;

    private static IntPtr _show, _quit;

    /// <summary>Keyed on the folder, so copies kept in different folders don't meet.</summary>
    private static string Name(string which)
    {
        var folder = AppContext.BaseDirectory.TrimEnd('\\').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
        return $"Local\\Lighthouse-{hash}-{which}";
    }

    /// <summary>Sets the running copy's event. False when no copy is running.</summary>
    public static bool Signal(string which)
    {
        var handle = OpenEvent(EVENT_MODIFY_STATE, false, Name(which));
        if (handle == IntPtr.Zero) return false;
        SetEvent(handle);
        CloseHandle(handle);
        return true;
    }

    /// <summary>
    /// Creates this copy's events. False when another copy got there first.
    /// </summary>
    public static bool Claim()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(Descriptor, 1, out var sd, IntPtr.Zero))
            throw new Win32Exception();
        try
        {
            var sa = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = sd,
                InheritHandle = 0,
            };
            _show = CreateEvent(ref sa, false, false, Name("Show"));
            bool taken = Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS;
            _quit = CreateEvent(ref sa, false, false, Name("Quit"));
            return _show != IntPtr.Zero && _quit != IntPtr.Zero && !taken;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>Waits for the events in the background and acts on them on the UI thread.</summary>
    public static void Listen(MainForm form)
    {
        var handles = new WaitHandle[]
        {
            new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = new SafeWaitHandle(_show, false) },
            new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = new SafeWaitHandle(_quit, false) },
        };
        var thread = new Thread(() =>
        {
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                if (form.IsDisposed) return;
                try
                {
                    if (which == 0) form.BeginInvoke(form.ShowFromSignal);
                    else { form.BeginInvoke(form.QuitFromSignal); return; }
                }
                catch (InvalidOperationException) { return; } // form already gone
            }
        })
        { IsBackground = true, Name = "Instance signals" };
        thread.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle; // a Win32 BOOL; kept as int so the struct is blittable
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateEvent(ref SecurityAttributes sa, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenEvent(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out IntPtr descriptor, IntPtr size);
}
