using System.Text;
using System.Text.Json;
using Lighthouse.Indexing;
using Lighthouse.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static Lighthouse.Native.NativeMethods;

namespace Lighthouse;

public sealed class MainForm : Form
{
    /// <summary>Static assets, served by WebView2 straight from the wwwroot folder.</summary>
    private const string VirtualHost = "lighthouse.app";

    /// <summary>
    /// Icons live on a separate origin on purpose. Requests to a folder-mapped
    /// virtual host are resolved inside WebView2 and never raise
    /// WebResourceRequested, so an endpoint we answer ourselves has to sit on a host
    /// that is not mapped.
    /// </summary>
    private const string IconHost = "lighthouse-icons.local";
    private const int ResizeBorder = 6;
    private const int HotkeyId = 0xB0A7;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly IndexService _service = new();
    private readonly IconProvider _icons = new();
    private readonly NotifyIcon _tray = new();
    private readonly ShellContextMenu _shellMenu = new();
    private readonly Settings _settings = Settings.Load();
    private string _shellMenuPath = string.Empty;

    /// <summary>Highest query generation seen; page requests within it are never dropped.</summary>
    private int _latestSeq;
    private bool _reallyClosing;
    private bool _trayHintShown;

    private readonly string _initialQuery;
    private readonly string _initialFilter;
    private readonly bool _initialTidy;
    private readonly bool _openMenuOnStart;
    private readonly bool _openSettingsOnStart;

