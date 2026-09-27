using System.Numerics;
using System.Runtime.InteropServices;

namespace TotkCave.Models;

public sealed class CrBin
{
    public static readonly byte[] MagicBytes = "cave017\0"u8.ToArray();

    public required string Path { get; init; }
    public required uint CaveId { get; init; }
    public required float[,] Transform { get; init; } = new float[3, 4];
    public required Vector3 BasePos { get; init; }
    public required float MinSidelength { get; init; }
    public required int NumSubdivisions { get; init; }
    public required (Vector3 Min, Vector3 Max) Aabb { get; init; }
    public required Vector4 Bounding { get; init; }
    public List<CrBinNode> Nodes { get; } = [];
    public List<CrBinStream> Streams { get; } = [];
    public List<CrBinMaterial> Materials { get; } = [];
    public List<CrBinPageFile> PageFiles { get; } = [];

    public string ChunkDirName => $"{System.IO.Path.GetFileName(Path)}.{CaveId:x8}";
    public string ChunkDirPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", ChunkDirName);

    public static CrBin FromFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return FromBytes(data, path);
    }

    /// <summary>
    /// Reads a crbin through a mod: the bytes from wherever <paramref name="resolve"/> sends the
    /// path, while <see cref="Path"/> - which the pages are found beside - stays the dump's.
    /// </summary>
    public static CrBin FromFile(string path, Func<string, string>? resolve) =>
        FromBytes(File.ReadAllBytes(resolve?.Invoke(path) ?? path), path);

    /// <summary>
    /// A crbin's bytes with material entries appended to its table. The table is the last but
    /// one block of the file - only the page file table follows it - so the entries go in
    /// before that, and its offset moves along.
    /// </summary>
    public static byte[] AppendMaterials(ReadOnlySpan<byte> crbin, IReadOnlyList<CrBinMaterial> added)
    {
        if (added.Count == 0) return crbin.ToArray();

        uint hOff = MemoryMarshal.Read<uint>(crbin[0xA0..]);
        uint hCnt = MemoryMarshal.Read<uint>(crbin[0xA4..]);
        uint iOff = MemoryMarshal.Read<uint>(crbin[0xA8..]);
        uint at = hOff + hCnt * 32;
        if (at != iOff)
            throw new InvalidDataException("The material table is not followed directly by the page file table.");

        byte[] result = new byte[crbin.Length + added.Count * 32];
        crbin[..(int)at].CopyTo(result);
        for (int i = 0; i < added.Count; i++)
        {
            Span<byte> e = result.AsSpan((int)at + i * 32, 32);
            CrBinMaterial m = added[i];
            MemoryMarshal.Write(e, m.UBias.X); MemoryMarshal.Write(e[4..], m.UBias.Y); MemoryMarshal.Write(e[8..], m.UBias.Z);
            MemoryMarshal.Write(e[12..], m.ArrayLayer);
            MemoryMarshal.Write(e[16..], m.VBias.X); MemoryMarshal.Write(e[20..], m.VBias.Y); MemoryMarshal.Write(e[24..], m.VBias.Z);
            MemoryMarshal.Write(e[28..], m.UvScale);
        }
        crbin[(int)at..].CopyTo(result.AsSpan((int)at + added.Count * 32));

        MemoryMarshal.Write(result.AsSpan(0xA4), hCnt + (uint)added.Count);
        MemoryMarshal.Write(result.AsSpan(0xA8), iOff + (uint)added.Count * 32);
        return result;
    }

    public static CrBin FromBytes(ReadOnlySpan<byte> d, string path = "")
    {
        if (d.Length < 0x1DC || !d[..8].SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Invalid C.crbin magic header (expected 'cave017\\0').");
        }

        uint caveId = MemoryMarshal.Read<uint>(d[0x10..]);

        float[,] transform = new float[3, 4];
        int transOff = 0x14;
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                transform[r, c] = MemoryMarshal.Read<float>(d[(transOff + (r * 4 + c) * 4)..]);
            }
        }

        Vector3 basePos = new(
            MemoryMarshal.Read<float>(d[0xB8..]),
            MemoryMarshal.Read<float>(d[0xBC..]),
            MemoryMarshal.Read<float>(d[0xC0..])
        );

        float minSidelength = MemoryMarshal.Read<float>(d[0xC4..]);
        int numSubdivisions = (int)MemoryMarshal.Read<uint>(d[0xC8..]);

        Vector3 aabbMin = new(
            MemoryMarshal.Read<float>(d[0xCC..]),
            MemoryMarshal.Read<float>(d[0xD0..]),
            MemoryMarshal.Read<float>(d[0xD4..])
        );
        Vector3 aabbMax = new(
            MemoryMarshal.Read<float>(d[0xD8..]),
            MemoryMarshal.Read<float>(d[0xDC..]),
            MemoryMarshal.Read<float>(d[0xE0..])
        );

        Vector4 bounding = new(basePos, minSidelength);

        CrBin crbin = new()
        {
            Path = path,
            CaveId = caveId,
            Transform = transform,
            BasePos = basePos,
            MinSidelength = minSidelength,
            NumSubdivisions = numSubdivisions,
            Aabb = (aabbMin, aabbMax),
            Bounding = bounding
        };

        (uint nOff, uint nCnt) = (MemoryMarshal.Read<uint>(d[0x68..]), MemoryMarshal.Read<uint>(d[0x6C..]));
        uint cOff = MemoryMarshal.Read<uint>(d[0x70..]);
        uint dOff = MemoryMarshal.Read<uint>(d[0x80..]);
        uint eOff = MemoryMarshal.Read<uint>(d[0x88..]);
        uint fOff = MemoryMarshal.Read<uint>(d[0x90..]);
        (uint gOff, uint gCnt) = (MemoryMarshal.Read<uint>(d[0x98..]), MemoryMarshal.Read<uint>(d[0x9C..]));
        (uint hOff, uint hCnt) = (MemoryMarshal.Read<uint>(d[0xA0..]), MemoryMarshal.Read<uint>(d[0xA4..]));
        (uint iOff, uint iCnt) = (MemoryMarshal.Read<uint>(d[0xA8..]), MemoryMarshal.Read<uint>(d[0xAC..]));

        for (int i = 0; i < nCnt; i++)
        {
            int o = (int)(nOff + i * 8);
            int eo = (int)(eOff + i * 16);

            ushort cellX = MemoryMarshal.Read<ushort>(d[o..]);
            ushort cellY = MemoryMarshal.Read<ushort>(d[(o + 2)..]);
            ushort cellZ = MemoryMarshal.Read<ushort>(d[(o + 4)..]);
            ushort lod = MemoryMarshal.Read<ushort>(d[(o + 6)..]);

            int co = (int)(cOff + i * 8);
            uint childFirst = MemoryMarshal.Read<uint>(d[co..]);
            uint childEnd = MemoryMarshal.Read<uint>(d[(co + 4)..]);

            int doff = (int)(dOff + i * 24);
            float minX = MemoryMarshal.Read<float>(d[doff..]);
            float minY = MemoryMarshal.Read<float>(d[(doff + 4)..]);
            float minZ = MemoryMarshal.Read<float>(d[(doff + 8)..]);
            float maxX = MemoryMarshal.Read<float>(d[(doff + 12)..]);
            float maxY = MemoryMarshal.Read<float>(d[(doff + 16)..]);
            float maxZ = MemoryMarshal.Read<float>(d[(doff + 20)..]);

            uint connMask = MemoryMarshal.Read<uint>(d[(int)(fOff + i * 4)..]);
            uint baseStream = MemoryMarshal.Read<uint>(d[eo..]);
            ushort baseMat = MemoryMarshal.Read<ushort>(d[(eo + 4)..]);
            byte streamCnt = d[eo + 6];

            crbin.Nodes.Add(new CrBinNode(
                (cellX, cellY, cellZ),
                lod,
                (childFirst, childEnd),
                (minX, minY, minZ, maxX, maxY, maxZ),
                connMask,
                baseStream,
                baseMat,
                streamCnt
            ));
        }

        for (int i = 0; i < gCnt; i++)
        {
            int o = (int)(gOff + i * 12);
            crbin.Streams.Add(new CrBinStream(
                MemoryMarshal.Read<uint>(d[o..]),
                MemoryMarshal.Read<uint>(d[(o + 4)..]),
                MemoryMarshal.Read<ushort>(d[(o + 8)..]),
                MemoryMarshal.Read<ushort>(d[(o + 10)..])
            ));
        }

        for (int i = 0; i < hCnt; i++)
        {
            int o = (int)(hOff + i * 32);
            Vector3 uBias = new(
                MemoryMarshal.Read<float>(d[o..]),
                MemoryMarshal.Read<float>(d[(o + 4)..]),
                MemoryMarshal.Read<float>(d[(o + 8)..])
            );
            float layer = MemoryMarshal.Read<float>(d[(o + 12)..]);
            Vector3 vBias = new(
                MemoryMarshal.Read<float>(d[(o + 16)..]),
                MemoryMarshal.Read<float>(d[(o + 20)..]),
                MemoryMarshal.Read<float>(d[(o + 24)..])
            );
            float scale = MemoryMarshal.Read<float>(d[(o + 28)..]);

            crbin.Materials.Add(new CrBinMaterial(uBias, layer, vBias, scale));
        }

        for (int i = 0; i < iCnt; i++)
        {
            int o = (int)(iOff + i * 8);
            crbin.PageFiles.Add(new CrBinPageFile(
                MemoryMarshal.Read<ushort>(d[o..]),
                MemoryMarshal.Read<ushort>(d[(o + 2)..]),
                MemoryMarshal.Read<ushort>(d[(o + 4)..])
            ));
        }

        return crbin;
    }
}
