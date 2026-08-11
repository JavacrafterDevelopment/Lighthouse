using System.Text;

namespace Lighthouse.Indexing;

public enum MatchMode
{
    /// <summary>Prefix hits rank first, then word starts, then anywhere in the name.</summary>
    Smart = 0,
    /// <summary>Only names that begin with the query.</summary>
    StartsWith = 1,
}

public enum TypeFilter { All = 0, FilesOnly = 1, FoldersOnly = 2 }

public sealed record SearchOptions(
    MatchMode Mode = MatchMode.Smart,
    TypeFilter Types = TypeFilter.All,
    bool IncludeHidden = true,
    bool IncludeSystem = false);

public sealed class SearchResult
{
    public required int[] Page { get; init; }
    public required int Total { get; init; }
    public required int Offset { get; init; }
    public long ElapsedMicroseconds { get; set; }
}

/// <summary>
/// Ranks and filters index entries as the user types.
///
/// Two things keep this instant on a few million files: matching runs over the
/// packed UTF-8 name pool with a vectorised first-byte skip, and extending a query
/// ("min" -> "mine") only rescans the previous result set instead of the whole
/// index.
/// </summary>
public sealed class SearchEngine
{
    private const int TierExact = 0;
    private const int TierPrefix = 1;
    private const int TierWordStart = 2;
    private const int TierContains = 3;

    private static readonly byte[] Fold = BuildFoldTable();

    private readonly FileIndex _index;
    private readonly object _gate = new();

    // Previous full (unsorted, unpaged) match set, used for incremental narrowing.
    private string _lastQuery = string.Empty;
    private bool _hasLast;
    private SearchOptions _lastOptions = new();
    private int[] _lastMatches = [];
    private int _lastCount;
    private int _lastVersion = -1;
    private string _lastPathQuery = string.Empty;

    // Ordered view of the current match set, reused across page requests.
    private int[] _sorted = [];
    private int _sortedNeed = -1;
    private string _sortedKey = string.Empty;

    public SearchEngine(FileIndex index) => _index = index;

    /// <summary>Drops the incremental cache; call after the index changes.</summary>
    public void Invalidate()
    {
        lock (_gate) { _hasLast = false; _lastVersion = -1; _sortedKey = string.Empty; }
    }

    public SearchResult Search(string query, SearchOptions options, int offset, int limit)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            query ??= string.Empty;

            // A path fragment ("games\mine") matches the last segment by name and
            // then requires the earlier part to appear in the folder path.
            string nameQuery = query;
            string pathQuery = string.Empty;
            int sep = query.LastIndexOfAny(['\\', '/']);
            if (sep >= 0)
            {
                pathQuery = query[..sep].Replace('/', '\\');
                nameQuery = query[(sep + 1)..];
            }

            int total = BuildMatches(nameQuery, pathQuery, options);
            var matches = _lastMatches;

            if (pathQuery.Length > 0)
            {
                // Compacts _lastMatches in place; _lastCount is updated to match so the
                // next keystroke narrows from the path-filtered set.
                total = FilterByPath(matches, total, pathQuery);
            }
            _lastPathQuery = pathQuery;

            int[] page = SelectPage(matches, total, nameQuery, offset, limit);

