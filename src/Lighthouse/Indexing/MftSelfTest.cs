using System.Diagnostics;
using Lighthouse.Indexing;

namespace Lighthouse;

/// <summary>
/// Diagnostic for the master file table reader: run `Lighthouse.exe --mft-selftest`
/// to scan every NTFS volume and print record counts, timing and a few resolved
/// paths. Needs administrator; without it every volume reports access denied,
/// which is itself the expected result.
/// </summary>
internal static class MftSelfTest
{
    public static void Run()
    {
        Console.WriteLine($"elevated: {Program.IsElevated}");
        Console.WriteLine();

        var index = new FileIndex();

        foreach (var drive in DriveInfo.GetDrives())
        {
            string format;
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                format = drive.DriveFormat;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{drive.Name}  unavailable: {ex.Message}");
                continue;
            }

            char letter = drive.Name[0];
            if (!format.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{letter}:  {format} - no master file table, would use the folder walk");
                continue;
            }

            int before = index.Count;
            var sw = Stopwatch.StartNew();
            try
            {
                var scanner = new VolumeScanner(letter);
                int added = scanner.Scan(index, CancellationToken.None);
                sw.Stop();

                Console.WriteLine(
                    $"{letter}:  {added:N0} records in {sw.ElapsedMilliseconds:N0} ms " +
                    $"({added / Math.Max(1.0, sw.Elapsed.TotalSeconds) / 1000:N0}k/s), " +
                    $"journal id {scanner.JournalId:X}, next usn {scanner.NextUsn:N0}");

                // Resolved paths are the real proof: they only come out right if the
                // parent reference numbers were linked up correctly.
                int shown = 0;
                for (int i = before; i < index.Count && shown < 4; i++)
                {
                    if (index.IsDirectory(i)) continue;
                    string path = index.GetFullPath(i);
                    bool exists = File.Exists(path);
                    Console.WriteLine($"      {(exists ? "ok  " : "MISS")} {path}");
                    shown++;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"{letter}:  failed after {sw.ElapsedMilliseconds} ms - {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"total indexed: {index.Count:N0}");
        Console.WriteLine($"managed heap:  {GC.GetTotalMemory(false) / (1024 * 1024):N0} MB");
    }
}
