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
///
/// A rebuild scans into a brand new index and swaps it in only once it is complete,
/// so searching keeps working against the old one the whole time it runs.
/// </summary>
public sealed class IndexService : IDisposable
{
    /// <summary>How many new entries between progress publishes during a folder walk.</summary>
    private const int ProgressEvery = 20_000;

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource _monitorCts = new();
    private readonly List<UsnMonitor> _monitors = [];
    private int _rebuilding;

    public FileIndex Index { get; private set; } = new();
    public SearchEngine Search { get; private set; }
    public IndexStatus Status { get; private set; } =
        new(IndexPhase.Starting, "Starting up", 0, 0, 0, 0);

    public event Action<IndexStatus>? StatusChanged;

    public IndexService() => Search = new SearchEngine(Index);

    /// <summary>True while a scan is running, so the UI can hide the re-index button.</summary>
    public bool IsBusy => Volatile.Read(ref _rebuilding) != 0;

    public Task StartAsync(bool elevated) => Run(elevated, Index, Search, rebuild: false);

    /// <summary>
    /// Throws away the current index and scans everything again. Safe to call while
    /// the app is in use; returns immediately if a scan is already running.
    /// </summary>
    public Task RebuildAsync(bool elevated)
    {
        var fresh = new FileIndex();
        return Run(elevated, fresh, new SearchEngine(fresh), rebuild: true);
    }

    private Task Run(bool elevated, FileIndex target, SearchEngine search, bool rebuild)
    {
        if (Interlocked.Exchange(ref _rebuilding, 1) != 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                Build(elevated, target, search, rebuild, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            finally
            {
                Volatile.Write(ref _rebuilding, 0);
            }
        }, _cts.Token);
    }

    private void Build(bool elevated, FileIndex target, SearchEngine search, bool rebuild, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var volumes = GetCandidateVolumes();
        var scanners = new List<VolumeScanner>();

        Report(new IndexStatus(IndexPhase.Scanning,
            rebuild ? "Re-indexing" : elevated ? "Reading the file table" : "Scanning folders",
            rebuild ? Index.Count : 0, 0, volumes.Count, sw.ElapsedMilliseconds));

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
                    scanner.Scan(target, ct);
                    scanners.Add(scanner);
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
                    WalkVolume(letter, target, search, rebuild, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    failures.Add($"{letter}: {ex.Message}");
                }
            }

            done++;
            Report(new IndexStatus(IndexPhase.Scanning,
                rebuild ? $"Re-indexing {letter}:" : $"Indexed {letter}:",
                rebuild ? Index.Count : target.Count, done, volumes.Count, sw.ElapsedMilliseconds));
        }

        // Swap the finished index in as one step; until now every search has been
        // running against the previous one.
        StopMonitors();
        Index = target;
        Search = search;
        search.Invalidate();

        if (elevated)
        {
            _monitorCts = new CancellationTokenSource();
            foreach (var scanner in scanners)
            {
                var monitor = new UsnMonitor(scanner, target, search);
                monitor.Start(_monitorCts.Token);
                _monitors.Add(monitor);
            }
        }

        string message = elevated
            ? $"{target.Count:N0} items indexed"
            : $"{target.Count:N0} items indexed — limited mode, run as administrator for the full drive index";
        if (failures.Count > 0) message += $" ({failures.Count} volume(s) skipped)";

        Report(new IndexStatus(
            elevated ? IndexPhase.Ready : IndexPhase.Limited,
            message, target.Count, done, volumes.Count, sw.ElapsedMilliseconds));
    }

    private void StopMonitors()
    {
        _monitorCts.Cancel();
        foreach (var m in _monitors) m.Dispose();
        _monitors.Clear();
        _monitorCts.Dispose();
        _monitorCts = new CancellationTokenSource();
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
    private void WalkVolume(char letter, FileIndex target, SearchEngine search, bool rebuild, CancellationToken ct)
    {
        string root = $"{letter}:";
        int rootIndex = target.Add(Encoding.ASCII.GetBytes(root), -1,
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
                int entry = target.Add(buffer.AsSpan(0, len), parent, flags);

                if (isDir) queue.Enqueue((child.FullName, entry));

                // Publish periodically so results fill in during a long walk instead of
                // the list staying empty until the whole volume is finished. During a
                // rebuild the live index is untouched, so only the count moves.
                if (++sinceReport >= ProgressEvery)
                {
                    sinceReport = 0;
                    if (!rebuild) search.Invalidate();
                    Report(Status with
                    {
                        Message = rebuild
                            ? $"Re-indexing {letter}: — {target.Count:N0} items"
                            : $"Scanning {letter}: — {target.Count:N0} items",
                        FileCount = rebuild ? Index.Count : target.Count,
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
        StopMonitors();
        _monitorCts.Dispose();
        _cts.Dispose();
    }
}
