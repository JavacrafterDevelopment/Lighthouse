using System.Text;
using Lighthouse.Shell;

namespace Lighthouse;

/// <summary>
/// Diagnostic for the shell icon pipeline: run `Lighthouse.exe --icon-selftest` to
/// dump what the provider produces for a handful of representative paths. Useful
/// when icons come back blank and you need to know whether the shell lookup or the
/// WebView2 plumbing is at fault.
/// </summary>
internal static class IconSelfTest
{
    public static void Run()
    {
        string outDir = Path.Combine(Path.GetTempPath(), "lighthouse-icons");
        Directory.CreateDirectory(outDir);

        var samples = new (string Path, bool IsDir)[]
        {
            (@"C:\Windows", true),
            (@"C:\Windows\explorer.exe", false),
            (@"C:\sample.zip", false),
            (@"C:\sample.txt", false),
            (@"C:\sample.pdf", false),
            (@"C:\sample.docx", false),
            (@"C:\sample.unknownext", false),
        };

        using var icons = new IconProvider();
        var report = new StringBuilder();

        foreach (var (path, isDir) in samples)
        {
            string key = IconProvider.KeyFor(path, isDir);
            byte[] png;
            string error = "";
            try
            {
                png = icons.GetAsync(key, path, isDir, 48).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                png = [];
                error = ex.ToString();
            }

            bool isPng = png.Length > 8 && png[0] == 0x89 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G';
            string file = Path.Combine(outDir,
                new string(Path.GetFileName(path).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()) + ".png");
            if (png.Length > 0) File.WriteAllBytes(file, png);

            report.AppendLine($"{path,-34} key={key,-38} bytes={png.Length,-7} png={isPng} {error}");
            report.AppendLine($"  type: {(isDir ? FileTypeNames.ForFolder() : FileTypeNames.ForFile(Path.GetExtension(path)))}");
        }

        string reportPath = Path.Combine(outDir, "report.txt");
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"PNGs and report written to {outDir}");
    }
}
