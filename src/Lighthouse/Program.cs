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

        // Reading the NTFS master file table requires a raw volume handle, which
        // requires administrator. Ask once; if the user says no we fall back to a
        // (slower) directory walk rather than failing outright.
        if (!IsElevated && !args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase))
        {
            if (TryRelaunchElevated(args)) return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(InitialQuery(args)));
    }

    /// <summary>`Lighthouse.exe --query minecraft` opens with the search already filled in.</summary>
    private static string InitialQuery(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--query", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-q", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return string.Empty;
    }

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
