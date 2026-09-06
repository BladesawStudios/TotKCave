using System.Numerics;
using System.Runtime.InteropServices;

namespace TotkCave.Decoding;

public readonly record struct DecodedBlock(
    (int Qx, int Qy, int Qz) QPos,
    (int W1, int W2) Weights,
    (int Nu, int Nv) NormalRaw,
    Vector3 ColorRgb,
    int Low6
);

public readonly record struct DecodedVertex(
    int[] Materials,
    int PatchFlags,
    DecodedBlock Parent,
    DecodedBlock Self
);

public static class VertexDecoder
{
    public const int VertexStride = 28;

    public static DecodedBlock DecodeBlock(ReadOnlySpan<byte> blockBytes)
    {
        ulong low64 = MemoryMarshal.Read<ulong>(blockBytes);
        uint high32 = MemoryMarshal.Read<uint>(blockBytes[8..]);

        int qx = (int)((low64 >> 6) & 0x1FFF);
        int qy = (int)((low64 >> 19) & 0x1FFF);
        int qz = (int)((low64 >> 32) & 0x1FFF);
        int w1 = (int)((low64 >> 45) & 0x1F);
        int w2 = (int)((low64 >> 50) & 0x1F);

        int nu = (int)(high32 & 0x7F);
        int nv = (int)((high32 >> 7) & 0x7F);
        int r = (int)((high32 >> 14) & 0x3F);
        int g = (int)((high32 >> 20) & 0x3F);
        int b = (int)((high32 >> 26) & 0x3F);

        // 1/64, not 1/63: the game scales these by 0.015625, which puts the stored
        // value 32 at exactly 0.5. That is the neutral point of the vertex grade and the
        // single most common value in the data, so 1/63 would tint the untinted case.
        Vector3 color = new(r / 64.0f, g / 64.0f, b / 64.0f);
        int low6 = (int)(low64 & 0x3F);

        return new DecodedBlock(
            (qx, qy, qz),
            (w1, w2),
            (nu, nv),
            color,
            low6
        );
    }

    public static DecodedVertex DecodeVertex(ReadOnlySpan<byte> page, int vertexIndex)
    {
        int o = vertexIndex * VertexStride;
        if (o + VertexStride > page.Length)
            throw new ArgumentOutOfRangeException(nameof(vertexIndex), "Vertex index exceeds page buffer boundary.");

        uint patch = MemoryMarshal.Read<uint>(page[o..]);
        int[] mats = [
            (int)((patch >> 5) & 0x1FF),
            (int)((patch >> 14) & 0x1FF),
            (int)((patch >> 23) & 0x1FF)
        ];
        int patchFlags = (int)(patch & 0x1F);

        DecodedBlock parent = DecodeBlock(page[(o + 4)..(o + 16)]);
        DecodedBlock selfBlock = DecodeBlock(page[(o + 16)..(o + 28)]);

        return new DecodedVertex(mats, patchFlags, parent, selfBlock);
    }
}