            sw.Stop();
            return new SearchResult
            {
                Page = page,
                Total = total,
                Offset = offset,
                ElapsedMicroseconds = sw.Elapsed.Ticks / 10,
            };
        }
    }

    // ------------------------------------------------------------ matching

    private int BuildMatches(string query, string pathQuery, SearchOptions options)
    {
        int indexCount = _index.Count;
        int version = _index.Version;

        // Extending a query can only ever shrink the match set, so rescan the previous
        // results instead of the whole index. This is what keeps typing fluid.
        bool canNarrow =
            _hasLast &&
            _lastVersion == version &&
            _lastOptions == options &&
            // The cached set was already narrowed by the previous path filter, so it is
            // only a superset of the new result when the path part is unchanged.
            string.Equals(_lastPathQuery, pathQuery, StringComparison.OrdinalIgnoreCase) &&
            _lastQuery.Length > 0 &&
            query.Length > _lastQuery.Length &&
            query.StartsWith(_lastQuery, StringComparison.OrdinalIgnoreCase);

        int[] source = canNarrow ? _lastMatches : [];
        int sourceCount = canNarrow ? _lastCount : indexCount;

        var qBytes = ToFoldedBytes(query, out bool queryIsAscii);

        int[] result;
        int count;

        if (canNarrow)
        {
            result = new int[sourceCount];
            count = ScanSubset(source, sourceCount, qBytes, queryIsAscii, query, options, result);
        }
        else
        {
            (result, count) = ScanAll(indexCount, qBytes, queryIsAscii, query, options);
        }

        _lastQuery = query;
        _lastOptions = options;
        _lastMatches = result;
        _lastCount = count;
        _lastVersion = version;
        _hasLast = true;
        return count;
    }

    private (int[] matches, int count) ScanAll(
        int indexCount, byte[] q, bool queryIsAscii, string query, SearchOptions options)
    {
        int partitions = Math.Clamp(Environment.ProcessorCount, 1, 16);
        int chunk = Math.Max(4096, indexCount / partitions + 1);
        partitions = (indexCount + chunk - 1) / chunk;
        if (partitions <= 0) return ([], 0);

        var buffers = new int[partitions][];
        var counts = new int[partitions];

        Parallel.For(0, partitions, p =>
        {
            int start = p * chunk;
            int end = Math.Min(indexCount, start + chunk);
            var buf = new int[Math.Max(16, (end - start) / 4)];
            int n = 0;

            for (int i = start; i < end; i++)
            {
                if (!Accept(i, q, queryIsAscii, query, options)) continue;
                if (n == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                buf[n++] = i;
            }

            buffers[p] = buf;
            counts[p] = n;
        });

        int total = 0;
        foreach (int c in counts) total += c;

        var all = new int[total];
        int at = 0;
        for (int p = 0; p < partitions; p++)
        {
            if (counts[p] == 0) continue;
            Array.Copy(buffers[p], 0, all, at, counts[p]);
            at += counts[p];
        }
        return (all, total);
    }

    private int ScanSubset(
        int[] source, int sourceCount, byte[] q, bool queryIsAscii,
        string query, SearchOptions options, int[] dest)
    {
        int n = 0;
        for (int k = 0; k < sourceCount; k++)
        {
            int i = source[k];
            if (Accept(i, q, queryIsAscii, query, options)) dest[n++] = i;
        }
        return n;
    }

    private bool Accept(int i, byte[] q, bool queryIsAscii, string query, SearchOptions options)
    {
        var flags = _index.Flags;
        byte f = flags[i];

        if ((f & (FileIndex.FlagVolumeRoot | FileIndex.FlagDeleted)) != 0) return false;
        bool isDir = (f & FileIndex.FlagDirectory) != 0;
        if (options.Types == TypeFilter.FilesOnly && isDir) return false;
        if (options.Types == TypeFilter.FoldersOnly && !isDir) return false;
        if (!options.IncludeHidden && (f & FileIndex.FlagHidden) != 0) return false;
        if (!options.IncludeSystem && (f & FileIndex.FlagSystem) != 0) return false;

        if (q.Length == 0) return true;

        var name = _index.NamePool.AsSpan(_index.NameOffsets[i], _index.NameLengths[i]);
        return Tier(name, q, queryIsAscii, query, options.Mode) >= 0;
    }

    /// <summary>Returns the rank tier for a name, or -1 when it does not match.</summary>
    private static int Tier(
        ReadOnlySpan<byte> name, byte[] q, bool queryIsAscii, string query, MatchMode mode)
    {
        if (!queryIsAscii) return TierNonAscii(name, query, mode);

        int at = IndexOfFolded(name, q);
        if (at < 0) return -1;

        if (at == 0) return name.Length == q.Length ? TierExact : TierPrefix;
        if (mode == MatchMode.StartsWith) return -1;
        return IsWordBoundary(name, at) ? TierWordStart : TierContains;
    }

    private static int TierNonAscii(ReadOnlySpan<byte> name, string query, MatchMode mode)
    {
        string s = Encoding.UTF8.GetString(name);
        int at = s.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        if (at == 0) return s.Length == query.Length ? TierExact : TierPrefix;
        if (mode == MatchMode.StartsWith) return -1;
        char prev = s[at - 1];
        return prev is ' ' or '-' or '_' or '.' or '(' or '[' or '\'' ? TierWordStart : TierContains;
    }

    /// <summary>
    /// Case-insensitive substring search over UTF-8 bytes. Safe for non-ASCII names:
    /// UTF-8 continuation bytes are always >= 0x80, so they can never be mistaken for
    /// an ASCII query byte.
    /// </summary>
    private static int IndexOfFolded(ReadOnlySpan<byte> haystack, byte[] needle)
    {
        int nlen = needle.Length;
        if (nlen == 0) return 0;
        if (haystack.Length < nlen) return -1;

        byte f0 = needle[0];                       // already folded to lower case
        byte u0 = (byte)(f0 >= 'a' && f0 <= 'z' ? f0 - 32 : f0);

        int pos = 0;
        while (true)
        {
            var window = haystack[pos..];
            int limit = window.Length - nlen;
            if (limit < 0) return -1;

            // Vectorised jump to the next possible start byte.
            int rel = f0 == u0
                ? window.IndexOf(f0)
                : window.IndexOfAny(f0, u0);
            if (rel < 0 || rel > limit) return -1;

            int start = pos + rel;
            int k = 1;
            while (k < nlen && Fold[haystack[start + k]] == needle[k]) k++;
            if (k == nlen) return start;

            pos = start + 1;
        }
    }

    private static bool IsWordBoundary(ReadOnlySpan<byte> name, int at)
    {
        byte prev = name[at - 1];
        if (prev is (byte)' ' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'('
                 or (byte)'[' or (byte)'\'' or (byte)',' or (byte)'+' or (byte)'&') return true;
        // camelCase / digit boundaries, e.g. "MyMinecraft" matching "minecraft".
        byte cur = name[at];
        bool prevLower = prev is >= (byte)'a' and <= (byte)'z';
        bool curUpper = cur is >= (byte)'A' and <= (byte)'Z';
        if (prevLower && curUpper) return true;
        bool prevDigit = prev is >= (byte)'0' and <= (byte)'9';
        bool curAlpha = (cur | 0x20) is >= (byte)'a' and <= (byte)'z';
        return prevDigit && curAlpha;
    }

    // -------------------------------------------------------- path filtering

    private int FilterByPath(int[] matches, int count, string pathQuery)
    {
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            int i = matches[k];
            string dir = _index.GetDirectoryPath(i);
            if (dir.Contains(pathQuery, StringComparison.OrdinalIgnoreCase)) matches[n++] = i;
        }
        _lastCount = n;
        return n;
    }

    // ------------------------------------------------------------- ordering

    private int[] SelectPage(int[] matches, int count, string query, int offset, int limit)
    {
        if (count == 0 || offset >= count) return [];

        int need = Math.Min(count, offset + limit);

        // Scrolling asks for page after page of the same result set; ordering it once
        // and reusing it keeps paging free.
        string cacheKey = $"{_lastVersion}{_lastOptions}{_lastPathQuery}{query}";
        if (_sortedKey == cacheKey && _sortedNeed >= need)
            return Page(_sorted, offset, limit, need, query.Length > 0);

        var qBytes = ToFoldedBytes(query, out bool queryIsAscii);

        // Coarse key: tier, then name length, then the leading folded bytes. Ordering
        // by this key is a prefix of the exact ordering below, so taking the top `need`
        // by key never drops something the exact sort would rank higher (ties can only
        // occur inside an identical tier/length/prefix group).
        //
        // With no query there is no "best match" to rank by, so drop the length term
        // and order plain alphabetically instead of floating one-character names up.
        bool byLength = qBytes.Length > 0;

        var keys = new ulong[count];
        for (int k = 0; k < count; k++)
        {
            int i = matches[k];
            var name = _index.NamePool.AsSpan(_index.NameOffsets[i], _index.NameLengths[i]);
            int tier = byLength
                ? Math.Max(0, Tier(name, qBytes, queryIsAscii, query, MatchMode.Smart))
                : TierPrefix;
            keys[k] = PackKey(tier, name, byLength);
        }

        int[] selected;
        int ordered;
        if (count <= 100_000)
        {
            var idx = new int[count];
            Array.Copy(matches, idx, count);
            Array.Sort(keys, idx);
            selected = idx;
            ordered = count;
        }
        else
        {
            // Selecting the top `need` beats sorting a multi-million entry set, and
            // grabbing a generous slab means the next few scroll pages are already there.
            int slab = Math.Min(count, Math.Max(need, 4096));
            selected = TopK(keys, matches, count, slab);
            ordered = selected.Length;
        }

        _sorted = selected;
        _sortedNeed = ordered;
        _sortedKey = cacheKey;

        return Page(selected, offset, limit, need, byLength);
    }

    private int[] Page(int[] selected, int offset, int limit, int need, bool byLength)
    {
        int take = Math.Min(limit, Math.Min(need, selected.Length) - offset);
        if (take <= 0) return [];

        var page = new int[take];
        Array.Copy(selected, offset, page, 0, take);

        // Exact ordering for just the rows about to be displayed.
        Array.Sort(page, ExactComparer(byLength));
        return page;
    }

    private ulong PackKey(int tier, ReadOnlySpan<byte> name, bool byLength)
    {
        ulong key = (ulong)(tier & 0x3) << 62;
        if (byLength) key |= (ulong)Math.Min(name.Length, 0x3FFF) << 48;

        // Room for six folded characters when length is part of the key, eight when
        // it is not, which is enough to order alphabetically before the exact pass.
        int chars = byLength ? 6 : 7;
        ulong head = 0;
        for (int b = 0; b < chars; b++)
        {
            byte c = b < name.Length ? Fold[name[b]] : (byte)0;
            head = (head << 8) | c;
        }
        return key | head;
    }

    private Comparison<int> ExactComparer(bool byLength) => (a, b) =>
    {
        var na = _index.NamePool.AsSpan(_index.NameOffsets[a], _index.NameLengths[a]);
        var nb = _index.NamePool.AsSpan(_index.NameOffsets[b], _index.NameLengths[b]);

        if (byLength && na.Length != nb.Length) return na.Length - nb.Length;

        int len = Math.Min(na.Length, nb.Length);
        for (int k = 0; k < len; k++)
        {
            int d = Fold[na[k]] - Fold[nb[k]];
            if (d != 0) return d;
        }
        if (na.Length != nb.Length) return na.Length - nb.Length;
        return a - b;
    };

    /// <summary>Selects the <paramref name="k"/> smallest keys with a bounded max-heap.</summary>
    private static int[] TopK(ulong[] keys, int[] values, int count, int k)
    {
        k = Math.Min(k, count);
        var hKeys = new ulong[k];
        var hVals = new int[k];
        int size = 0;

        for (int i = 0; i < count; i++)
        {
            ulong key = keys[i];
            if (size < k)
            {
                hKeys[size] = key; hVals[size] = values[i];
                SiftUp(hKeys, hVals, size++);
            }
            else if (key < hKeys[0])
            {
                hKeys[0] = key; hVals[0] = values[i];
                SiftDown(hKeys, hVals, size, 0);
            }
        }

        Array.Sort(hKeys, hVals, 0, size);
        if (size != hVals.Length) Array.Resize(ref hVals, size);
        return hVals;
    }

    private static void SiftUp(ulong[] keys, int[] vals, int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (keys[parent] >= keys[i]) break;
            (keys[parent], keys[i]) = (keys[i], keys[parent]);
            (vals[parent], vals[i]) = (vals[i], vals[parent]);
            i = parent;
        }
    }

    private static void SiftDown(ulong[] keys, int[] vals, int size, int i)
    {
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, big = i;
            if (l < size && keys[l] > keys[big]) big = l;
            if (r < size && keys[r] > keys[big]) big = r;
            if (big == i) break;
            (keys[big], keys[i]) = (keys[i], keys[big]);
            (vals[big], vals[i]) = (vals[i], vals[big]);
            i = big;
        }
    }

    // --------------------------------------------------------------- helpers

    private static byte[] ToFoldedBytes(string query, out bool isAscii)
    {
        isAscii = true;
        foreach (char c in query)
        {
            if (c > 0x7F) { isAscii = false; break; }
        }
        if (!isAscii) return Encoding.UTF8.GetBytes(query);

        var bytes = new byte[query.Length];
        for (int i = 0; i < query.Length; i++) bytes[i] = Fold[(byte)query[i]];
        return bytes;
    }

    private static byte[] BuildFoldTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
            t[i] = (byte)(i >= 'A' && i <= 'Z' ? i + 32 : i);
        return t;
    }
}
