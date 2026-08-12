using System.Runtime.InteropServices;
using System.Text;
using static Lighthouse.Native.NativeMethods;

namespace Lighthouse.Shell;

/// <summary>One entry read out of the real Windows context menu.</summary>
public sealed class ShellMenuItem
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public bool Separator { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Checked { get; init; }
    public bool IsDefault { get; init; }
    /// <summary>Menu bitmap the shell extension supplied, as a PNG data URI.</summary>
    public string? Icon { get; init; }
    public List<ShellMenuItem>? Children { get; init; }
}

/// <summary>
/// Reads the genuine Explorer context menu for a path — including whatever third
/// party extensions have registered, so an installed 7-Zip contributes its own
/// cascading submenu — and hands it over as plain data for the UI to draw in
/// Lighthouse's own styling.
///
/// The COM objects must stay alive between building the menu and invoking a
/// command, so one instance holds the state for the menu that is currently open.
/// Everything here must run on the UI thread: shell extensions are apartment
/// threaded and some expect a message pump.
/// </summary>
public sealed class ShellContextMenu : IDisposable
{
    private const uint CmdFirst = 0x8000;
    private const uint CmdLast = 0xEFFF;

    private IContextMenu? _menu;
    private IContextMenu2? _menu2;
    private IShellFolder? _parent;
    private IntPtr _pidl = IntPtr.Zero;
    private IntPtr _hmenu = IntPtr.Zero;

    /// <summary>
    /// Builds the menu for <paramref name="path"/>. Pass <paramref name="extended"/>
    /// to include the extra verbs Explorer only shows on shift+right-click.
    /// </summary>
    public List<ShellMenuItem> Build(string path, IntPtr hwnd, bool extended, Action<string>? trace = null)
    {
        Release();

        var items = new List<ShellMenuItem>();
        if (string.IsNullOrWhiteSpace(path)) return items;

        _pidl = ILCreateFromPath(path);
        trace?.Invoke($"ILCreateFromPath -> 0x{_pidl.ToInt64():X}");
        if (_pidl == IntPtr.Zero) return items;

        var iidFolder = IID_IShellFolder;
        int hr = SHBindToParent(_pidl, ref iidFolder, out _parent, out IntPtr childPidl);
        trace?.Invoke($"SHBindToParent -> hr=0x{hr:X8} parent={(_parent is null ? "null" : "ok")} childPidl=0x{childPidl.ToInt64():X}");
        if (hr != 0 || _parent is null) return items;

        var iidMenu = IID_IContextMenu;
        var apidl = new[] { childPidl };
        hr = _parent.GetUIObjectOf(hwnd, 1, apidl, ref iidMenu, IntPtr.Zero, out IntPtr pcm);
        trace?.Invoke($"GetUIObjectOf -> hr=0x{hr:X8} pcm=0x{pcm.ToInt64():X}");
        if (hr != 0 || pcm == IntPtr.Zero) return items;

        _menu = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
        Marshal.Release(pcm);
        _menu2 = _menu as IContextMenu2;
        trace?.Invoke($"IContextMenu2 supported: {_menu2 is not null}");

        _hmenu = CreatePopupMenu();
        if (_hmenu == IntPtr.Zero) return items;

        uint flags = CMF_EXPLORE | (extended ? CMF_EXTENDEDVERBS : CMF_NORMAL);
        hr = _menu.QueryContextMenu(_hmenu, 0, CmdFirst, CmdLast, flags);
        trace?.Invoke($"QueryContextMenu -> hr=0x{hr:X8} count={GetMenuItemCount(_hmenu)}");
        if (hr < 0) return items;

        ReadMenu(_hmenu, items, 0);
        return items;
    }

