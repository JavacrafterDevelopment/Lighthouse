using System.Runtime.InteropServices;
using System.Text;
using static Lighthouse.Native.NativeMethods;

namespace Lighthouse.Indexing;

/// <summary>
/// Tails a volume's NTFS change journal so the index stays correct as files come
/// and go, without ever rescanning. Polls rather than blocking inside
/// DeviceIoControl so shutdown is immediate.
/// </summary>
public sealed class UsnMonitor : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private const int PollIntervalMs = 750;

    private readonly VolumeScanner _scanner;
    private readonly FileIndex _index;
    private readonly SearchEngine _search;
    private Thread? _thread;
    private long _nextUsn;

    public UsnMonitor(VolumeScanner scanner, FileIndex index, SearchEngine search)
    {
        _scanner = scanner;
        _index = index;
        _search = search;
        _nextUsn = scanner.NextUsn;
    }

    public void Start(CancellationToken ct)
    {
        if (_scanner.JournalId == 0) return; // volume has no usable journal

        _thread = new Thread(() => Loop(ct))
        {
            IsBackground = true,
            Name = $"Lighthouse.Usn.{_scanner.Letter}",
        };
        _thread.Start();
    }

    private unsafe void Loop(CancellationToken ct)
    {
        using var volume = VolumeScanner.OpenVolume(_scanner.Letter);
        if (volume.IsInvalid) return;

        IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
        var nameBytes = new byte[1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = new READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = _nextUsn,
                    ReasonMask = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE
                               | USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0, // return immediately; we poll instead
                    UsnJournalID = _scanner.JournalId,
                };

                bool ok = DeviceIoControl(
                    volume, FSCTL_READ_USN_JOURNAL,
                    (IntPtr)(&request), sizeof(READ_USN_JOURNAL_DATA_V0),
                    buffer, BufferSize, out int bytesReturned, IntPtr.Zero);

                if (!ok)
                {
                    // Journal was reset or overflowed; give up rather than serve stale data.
                    return;
                }

                if (bytesReturned <= sizeof(long))
                {
                    if (ct.WaitHandle.WaitOne(PollIntervalMs)) return;
                    continue;
                }

                byte* p = (byte*)buffer;
                _nextUsn = *(long*)p;

                bool changed = false;
                int offset = sizeof(long);
                while (offset + 60 <= bytesReturned)
                {
                    byte* rec = p + offset;
                    int recordLength = *(int*)(rec + UsnRecordV2.RecordLength);
                    if (recordLength <= 0 || offset + recordLength > bytesReturned) break;

                    if (*(ushort*)(rec + UsnRecordV2.MajorVersion) == 2)
                        changed |= Apply(rec, nameBytes);

                    offset += recordLength;
                }

                if (changed) _search.Invalidate();

                if (ct.WaitHandle.WaitOne(bytesReturned > sizeof(long) ? 50 : PollIntervalMs)) return;
            }
        }
        catch
        {
            // A failed monitor just means the index stops updating live; searching still works.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private unsafe bool Apply(byte* rec, byte[] nameBytes)
    {
        ulong frn = *(ulong*)(rec + UsnRecordV2.FileReferenceNumber);
        ulong parentFrn = *(ulong*)(rec + UsnRecordV2.ParentFileReferenceNumber);
        uint reason = *(uint*)(rec + UsnRecordV2.Reason);
        uint attrs = *(uint*)(rec + UsnRecordV2.FileAttributes);
        ushort nameLenBytes = *(ushort*)(rec + UsnRecordV2.FileNameLength);
        ushort nameOff = *(ushort*)(rec + UsnRecordV2.FileNameOffset);

        int utf8Len;
        fixed (byte* dst = nameBytes)
        {
            utf8Len = Encoding.UTF8.GetBytes((char*)(rec + nameOff), nameLenBytes / 2, dst, nameBytes.Length);
        }
        var name = nameBytes.AsSpan(0, utf8Len);

        var map = _scanner.FrnToIndex;

        if ((reason & USN_REASON_FILE_DELETE) != 0)
        {
            lock (map)
            {
                if (map.TryGetValue(frn, out int existing))
                {
                    _index.MarkDeleted(existing);
                    map.Remove(frn);
                    return true;
                }
            }
            return false;
        }

        if ((reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) != 0)
        {
            byte flags = 0;
            if ((attrs & FILE_ATTRIBUTE_DIRECTORY) != 0) flags |= FileIndex.FlagDirectory;
            if ((attrs & FILE_ATTRIBUTE_HIDDEN) != 0) flags |= FileIndex.FlagHidden;
            if ((attrs & FILE_ATTRIBUTE_SYSTEM) != 0) flags |= FileIndex.FlagSystem;

            lock (map)
            {
                int parent = map.TryGetValue(parentFrn, out int p) ? p : _scanner.RootIndex;

                if (map.TryGetValue(frn, out int existing))
                {
                    // Rename: keep the entry identity so children keep resolving through it.
                    _index.Rename(existing, name);
                    _index.SetParent(existing, parent);
                }
                else
                {
                    map[frn] = _index.Add(name, parent, flags);
                }
            }
            return true;
        }

        return false;
    }

    public void Dispose() => _thread?.Join(TimeSpan.FromMilliseconds(1500));
}
