using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TotkCave.Models;

namespace TotkCave.PageSource;

public sealed class QuadPageSource : IPageSource
{
    private readonly QuadResource _resource;
    private readonly string? _pagesDir;
    private readonly Func<string, string>? _resolve;
    private readonly ConcurrentDictionary<int, byte[]> _cache = new();

    public string SourceKind { get; private set; } = "quad";

    /// <param name="resolve">
    /// Maps a page's path in the dump to the file to read in its place - a mod's copy, say.
    /// </param>
    public QuadPageSource(QuadResource resource, string? pagesDir = null, Func<string, string>? resolve = null)
    {
        _resource = resource;
        _pagesDir = pagesDir;
        _resolve = resolve;
    }

    /// <summary>Where a page file lives beside the resource in the dump, whether or not it is there.</summary>
    public static string PathOf(QuadResource resource, int fid)
    {
        (_, uint pageId) = resource.GetPageFile(fid);
        return Path.Combine(resource.Path + $".{resource.Id:x8}", $"{pageId:D6}.quad");
    }

    /// <summary>
    /// A page file's bytes decompressed: as they are when a console dump left them so, or
    /// after the resource's id and a plain zstd frame.
    /// </summary>
    public static byte[] Unpack(QuadResource resource, int fid, byte[] raw)
    {
        (uint decompressedSize, _) = resource.GetPageFile(fid);
        if (raw.Length == decompressedSize) return raw;
        if (raw.Length > 4 && MemoryMarshal.Read<uint>(raw) == resource.Id)
            return DecompressZstdFrame(raw.AsSpan(4), (int)decompressedSize);
        return raw;
    }

    public byte[] GetPage(int fid)
    {
        if (_cache.TryGetValue(fid, out byte[]? cached))
            return cached;

        (uint decompressedSize, uint pageId) = _resource.GetPageFile(fid);

        string pageDir = _resource.Path + $".{_resource.Id:x8}";
        string cand1 = Path.Combine(pageDir, $"{pageId:D6}.quad");
        string cand2 = Path.Combine(pageDir, $"{pageId:D6}");
        if (!string.IsNullOrEmpty(_pagesDir))
        {
            string cand3 = Path.Combine(_pagesDir, $"{pageId:D6}.quad");
            string cand4 = Path.Combine(_pagesDir, $"{pageId:D6}");
            if (File.Exists(cand3)) cand1 = cand3;
            else if (File.Exists(cand4)) cand1 = cand4;
        }

        if (!File.Exists(cand1) && File.Exists(cand2))
            cand1 = cand2;

        if (_resolve is not null) cand1 = _resolve(cand1);

        if (!File.Exists(cand1))
            throw new FileNotFoundException($"Quad page file missing for page {pageId} (index {fid}): {cand1}");

        byte[] raw = File.ReadAllBytes(cand1);
        byte[] page;

        if (raw.Length == decompressedSize)
        {
            page = raw;                                     // console dump: already decompressed
            SourceKind = "quad-console";
        }
        else if (raw.Length > 4 && MemoryMarshal.Read<uint>(raw) == _resource.Id)
        {
            page = DecompressZstdFrame(raw.AsSpan(4), (int)decompressedSize);
            SourceKind = "quad-romfs-zstd";
        }
        else
        {
            page = raw;
        }

        // Another thread may have produced the same page concurrently; keep whichever
        // landed first so callers always see one shared buffer per page.
        return _cache.GetOrAdd(fid, page);
    }

    private static byte[] DecompressZstdFrame(ReadOnlySpan<byte> compressedFrame, int expectedSize)
    {
        byte[] output = new byte[expectedSize];
        using ZstdSharp.Decompressor decompressor = new();

        int written = decompressor.Unwrap(compressedFrame, output);
        if (written != expectedSize)
        {
            throw new InvalidDataException(
                $"zstd page decompressed to {written} bytes, expected {expectedSize}.");
        }

        return output;
    }
}
