using System.Diagnostics;
using System.Security.Principal;

namespace Lighthouse;

internal static class Program
{
    /// <summary>Set once at startup; true when we can read raw volumes (i.e. the MFT).</summary>
    public static bool IsElevated { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        IsElevated = CheckElevated();

        if (args.Contains("--icon-selftest", StringComparer.OrdinalIgnoreCase))
        {
            IconSelfTest.Run();
            return;
        }

        if (args.Contains("--mft-selftest", StringComparer.OrdinalIgnoreCase))
        {
            MftSelfTest.Run();
            return;
        }

        int menuAt = Array.FindIndex(args, a => a.Equals("--menu-selftest", StringComparison.OrdinalIgnoreCase));
        if (menuAt >= 0)
        {
            string? target = menuAt + 1 < args.Length && !args[menuAt + 1].StartsWith('-')
                ? args[menuAt + 1]
                : null;
            int jsonAt = Array.FindIndex(args, a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
            string? jsonOut = jsonAt >= 0 && jsonAt + 1 < args.Length ? args[jsonAt + 1] : null;

            MenuSelfTest.Run(target, args.Contains("--extended", StringComparer.OrdinalIgnoreCase), jsonOut);
            return;
        }

        // Reading the NTFS master file table requires a raw volume handle, which
        // requires administrator. Ask once; if the user says no we fall back to a
        // (slower) directory walk rather than failing outright.
        if (!IsElevated && !args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase))
        {
            if (TryRelaunchElevated(args)) return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(
            ArgValue(args, "--query", "-q") ?? string.Empty,
            NormaliseFilter(ArgValue(args, "--filter", "-f")),
            args.Contains("--open-menu", StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Reads "--name value" from the command line.</summary>
    private static string? ArgValue(string[] args, string name, string alias)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals(alias, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static string NormaliseFilter(string? value) => value?.ToLowerInvariant() switch
    {
        "apps" or "app" => "apps",
        "files" or "file" => "files",
        "folders" or "folder" or "dirs" => "folders",
        _ => string.Empty,
    };

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restarts this process with the "runas" verb. Returns true when a new elevated
    /// process was started and this one should exit.
    /// </summary>
    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = string.Join(' ', args.Select(Quote)),
            };

            Process.Start(psi);
            return true;
        }
        catch
        {
            // User cancelled the UAC prompt (or elevation is unavailable):
            // carry on unelevated in limited mode.
            return false;
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
