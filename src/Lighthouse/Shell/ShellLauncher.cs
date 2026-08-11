using System.Diagnostics;

namespace Lighthouse.Shell;

/// <summary>
/// Opens files and folders.
///
/// Lighthouse runs elevated so it can read the master file table, and anything a
/// process launches inherits its token. Launching a random .exe from search results
/// as administrator would be a nasty surprise, so we hand the path to explorer.exe:
/// the new explorer process delegates to the already-running desktop shell (which
/// runs at the user's normal integrity level) and exits, so the target ends up
/// running unelevated, exactly as if it had been double-clicked in a folder window.
/// </summary>
public static class ShellLauncher
{
    public static void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (Program.IsElevated)
        {
            if (TryViaExplorer($"\"{path}\"")) return;
        }

        // Not elevated (or explorer is unavailable): a direct shell execute is both
        // safe and more capable.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = SafeDirectory(path),
            });
        }
        catch (Exception ex)
        {
            ShowError(path, ex);
        }
    }

    /// <summary>Opens the containing folder with the item selected.</summary>
    public static void Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (TryViaExplorer($"/select,\"{path}\"")) return;

        try
        {
            string? dir = SafeDirectory(path);
            if (dir is not null)
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(path, ex);
        }
    }

    /// <summary>Opens the folder that contains the item (or the folder itself).</summary>
    public static void OpenContainingFolder(string path)
    {
        string? dir = SafeDirectory(path);
        if (dir is null) return;
        if (TryViaExplorer($"\"{dir}\"")) return;
        Open(dir);
    }

    private static bool TryViaExplorer(string arguments)
    {
        try
        {
            string explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!File.Exists(explorer)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = arguments,
                UseShellExecute = false,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) return path;
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    private static void ShowError(string path, Exception ex)
    {
        MessageBox.Show(
            $"Could not open:\n{path}\n\n{ex.Message}",
            "Lighthouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