    public MainForm(string initialQuery = "", string initialFilter = "",
                    bool initialTidy = false, bool openMenuOnStart = false,
                    bool openSettingsOnStart = false)
    {
        _initialQuery = initialQuery;
        _initialFilter = initialFilter;
        _initialTidy = initialTidy;
        _openMenuOnStart = openMenuOnStart;
        _openSettingsOnStart = openSettingsOnStart;
        Text = "Lighthouse";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 440);
        Size = new Size(1120, 700);
        // Matches the page background so startup does not flash the wrong colour
        // before WebView2 paints.
        BackColor = ColorTranslator.FromHtml(_settings.IsDark ? "#191512" : "#F3EDE1");
        KeyPreview = true;
        DoubleBuffered = true;

        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* no icon embedded */ }

        Controls.Add(_web);
        SetupTray();

        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    // ------------------------------------------------------------- lifecycle

    private async void OnLoad(object? sender, EventArgs e)
    {
        ApplyRoundedCorners();

        try
        {
            var userData = Path.Combine(AppContext.BaseDirectory, "data", "webview");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData);

            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Lighthouse needs the Microsoft Edge WebView2 runtime, which could not be started.\n\n"
                + ex.Message
                + "\n\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2/",
                "Lighthouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Allow);

        core.AddWebResourceRequestedFilter($"https://{IconHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnIconRequested;
        core.WebMessageReceived += OnWebMessage;

        _service.StatusChanged += OnIndexStatusChanged;

        // Stamp the theme before any document script runs, so a dark install never
        // flashes the light palette on the way in.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            $"document.documentElement.dataset.theme = '{(_settings.IsDark ? "dark" : "light")}';");

        core.Navigate($"https://{VirtualHost}/index.html");

        _ = _service.StartAsync(Program.IsElevated);
        RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.Space);
    }

    private void SaveSettings(JsonElement root)
    {
        if (root.TryGetProperty("closeToTray", out var tray) &&
            tray.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.CloseToTray = tray.GetBoolean();

        if (root.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String)
            _settings.Theme = theme.GetString() == "dark" ? "dark" : "light";

        _settings.Save();

        BackColor = ColorTranslator.FromHtml(_settings.IsDark ? "#191512" : "#F3EDE1");
        _tray.Text = _settings.CloseToTray
            ? "Lighthouse — Ctrl+Shift+Space"
            : "Lighthouse";
    }

    private void PostSettings()
    {
        var sb = new StringBuilder(128);
        using (var w = new Utf8JsonWriterScope(sb))
        {
            w.Writer.WriteStartObject();
            w.Writer.WriteString("evt", "settings");
            w.Writer.WriteBoolean("closeToTray", _settings.CloseToTray);
            w.Writer.WriteString("theme", _settings.IsDark ? "dark" : "light");
            w.Writer.WriteEndObject();
        }
        Post(sb.ToString());
    }

    /// <summary>Pushes anything the command line asked for, once the page can hear it.</summary>
    private void SendStartupState()
    {
        PostSettings();

        if (_initialQuery.Length > 0 || _initialFilter.Length > 0 || _initialTidy)
        {
            var sb = new StringBuilder();
            using (var w = new Utf8JsonWriterScope(sb))
            {
                w.Writer.WriteStartObject();
                w.Writer.WriteString("evt", "setQuery");
                w.Writer.WriteString("query", _initialQuery);
                if (_initialFilter.Length > 0) w.Writer.WriteString("types", _initialFilter);
                if (_initialTidy) w.Writer.WriteBoolean("tidy", true);
                w.Writer.WriteEndObject();
            }
            Post(sb.ToString());
        }

        if (_openMenuOnStart) Post("{\"evt\":\"openMenu\"}");
        if (_openSettingsOnStart) Post("{\"evt\":\"openSettings\"}");

        OnIndexStatusChanged(_service.Status);
    }

    private void OnIndexStatusChanged(IndexStatus status)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var sb = new StringBuilder();
            using (var w = new Utf8JsonWriterScope(sb))
            {
                w.Writer.WriteStartObject();
                w.Writer.WriteString("evt", "status");
                w.Writer.WriteString("phase", status.Phase.ToString().ToLowerInvariant());
                w.Writer.WriteString("message", status.Message);
                w.Writer.WriteNumber("count", status.FileCount);
                w.Writer.WriteNumber("volumesDone", status.VolumesDone);
                w.Writer.WriteNumber("volumesTotal", status.VolumesTotal);
                w.Writer.WriteNumber("elapsedMs", status.ElapsedMs);
                w.Writer.WriteEndObject();
            }
            Post(sb.ToString());
        });
    }

    // --------------------------------------------------------------- bridge

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; }
        catch { return; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";

            switch (cmd)
            {
                case "ready":
                    // The page tells us when its listener is live. Sending startup state
                    // off NavigationCompleted instead races the script and can be lost.
                    SendStartupState();
                    break;

                case "search":
                    await HandleSearchAsync(root);
                    break;

                case "open":
                    ShellLauncher.Open(GetString(root, "path"));
                    break;

                case "reveal":
                    ShellLauncher.Reveal(GetString(root, "path"));
                    break;

                case "openFolder":
                    ShellLauncher.OpenContainingFolder(GetString(root, "path"));
                    break;

                case "copy":
                    CopyToClipboard(GetString(root, "text"));
                    break;

                case "window":
                    HandleWindowCommand(GetString(root, "action"));
                    break;

                case "shellMenu":
                    HandleShellMenu(root);
                    break;

                case "shellInvoke":
                    HandleShellInvoke(root);
                    break;

                case "shellMenuClose":
                    _shellMenu.Release();
                    break;

                case "reindex":
                    _ = _service.RebuildAsync(Program.IsElevated);
                    break;

                case "saveSettings":
                    SaveSettings(root);
                    break;

            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"bridge error: {ex}");
        }
    }

    private async Task HandleSearchAsync(JsonElement root)
    {
        int id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        string query = GetString(root, "query");
        int offset = root.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
        int limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 200;
        limit = Math.Clamp(limit, 1, 2000);

        var options = new SearchOptions(
            Mode: GetString(root, "mode") == "startsWith" ? MatchMode.StartsWith : MatchMode.Smart,
            Types: GetString(root, "types") switch
            {
                "files" => TypeFilter.FilesOnly,
                "folders" => TypeFilter.FoldersOnly,
                "apps" => TypeFilter.AppsOnly,
                _ => TypeFilter.All,
            },
            IncludeHidden: !root.TryGetProperty("hidden", out var h) || h.GetBoolean(),
            IncludeSystem: root.TryGetProperty("system", out var s) && s.GetBoolean(),
            HideClutter: root.TryGetProperty("tidy", out var t) && t.GetBoolean());

        int seq = root.TryGetProperty("seq", out var seqEl) ? seqEl.GetInt32() : 0;
        _latestSeq = Math.Max(_latestSeq, seq);

        string payload = await Task.Run(() =>
        {
            // A newer keystroke already arrived, so this result would be thrown away:
            // skip the work. Paging requests share a generation, so they survive.
            if (seq < _latestSeq) return string.Empty;

            try
            {
                var result = _service.Search.Search(query, options, offset, limit);
                return BuildSearchResponse(id, seq, result);
            }
            catch (Exception ex)
            {
                // Never leave the list silently blank: say what went wrong.
                System.Diagnostics.Debug.WriteLine($"search failed: {ex}");
                return BuildSearchError(id, seq, ex);
            }
        });

        if (payload.Length > 0 && seq >= _latestSeq) Post(payload);
    }

    private static string BuildSearchError(int id, int seq, Exception ex)
    {
        var sb = new StringBuilder(256);
        using (var scope = new Utf8JsonWriterScope(sb))
        {
            var w = scope.Writer;
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteNumber("seq", seq);
            w.WriteNumber("total", 0);
            w.WriteNumber("offset", 0);
            w.WriteNumber("us", 0);
            w.WriteNumber("indexed", 0);
            w.WriteString("error", ex.GetType().Name + ": " + ex.Message);
            w.WriteStartArray("rows");
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return sb.ToString();
    }

    private string BuildSearchResponse(int id, int seq, SearchResult result)
    {
        var index = _service.Index;
        var sb = new StringBuilder(1 << 16);

        using (var scope = new Utf8JsonWriterScope(sb))
        {
            var w = scope.Writer;
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteNumber("seq", seq);
            w.WriteNumber("total", result.Total);
            w.WriteNumber("offset", result.Offset);
            w.WriteNumber("us", result.ElapsedMicroseconds);
            w.WriteNumber("indexed", index.Count);
            w.WriteStartArray("rows");

            foreach (int i in result.Page)
            {
                bool isDir = index.IsDirectory(i);
                string name = index.GetName(i);
                string dir = index.GetDirectoryPath(i);
                string full = dir.Length == 0
                    ? name
                    : dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

                string ext = isDir ? string.Empty : Path.GetExtension(name);

                w.WriteStartObject();
                w.WriteString("n", name);
                w.WriteString("e", ext);
                w.WriteString("d", dir);
                w.WriteString("p", full);
                w.WriteBoolean("dir", isDir);
                w.WriteString("k", IconProvider.KeyFor(full, isDir));
                w.WriteString("t", isDir ? FileTypeNames.ForFolder() : FileTypeNames.ForFile(ext));

                if (TryStat(full, out long size, out long modifiedMs))
                {
                    w.WriteNumber("s", isDir ? -1 : size);
                    w.WriteNumber("m", modifiedMs);
                }
                else
                {
                    w.WriteNumber("s", -1);
                    w.WriteNumber("m", 0);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return sb.ToString();
    }

    /// <summary>Size and modified time, fetched only for the rows about to be drawn.</summary>
    private static bool TryStat(string path, out long size, out long modifiedMs)
    {
        size = 0;
        modifiedMs = 0;
        try
        {
            if (!GetFileAttributesEx(path, 0, out var data)) return false;
            size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
            modifiedMs = DateTimeOffset.FromFileTime(data.ftLastWriteTime).ToUnixTimeMilliseconds();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Post(string json)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
        catch { /* view torn down */ }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    // ---------------------------------------------------------------- icons

    private async void OnIconRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var q = ParseQuery(new Uri(e.Request.Uri).Query);
            string key = q.GetValueOrDefault("k", string.Empty);
            string path = q.GetValueOrDefault("p", string.Empty);
            bool isDir = q.GetValueOrDefault("d") == "1";
            int size = int.TryParse(q.GetValueOrDefault("s"), out int sz) ? sz : 48;

            byte[] png = await _icons.GetAsync(key, path, isDir, size);

            var stream = new MemoryStream(png, writable: false);
            // Headers are CRLF-separated with no trailing terminator.
            e.Response = _web.CoreWebView2!.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                "Content-Type: image/png\r\nCache-Control: public, max-age=86400");
        }
        catch
        {
            try
            {
                e.Response = _web.CoreWebView2!.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", string.Empty);
            }
            catch { /* view torn down */ }
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------------------------------------------------- shell context menu

    /// <summary>
    /// Builds Explorer's own menu for a path and ships it to the UI as data. Runs on
    /// the UI thread because shell extensions are apartment threaded; the first open
    /// for a given file type pays for loading that extension's DLL.
    /// </summary>
    private void HandleShellMenu(JsonElement root)
    {
        int id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        string path = GetString(root, "path");
        bool extended = root.TryGetProperty("extended", out var ex) && ex.GetBoolean();

        List<ShellMenuItem> items;
        try
        {
            items = _shellMenu.Build(path, Handle, extended);
            _shellMenuPath = path;
        }
        catch
        {
            // A misbehaving shell extension should cost us the menu, not the app.
            items = [];
            _shellMenuPath = string.Empty;
        }

        var sb = new StringBuilder(4096);
        using (var scope = new Utf8JsonWriterScope(sb))
        {
            var w = scope.Writer;
            w.WriteStartObject();
            w.WriteString("evt", "shellMenu");
            w.WriteNumber("id", id);
            w.WriteString("path", path);
            w.WriteStartArray("items");
            ShellMenuJson.Write(w, items);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        Post(sb.ToString());
    }

    private void HandleShellInvoke(JsonElement root)
    {
        int id = root.TryGetProperty("menuId", out var el) ? el.GetInt32() : -1;
        if (id < 0) return;

        // Plain "Open" goes through the same de-elevated route as double-clicking.
        // Invoking it here would run the target with Lighthouse's admin token, which
        // is not what someone expects from opening a file out of a search result.
        if (Program.IsElevated
            && string.Equals(_shellMenu.GetVerb(id), "open", StringComparison.OrdinalIgnoreCase)
            && _shellMenuPath.Length > 0)
        {
            string target = _shellMenuPath;
            _shellMenu.Release();
            ShellLauncher.Open(target);
            return;
        }

        string directory = string.Empty;
        try { directory = Path.GetDirectoryName(_shellMenuPath) ?? string.Empty; } catch { }

        try
        {
            _shellMenu.Invoke(id, Handle, directory);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"That command could not be run.\n\n{ex.Message}",
                "Lighthouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _shellMenu.Release();
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.StartsWith('?')) query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    // -------------------------------------------------------- window chrome

    private void HandleWindowCommand(string action)
    {
        switch (action)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;

            case "maximize":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                break;

            case "close":
                Close();
                break;

            case "drag":
                // Borderless windows have no real caption, so hand the drag to the
                // window manager as if the user had grabbed one.
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                break;
        }
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* pre-Windows 11 */ }
    }

    protected override void WndProc(ref Message m)
    {
        // Borderless: synthesise resize handles around the edges.
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                var p = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
                bool left = p.X <= ResizeBorder;
                bool right = p.X >= ClientSize.Width - ResizeBorder;
                bool top = p.Y <= ResizeBorder;
                bool bottom = p.Y >= ClientSize.Height - ResizeBorder;

                m.Result = (left, right, top, bottom) switch
                {
                    (true, _, true, _) => HTTOPLEFT,
                    (_, true, true, _) => HTTOPRIGHT,
                    (true, _, _, true) => HTBOTTOMLEFT,
                    (_, true, _, true) => HTBOTTOMRIGHT,
                    (true, _, _, _) => HTLEFT,
                    (_, true, _, _) => HTRIGHT,
                    (_, _, true, _) => HTTOP,
                    (_, _, _, true) => HTBOTTOM,
                    _ => HTCLIENT,
                };
            }
            return;
        }

        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            ToggleVisibility();
            return;
        }

        base.WndProc(ref m);
    }

    // ------------------------------------------------------------------ tray

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Lighthouse", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _reallyClosing = true; Close(); });

        _tray.Text = "Lighthouse — Ctrl+Shift+Space";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { _tray.Icon = SystemIcons.Application; }
    }

    private void ToggleVisibility()
    {
        if (Visible && WindowState != FormWindowState.Minimized) Hide();
        else ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        SetForegroundWindow(Handle);
        try { _web.CoreWebView2?.PostWebMessageAsJson("{\"evt\":\"focus\"}"); } catch { }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // By default closing hides to the tray so the index (and the few seconds it
        // took to build) survives; Exit from the tray menu really quits. Turning the
        // setting off makes the close button quit outright.
        // The in-page close button reaches here through Form.Close(), which does not
        // always report UserClosing, so accept None too. Shutdown, task manager and
        // the tray's Exit all report something else and fall through to quitting.
        bool userClosed = e.CloseReason is CloseReason.UserClosing or CloseReason.None;
        if (_settings.CloseToTray && !_reallyClosing && userClosed)
        {
            e.Cancel = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.BalloonTipTitle = "Lighthouse is still running";
                _tray.BalloonTipText = "Press Ctrl+Shift+Space to search again, or right-click the tray icon to exit.";
                _tray.ShowBalloonTip(4000);
            }
            return;
        }

        UnregisterHotKey(Handle, HotkeyId);
        _tray.Visible = false;
        _tray.Dispose();
        _service.Dispose();
        _icons.Dispose();
        _shellMenu.Dispose();
    }
}

/// <summary>Small helper so response building reads linearly.</summary>
internal sealed class Utf8JsonWriterScope : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly StringBuilder _target;

    public Utf8JsonWriter Writer { get; }

    public Utf8JsonWriterScope(StringBuilder target)
    {
        _target = target;
        Writer = new Utf8JsonWriter(_stream, new JsonWriterOptions { SkipValidation = true });
    }

    public void Dispose()
    {
        Writer.Flush();
        _target.Append(Encoding.UTF8.GetString(_stream.GetBuffer(), 0, (int)_stream.Length));
        Writer.Dispose();
        _stream.Dispose();
    }
}