    private void ReadMenu(IntPtr hmenu, List<ShellMenuItem> into, int depth)
    {
        if (depth > 4) return; // shell menus never nest this far; guards against loops

        // Extensions that build their submenus lazily only fill them once they see
        // the popup message.
        if (depth > 0) _menu2?.HandleMenuMsg(WM_INITMENUPOPUP, hmenu, IntPtr.Zero);

        int count = GetMenuItemCount(hmenu);
        var buffer = Marshal.AllocHGlobal(512 * sizeof(char));

        try
        {
            for (uint i = 0; i < count; i++)
            {
                var mii = new MENUITEMINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_BITMAP | MIIM_FTYPE,
                    dwTypeData = buffer,
                    cch = 512,
                };

                if (!GetMenuItemInfo(hmenu, i, true, ref mii)) continue;

                if ((mii.fType & MFT_SEPARATOR) != 0)
                {
                    // Never open with, or double up on, separators.
                    if (into.Count > 0 && !into[^1].Separator) into.Add(new ShellMenuItem { Separator = true });
                    continue;
                }

                string label = mii.cch > 0 ? Marshal.PtrToStringUni(buffer, (int)mii.cch) ?? "" : "";
                if (label.Length == 0) continue;

                List<ShellMenuItem>? children = null;
                if (mii.hSubMenu != IntPtr.Zero)
                {
                    children = [];
                    ReadMenu(mii.hSubMenu, children, depth + 1);
                    if (children.Count == 0) continue; // empty submenu is not worth showing
                }

                into.Add(new ShellMenuItem
                {
                    Id = children is null ? (int)(mii.wID - CmdFirst) : -1,
                    Label = CleanLabel(label),
                    Enabled = (mii.fState & MFS_GRAYED) == 0,
                    Checked = (mii.fState & MFS_CHECKED) != 0,
                    IsDefault = (mii.fState & MFS_DEFAULT) != 0,
                    Icon = ToDataUri(GdiPng.FromBitmap(mii.hbmpItem)),
                    Children = children,
                });
            }

            while (into.Count > 0 && into[^1].Separator) into.RemoveAt(into.Count - 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Strips accelerator markers and any tab-separated shortcut text. In menu
    /// strings a doubled ampersand is an escaped literal one, while a lone
    /// ampersand is the underline marker and is dropped.
    /// </summary>
    private static string CleanLabel(string raw)
    {
        int tab = raw.IndexOf('\t');
        if (tab >= 0) raw = raw[..tab];

        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '&') { sb.Append(raw[i]); continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '&') { sb.Append('&'); i++; }
        }
        return sb.ToString().Trim();
    }

    private static string? ToDataUri(byte[]? png) =>
        png is null ? null : "data:image/png;base64," + Convert.ToBase64String(png);

    /// <summary>
    /// The canonical, language-independent verb for a command ("open", "runas",
    /// "delete"), or null if the handler does not expose one. Used to special-case
    /// commands we would rather route elsewhere.
    /// </summary>
    public string? GetVerb(int id)
    {
        if (_menu is null || id < 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(260 * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            if (_menu.GetCommandString((IntPtr)id, GCS_VERBW, IntPtr.Zero, buffer, 260) != 0) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Runs one of the verbs returned by <see cref="Build"/>.</summary>
    public bool Invoke(int id, IntPtr hwnd, string directory)
    {
        if (_menu is null || id < 0) return false;

        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE,
            hwnd = hwnd,
            lpVerb = (IntPtr)(uint)id,   // MAKEINTRESOURCE of the command offset
            lpVerbW = (IntPtr)(uint)id,
            lpDirectory = directory,
            lpDirectoryW = directory,
            nShow = SW_SHOWNORMAL,
        };

        return _menu.InvokeCommand(ref info) >= 0;
    }

    public void Release()
    {
        if (_hmenu != IntPtr.Zero) { DestroyMenu(_hmenu); _hmenu = IntPtr.Zero; }

        // _menu2 is the same runtime callable wrapper as _menu obtained through a
        // QueryInterface cast, so releasing both would over-release it.
        _menu2 = null;
        if (_menu is not null) { Marshal.ReleaseComObject(_menu); _menu = null; }
        if (_parent is not null) { Marshal.ReleaseComObject(_parent); _parent = null; }
        if (_pidl != IntPtr.Zero) { ILFree(_pidl); _pidl = IntPtr.Zero; }
    }

    public void Dispose() => Release();
}
