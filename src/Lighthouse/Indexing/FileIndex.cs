using System.Text;

namespace Lighthouse.Indexing;

/// <summary>
/// Flat, append-only store of every file and folder on the indexed volumes.
///
/// Layout is struct-of-arrays with names packed into one big UTF-8 pool. That
/// keeps a few million entries in roughly 100 MB instead of the ~1 GB a
/// List&lt;class&gt; of the same data would cost, and it lets the search loop walk
/// contiguous memory.
///
/// Threading: entries are only ever appended, and arrays are only ever replaced
/// wholesale when growing. A reader that snapshots (array reference, count) is
/// therefore always looking at a consistent prefix, so searches need no lock.
/// Deletions flip a bit on an existing entry, which a concurrent reader may miss
/// for one query — harmless.
/// </summary>
public sealed class FileIndex
{
    public const byte FlagDirectory = 1 << 0;
    public const byte FlagHidden = 1 << 1;
    public const byte FlagSystem = 1 << 2;
    public const byte FlagVolumeRoot = 1 << 3;
    public const byte FlagDeleted = 1 << 4;
    /// <summary>Set when the name contains bytes outside ASCII.</summary>
    public const byte FlagNonAscii = 1 << 5;

    private byte[] _names = new byte[1 << 20];
    private int _namesLen;

    private int[] _nameOffset = new int[1 << 16];
    private ushort[] _nameLen = new ushort[1 << 16];
    private int[] _parent = new int[1 << 16];
    private byte[] _flags = new byte[1 << 16];

    private int _count;
    private int _version;
    private readonly object _writeLock = new();

    public int Count => Volatile.Read(ref _count);

    /// <summary>Bumped on every mutation so caches upstream know to drop.</summary>
    public int Version => Volatile.Read(ref _version);

    // Snapshots for the search loop. Read these once per query.
    public byte[] NamePool => _names;
    public int[] NameOffsets => _nameOffset;
    public ushort[] NameLengths => _nameLen;
    public byte[] Flags => _flags;
    public int[] Parents => _parent;

    public ReadOnlySpan<byte> GetNameBytes(int i) => _names.AsSpan(_nameOffset[i], _nameLen[i]);

    public string GetName(int i)
    {
        var span = GetNameBytes(i);
        return (_flags[i] & FlagNonAscii) == 0
            ? string.Create(span.Length, (_names, _nameOffset[i], span.Length), static (dst, s) =>
              {
                  var src = s.Item1.AsSpan(s.Item2, s.Item3);
                  for (int k = 0; k < src.Length; k++) dst[k] = (char)src[k];
              })
            : Encoding.UTF8.GetString(span);
    }

    public bool IsDirectory(int i) => (_flags[i] & FlagDirectory) != 0;

    /// <summary>
    /// Adds one entry. <paramref name="parent"/> is an index into this same store,
    /// or -1 for a volume root.
    /// </summary>
    public int Add(ReadOnlySpan<byte> utf8Name, int parent, byte flags)
    {
        lock (_writeLock)
        {
            int i = _count;

            if (i >= _nameOffset.Length) GrowEntries(i + 1);
            if (_namesLen + utf8Name.Length > _names.Length) GrowNames(_namesLen + utf8Name.Length);

            utf8Name.CopyTo(_names.AsSpan(_namesLen));
            _nameOffset[i] = _namesLen;
            _nameLen[i] = (ushort)utf8Name.Length;
            _parent[i] = parent;

            foreach (byte b in utf8Name)
            {
                if (b >= 0x80) { flags |= FlagNonAscii; break; }
            }
            _flags[i] = flags;

            _namesLen += utf8Name.Length;
            Volatile.Write(ref _count, i + 1);
            Interlocked.Increment(ref _version);
            return i;
        }
    }

    public void SetParent(int i, int parent) => _parent[i] = parent;

    public void MarkDeleted(int i)
    {
        _flags[i] |= FlagDeleted;
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Renames in place by appending the new bytes to the pool and repointing the
    /// entry. Keeping the entry identity matters: a renamed folder's children still
    /// resolve their paths through it.
    /// </summary>
    public void Rename(int i, ReadOnlySpan<byte> utf8Name)
    {
        lock (_writeLock)
        {
            if (_namesLen + utf8Name.Length > _names.Length) GrowNames(_namesLen + utf8Name.Length);
            utf8Name.CopyTo(_names.AsSpan(_namesLen));
            _nameOffset[i] = _namesLen;
            _nameLen[i] = (ushort)utf8Name.Length;
            _namesLen += utf8Name.Length;

            byte f = (byte)(_flags[i] & ~FlagNonAscii);
            foreach (byte b in utf8Name)
            {
                if (b >= 0x80) { f |= FlagNonAscii; break; }
            }
            _flags[i] = f;
            Interlocked.Increment(ref _version);
        }
    }

    private void GrowEntries(int needed)
    {
        int cap = Math.Max(_nameOffset.Length * 2, needed);
        // Replace wholesale so concurrent readers keep a valid older snapshot.
        var no = new int[cap]; Array.Copy(_nameOffset, no, _count); _nameOffset = no;
        var nl = new ushort[cap]; Array.Copy(_nameLen, nl, _count); _nameLen = nl;
        var pa = new int[cap]; Array.Copy(_parent, pa, _count); _parent = pa;
        var fl = new byte[cap]; Array.Copy(_flags, fl, _count); _flags = fl;
    }

    private void GrowNames(int needed)
    {
        long cap = Math.Max((long)_names.Length * 2, needed);
        cap = Math.Min(cap, int.MaxValue - 64);
        var n = new byte[cap];
        Array.Copy(_names, n, _namesLen);
        _names = n;
    }

    /// <summary>
    /// Builds the full path for an entry by walking the parent chain. Only called
    /// for rows the user can actually see, so the walk cost never matters.
    /// </summary>
    public string GetFullPath(int index)
    {
        Span<int> chain = stackalloc int[64];
        var overflow = default(List<int>);
        int depth = 0;

        int cur = index;
        while (cur >= 0)
        {
            if (depth < chain.Length) chain[depth] = cur;
            else (overflow ??= new List<int>()).Add(cur);
            depth++;

            if ((_flags[cur] & FlagVolumeRoot) != 0) break;
            int next = _parent[cur];
            if (next == cur) break; // defensive: corrupt parent link
            cur = next;
        }

        var sb = new StringBuilder(128);
        for (int d = depth - 1; d >= 0; d--)
        {
            int e = d < chain.Length ? chain[d] : overflow![d - chain.Length];
            AppendName(sb, e);
            bool isRoot = (_flags[e] & FlagVolumeRoot) != 0;
            if (d > 0) sb.Append('\\');
            else if (isRoot) sb.Append('\\'); // "C:" -> "C:\"
        }
        return sb.ToString();
    }

    /// <summary>Path of the containing folder, e.g. "C:\Games\Minecraft".</summary>
    public string GetDirectoryPath(int index)
    {
        int p = _parent[index];
        if (p < 0) return string.Empty;
        return GetFullPath(p);
    }

    private void AppendName(StringBuilder sb, int i)
    {
        var span = GetNameBytes(i);
        if ((_flags[i] & FlagNonAscii) == 0)
        {
            foreach (byte b in span) sb.Append((char)b);
        }
        else
        {
            sb.Append(Encoding.UTF8.GetString(span));
        }
    }
}
