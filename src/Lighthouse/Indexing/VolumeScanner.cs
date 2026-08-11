using System.Runtime.InteropServices;
using System.Text;
using Lighthouse.Native;
using Microsoft.Win32.SafeHandles;
using static Lighthouse.Native.NativeMethods;

namespace Lighthouse.Indexing;

/// <summary>
/// Reads every file and folder record straight out of a volume's NTFS master file
/// table using FSCTL_ENUM_USN_DATA. This is the same trick Everything uses: it
/// walks the MFT sequentially instead of doing millions of directory opens, so a
/// full 1M-file volume lands in a couple of seconds.
///
/// Requires a raw volume handle, which requires administrator.
/// </summary>
public sealed class VolumeScanner
{
    private const int BufferSize = 1 << 20; // 1 MB per DeviceIoControl round trip

    public char Letter { get; }
    public int RootIndex { get; private set; } = -1;

    /// <summary>Maps NTFS file reference numbers to entry indices, for live journal updates.</summary>
    public Dictionary<ulong, int> FrnToIndex { get; } = new();

    /// <summary>USN to resume the live monitor from, captured before the scan.</summary>
    public long NextUsn { get; private set; }
    public ulong JournalId { get; private set; }

    public VolumeScanner(char letter) => Letter = char.ToUpperInvariant(letter);

    public static SafeFileHandle OpenVolume(char letter)
    {
        return CreateFile(
            $@"\\.\{char.ToUpperInvariant(letter)}:",
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);
    }

    /// <summary>
    /// Enumerates the whole MFT into <paramref name="index"/>. Returns the number of
    /// entries added, or throws with a readable message on failure.
    /// </summary>
    public unsafe int Scan(FileIndex index, CancellationToken ct)
    {
        using var volume = OpenVolume(Letter);
        if (volume.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"Cannot open volume {Letter}: (error {err}). Administrator rights are required to read the file table.");
        }

        CaptureJournalPosition(volume);

        // Synthetic root so every entry has somewhere to hang off.
        RootIndex = index.Add(Encoding.ASCII.GetBytes($"{Letter}:"), -1,
                              FileIndex.FlagDirectory | FileIndex.FlagVolumeRoot);

        int firstEntry = index.Count;
        var parentFrns = new ulong[1 << 16];
        int localCount = 0;

        IntPtr outBuf = Marshal.AllocHGlobal(BufferSize);
        try
        {
            var input = new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue,
            };

            var nameUtf8 = new byte[1024];

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // `input` is a local, so it is already fixed; no pinning needed.
                bool ok = DeviceIoControl(
                    volume, FSCTL_ENUM_USN_DATA,
                    (IntPtr)(&input), sizeof(MFT_ENUM_DATA_V0),
                    outBuf, BufferSize,
                    out int bytesReturned, IntPtr.Zero);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_HANDLE_EOF) break;
                    throw new IOException($"Reading the file table of {Letter}: failed (error {err}).");
                }

                if (bytesReturned <= sizeof(ulong)) break;

                byte* p = (byte*)outBuf;
                input.StartFileReferenceNumber = *(ulong*)p;

                int offset = sizeof(ulong);
                while (offset + 60 <= bytesReturned)
                {
                    byte* rec = p + offset;
                    int recordLength = *(int*)(rec + UsnRecordV2.RecordLength);
                    if (recordLength <= 0 || offset + recordLength > bytesReturned) break;

                    ushort major = *(ushort*)(rec + UsnRecordV2.MajorVersion);
                    if (major != 2) { offset += recordLength; continue; }

                    ulong frn = *(ulong*)(rec + UsnRecordV2.FileReferenceNumber);
                    ulong parentFrn = *(ulong*)(rec + UsnRecordV2.ParentFileReferenceNumber);
                    uint attrs = *(uint*)(rec + UsnRecordV2.FileAttributes);
                    ushort nameLenBytes = *(ushort*)(rec + UsnRecordV2.FileNameLength);
                    ushort nameOff = *(ushort*)(rec + UsnRecordV2.FileNameOffset);

                    if (nameOff + nameLenBytes > recordLength) { offset += recordLength; continue; }

                    char* nameChars = (char*)(rec + nameOff);
                    int nameCharCount = nameLenBytes / 2;

                    int utf8Len;
                    fixed (byte* dst = nameUtf8)
                    {
                        utf8Len = Encoding.UTF8.GetBytes(nameChars, nameCharCount, dst, nameUtf8.Length);
                    }

                    byte flags = 0;
                    if ((attrs & FILE_ATTRIBUTE_DIRECTORY) != 0) flags |= FileIndex.FlagDirectory;
                    if ((attrs & FILE_ATTRIBUTE_HIDDEN) != 0) flags |= FileIndex.FlagHidden;
                    if ((attrs & FILE_ATTRIBUTE_SYSTEM) != 0) flags |= FileIndex.FlagSystem;

                    int entry = index.Add(nameUtf8.AsSpan(0, utf8Len), -1, flags);

                    if (localCount >= parentFrns.Length)
                        Array.Resize(ref parentFrns, parentFrns.Length * 2);
                    parentFrns[localCount++] = parentFrn;

                    // Directories are the only things that can be parents, and their
                    // FRNs are unique. Files with hard links can repeat an FRN; last
                    // one wins, which only affects which name we resolve links under.
                    FrnToIndex[frn] = entry;

                    offset += recordLength;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
        }

        // Second pass: turn parent FRNs into entry indices. Anything whose parent is
        // not in the table (top-level items, whose parent is the root record that the
        // enumeration does not return) hangs off the volume root.
        for (int i = 0; i < localCount; i++)
        {
            int entry = firstEntry + i;
            index.SetParent(entry,
                FrnToIndex.TryGetValue(parentFrns[i], out int p) && p != entry ? p : RootIndex);
        }

        return localCount;
    }

    /// <summary>
    /// Records where the change journal is right now, so the live monitor can pick up
    /// from exactly the point the scan snapshotted. Creates the journal if the volume
    /// does not have one yet.
    /// </summary>
    private unsafe void CaptureJournalPosition(SafeFileHandle volume)
    {
        var data = new USN_JOURNAL_DATA_V0();
        bool ok = DeviceIoControl(volume, FSCTL_QUERY_USN_JOURNAL,
            IntPtr.Zero, 0, (IntPtr)(&data), sizeof(USN_JOURNAL_DATA_V0),
            out _, IntPtr.Zero);

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err is ERROR_JOURNAL_NOT_ACTIVE or ERROR_INVALID_FUNCTION)
            {
                var create = new CREATE_USN_JOURNAL_DATA
                {
                    MaximumSize = 32 * 1024 * 1024,
                    AllocationDelta = 4 * 1024 * 1024,
                };
                DeviceIoControl(volume, FSCTL_CREATE_USN_JOURNAL,
                    (IntPtr)(&create), sizeof(CREATE_USN_JOURNAL_DATA),
                    IntPtr.Zero, 0, out _, IntPtr.Zero);

                ok = DeviceIoControl(volume, FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0, (IntPtr)(&data), sizeof(USN_JOURNAL_DATA_V0),
                    out _, IntPtr.Zero);
            }
        }

        if (ok)
        {
            JournalId = data.UsnJournalID;
            NextUsn = data.NextUsn;
        }
    }
}
