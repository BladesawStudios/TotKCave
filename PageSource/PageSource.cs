using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using McSharp;
using TotkCave.Models;

namespace TotkCave.PageSource;

public sealed class CavePageSource : IPageSource
{
    private readonly CrBin _crbin;
    private readonly string? _pagesDir;
    private readonly ConcurrentDictionary<int, byte[]> _cache = new();
    private readonly Dictionary<int, ushort> _blocks = [];

    /// <summary>
    /// MeshCodec scratch, one per thread and grown to the largest page seen. The mesh builder
    /// asks for pages from every core at once, and a romfs page wants about 2 MB of it.
    /// </summary>
    [ThreadStatic] private static byte[]? _work;

    public string SourceKind { get; private set; } = "unknown";

    public CavePageSource(CrBin crbin, string? caveDir = null, string? pagesDir = null)
    {
        _crbin = crbin;
        _pagesDir = pagesDir;

        foreach (CrBinPageFile pf in crbin.PageFiles)
        {
            _blocks[pf.PageFileIndex] = pf.BlockCount;
        }
    }

    public int GetExpectedSize(int fid)
    {
        ushort blocks = _blocks.GetValueOrDefault(fid, (ushort)0);
        return 0x10000 * blocks;
    }

    public bool IsDecompressedPage(string path, int fid)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists) return false;

            int exp = GetExpectedSize(fid);
            if (exp > 0 && info.Length != exp) return false;

            using FileStream stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            if (stream.Read(head) < 4) return false;

            uint chunkHash = MemoryMarshal.Read<uint>(head);
            if (chunkHash == _crbin.CaveId) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public byte[] GetPage(int fid)
    {
        if (_cache.TryGetValue(fid, out byte[]? cached))
            return cached;

        string chunkDir = _crbin.ChunkDirPath;
        string chunkFile = Path.Combine(chunkDir, $"{fid:D6}.chunk");

        if (File.Exists(chunkFile) && IsDecompressedPage(chunkFile, fid))
        {
            return Store(fid, File.ReadAllBytes(chunkFile), "console");
        }

        if (!string.IsNullOrEmpty(_pagesDir))
        {
            string cand1 = Path.Combine(_pagesDir, $"{fid:D6}");
            string cand2 = Path.Combine(_pagesDir, $"{fid:D6}.chunk");

            if (File.Exists(cand1)) return Store(fid, File.ReadAllBytes(cand1), "pages");
            if (File.Exists(cand2)) return Store(fid, File.ReadAllBytes(cand2), "pages");
        }

        if (File.Exists(chunkFile))
        {
            return Store(fid, Decompress(chunkFile), "meshcodec");
        }

        throw new FileNotFoundException($"No page available for chunk {fid:D6}: {chunkFile} is missing.");
    }

    /// <summary>
    /// Decodes a romfs page in process. Byte-identical to the MeshCodec CLI's output across
    /// every cave page, which is what this replaced.
    /// </summary>
    private static byte[] Decompress(string chunkFile)
    {
        byte[] src = File.ReadAllBytes(chunkFile);
        if (!MeshCodec.TryReadChunkHeader(src, out ResChunkHeader header))
            throw new InvalidDataException($"Not a MeshCodec chunk: {chunkFile}");

        if (_work is null || _work.Length < header.WorkMemSize)
            _work = new byte[header.WorkMemSize];

        byte[] dst = new byte[header.DecompressedSize];
        if (!MeshCodec.DecompressChunk(dst, src, _work))
            throw new InvalidDataException($"MeshCodec could not decode {chunkFile}");

        return dst;
    }

    private byte[] Store(int fid, byte[] data, string kind)
    {
        _cache[fid] = data;
        if (SourceKind == "unknown")
            SourceKind = kind;
        return data;
    }
}
