using System.Diagnostics;
using System.Text;

namespace Lighthouse.Indexing;

public enum IndexPhase { Starting, Scanning, Ready, Limited, Failed }

public sealed record IndexStatus(
    IndexPhase Phase,
    string Message,
    int FileCount,
    int VolumesDone,
    int VolumesTotal,
    long ElapsedMs);

/// <summary>
/// Owns the index: scans every NTFS volume at startup, keeps it current from the
/// change journal, and hands the search engine out to the UI.
/// </summary>
public sealed class IndexService : IDisposable
{
    /// <summary>How many new entries between progress publishes during a folder walk.</summary>
    private const int ProgressEvery = 20_000;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<VolumeScanner> _scanners = [];
    private readonly List<UsnMonitor> _monitors = [];

    public FileIndex Index { get; } = new();
    public SearchEngine Search { get; }
    public IndexStatus Status { get; private set; } =
        new(IndexPhase.Starting, "Starting up", 0, 0, 0, 0);

    public event Action<IndexStatus>? StatusChanged;

    public IndexService() => Search = new SearchEngine(Index);

    public Task StartAsync(bool elevated) =>
        Task.Run(() => Build(elevated, _cts.Token), _cts.Token);

    private void Build(bool elevated, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var volumes = GetCandidateVolumes();

        Report(new IndexStatus(IndexPhase.Scanning,
            elevated ? "Reading the file table" : "Scanning folders",
            0, 0, volumes.Count, sw.ElapsedMilliseconds));

        int done = 0;
        var failures = new List<string>();

        foreach (var drive in volumes)
        {
            if (ct.IsCancellationRequested) return;
            char letter = drive.Name[0];

            bool scanned = false;
            if (elevated && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var scanner = new VolumeScanner(letter);
                    scanner.Scan(Index, ct);
                    _scanners.Add(scanner);
                    scanned = true;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    failures.Add($"{letter}: {ex.Message}");
                }
            }

            if (!scanned)
            {
                try
                {
                    WalkVolume(letter, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    failures.Add($"{letter}: {ex.Message}");
                }
            }

            done++;
            Report(new IndexStatus(IndexPhase.Scanning,
                $"Indexed {letter}:",
                Index.Count, done, volumes.Count, sw.ElapsedMilliseconds));
        }

        Search.Invalidate();

        if (elevated)
        {
            foreach (var scanner in _scanners)
            {
                var monitor = new UsnMonitor(scanner, Index, Search);
                monitor.Start(_cts.Token);
                _monitors.Add(monitor);
            }
        }

        string message = elevated
            ? $"{Index.Count:N0} items indexed"
            : $"{Index.Count:N0} items indexed — limited mode, run as administrator for the full drive index";
        if (failures.Count > 0) message += $" ({failures.Count} volume(s) skipped)";

        Report(new IndexStatus(
            elevated ? IndexPhase.Ready : IndexPhase.Limited,
            message, Index.Count, done, volumes.Count, sw.ElapsedMilliseconds));
    }

    private static List<DriveInfo> GetCandidateVolumes()
    {
        var list = new List<DriveInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                list.Add(d);
            }
            catch
            {
                // Drive disappeared or is not queryable; skip it.
            }
        }
        return list;
    }

    /// <summary>
    /// Fallback used when we cannot read the MFT (no elevation, or a non-NTFS volume).
    /// Much slower than the journal scan but needs no special rights, and results stream
    /// in as it goes so the UI is usable immediately.
    /// </summary>
    private void WalkVolume(char letter, CancellationToken ct)
    {
        string root = $"{letter}:";
        int rootIndex = Index.Add(Encoding.ASCII.GetBytes(root), -1,
                                  FileIndex.FlagDirectory | FileIndex.FlagVolumeRoot);

        var queue = new Queue<(string Path, int Index)>();
        queue.Enqueue((root + "\\", rootIndex));

        var buffer = new byte[1024];
        int sinceReport = 0;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, parent) = queue.Dequeue();

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*",
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    });
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();

                byte flags = 0;
                FileAttributes attrs;
                try { attrs = child.Attributes; }
                catch { continue; }

                bool isDir = (attrs & FileAttributes.Directory) != 0;
                if (isDir) flags |= FileIndex.FlagDirectory;
                if ((attrs & FileAttributes.Hidden) != 0) flags |= FileIndex.FlagHidden;
                if ((attrs & FileAttributes.System) != 0) flags |= FileIndex.FlagSystem;

                int len = Encoding.UTF8.GetBytes(child.Name, buffer);
                int entry = Index.Add(buffer.AsSpan(0, len), parent, flags);

                if (isDir) queue.Enqueue((child.FullName, entry));

                // Publish periodically so results fill in during a long walk instead of
                // the list staying empty until the whole volume is finished.
                if (++sinceReport >= ProgressEvery)
                {
                    sinceReport = 0;
                    Search.Invalidate();
                    Report(Status with
                    {
                        Message = $"Scanning {letter}: — {Index.Count:N0} items",
                        FileCount = Index.Count,
                    });
                }
            }
        }
    }

    private void Report(IndexStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var m in _monitors) m.Dispose();
        _cts.Dispose();
    }
}
