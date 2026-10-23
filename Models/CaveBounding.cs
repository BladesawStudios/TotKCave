using System.Buffers.Binary;
using System.Numerics;

namespace TotkCave.Models;

/// <summary>
/// The space inside one cave or well, as the game keeps it in <c>CaveBounding/MainField/&lt;hash&gt;.vsts.zs</c>
/// (format <c>CSTS</c>, version 3): boxes on a grid that together fill the cave, and oriented boxes for what the grid misses.
/// </summary>
/// <remarks>
/// <para>
/// After the magic come a big-endian version and a little-endian count of boxes, then nine floats a box: its full size, a
/// rotation and its centre, in world space. The boxes stand on a 10 m grid in x and z, 10.5 m wide so that neighbours overlap,
/// and each is as tall as the cave is there. Not one of the game's 20,975 boxes is rotated, so a file with a rotated box is
/// refused rather than read wrongly.
/// </para>
/// <para>
/// Four lists of oriented boxes follow, each a count and that many 4x4 matrices that take the unit cube centred on the origin to
/// a box in world space, stored by rows with the translation in the last column. What the lists mean is not known; by where
/// they stand, the first's boxes are at the mouths, the second's round chambers the grid leaves out, and the fourth's up
/// shafts, like the 330 m one from Cave_Akkala_0011's floor to the surface that its grid does not reach. Of the actors the
/// caves' own placement files stand in or beside their meshes, 98% are within 2 m of the grid and the lists together, against
/// 94% for the grid alone.
/// </para>
/// <para>
/// A split cave, like Cave_CentralHyrule_0018_A and _B, has a file for each half, and wells have files too. The file names are
/// 64-bit hashes that do not come from the caves' names, so a caller matches a file to its cave by where its boxes are.
/// </para>
/// </remarks>
public sealed class CaveBounding
{
    private const int Version = 3;
    private const int HeaderBytes = 12;
    private const int BoxBytes = 36;
    private const int MatrixBytes = 64;
    private const int ListCount = 4;

    private readonly CaveBox[] _boxes;
    private readonly CaveOrientedBox[] _oriented;

    private CaveBounding(CaveBox[] boxes, CaveOrientedBox[] oriented)
    {
        _boxes = boxes;
        _oriented = oriented;
        Min = new Vector3(float.MaxValue);
        Max = new Vector3(float.MinValue);
        foreach (CaveBox box in boxes)
        {
            Min = Vector3.Min(Min, box.Centre - box.Size * 0.5f);
            Max = Vector3.Max(Max, box.Centre + box.Size * 0.5f);
        }
        foreach (CaveOrientedBox box in oriented)
        {
            Min = Vector3.Min(Min, box.Centre - box.Reach);
            Max = Vector3.Max(Max, box.Centre + box.Reach);
        }
    }

    /// <summary>The boxes on the grid that fill the cave.</summary>
    public IReadOnlyList<CaveBox> Boxes => _boxes;

    /// <summary>The oriented boxes of the four lists after the grid's, in file order.</summary>
    public IReadOnlyList<CaveOrientedBox> OrientedBoxes => _oriented;

    /// <summary>The corner of the box round every box, or float.MaxValue for a file with none.</summary>
    public Vector3 Min { get; }

    /// <summary>The far corner of the box round every box, or float.MinValue for a file with none.</summary>
    public Vector3 Max { get; }

    /// <summary>Reads a decompressed file. Throws <see cref="InvalidDataException"/> for anything that is not one.</summary>
    public static CaveBounding Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes || !data[..4].SequenceEqual("CSTS"u8))
            throw new InvalidDataException("Not a cave bounding file: no CSTS magic.");

        int version = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        if (version != Version) throw new InvalidDataException($"Cave bounding version {version}, not {Version}.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (count < 0 || HeaderBytes + (long)count * BoxBytes > data.Length)
            throw new InvalidDataException($"Cave bounding file of {data.Length} bytes cannot hold {count} boxes.");

        CaveBox[] boxes = new CaveBox[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> box = data.Slice(HeaderBytes + i * BoxBytes, BoxBytes);
            Vector3 rotation = Vec3(box[12..]);
            if (rotation != Vector3.Zero) throw new InvalidDataException($"Cave bounding box {i} is rotated, which no file of the game's is.");
            boxes[i] = new CaveBox(Vec3(box), Vec3(box[24..]));
        }

        List<CaveOrientedBox> oriented = [];
        int at = HeaderBytes + count * BoxBytes;
        for (int list = 0; list < ListCount; list++)
        {
            if (at + 4 > data.Length) throw new InvalidDataException($"Cave bounding file ends before oriented box list {list}.");
            int n = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
            at += 4;
            if (n < 0 || at + (long)n * MatrixBytes > data.Length)
                throw new InvalidDataException($"Cave bounding file of {data.Length} bytes cannot hold {n} oriented boxes in list {list}.");

            for (int i = 0; i < n; i++, at += MatrixBytes)
                oriented.Add(CaveOrientedBox.Read(list, data.Slice(at, MatrixBytes)));
        }
        if (at != data.Length) throw new InvalidDataException($"Cave bounding file has {data.Length - at} bytes after its last list.");

        return new CaveBounding(boxes, [.. oriented]);
    }

    /// <summary>Whether a point stands in one of the boxes, oriented or not, or within margin metres of one.</summary>
    public bool Contains(Vector3 point, float margin = 0f)
    {
        if (point.X < Min.X - margin || point.Y < Min.Y - margin || point.Z < Min.Z - margin
            || point.X > Max.X + margin || point.Y > Max.Y + margin || point.Z > Max.Z + margin)
            return false;

        foreach (CaveBox box in _boxes)
            if (box.Contains(point, margin)) return true;
        foreach (CaveOrientedBox box in _oriented)
            if (box.Contains(point, margin)) return true;
        return false;
    }

    internal static Vector3 Vec3(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data),
        BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(data[8..]));
}

/// <summary>One box of a <see cref="CaveBounding"/>'s grid: its full size and its centre, in world space.</summary>
public readonly record struct CaveBox(Vector3 Size, Vector3 Centre)
{
    /// <summary>Whether a point stands in the box, or within margin metres of it.</summary>
    public bool Contains(Vector3 point, float margin = 0f)
    {
        Vector3 reach = Size * 0.5f + new Vector3(margin);
        Vector3 off = Vector3.Abs(point - Centre);
        return off.X <= reach.X && off.Y <= reach.Y && off.Z <= reach.Z;
    }
}

/// <summary>
/// One oriented box of a <see cref="CaveBounding"/>: the list it is in, its centre, and its three edges as world-space vectors,
/// each as long as the box is along it.
/// </summary>
/// <remarks>
/// The edges are the columns of the file's matrix and stand at right angles, which a file is refused for breaking. A few boxes
/// are flat along an edge, which leaves them nothing inside, so <see cref="Contains"/> never holds a point for them.
/// </remarks>
public readonly record struct CaveOrientedBox(int List, Vector3 Centre, Vector3 EdgeX, Vector3 EdgeY, Vector3 EdgeZ)
{
    private const float Flat = 1e-3f;

    /// <summary>Half the size of the axis-aligned box round this one.</summary>
    public Vector3 Reach => (Vector3.Abs(EdgeX) + Vector3.Abs(EdgeY) + Vector3.Abs(EdgeZ)) * 0.5f;

    /// <summary>Whether a point stands in the box, or within margin metres of it.</summary>
    public bool Contains(Vector3 point, float margin = 0f)
    {
        Vector3 d = point - Centre;
        return Within(d, EdgeX, margin) && Within(d, EdgeY, margin) && Within(d, EdgeZ, margin);
    }

    internal static CaveOrientedBox Read(int list, ReadOnlySpan<byte> matrix)
    {
        Span<float> m = stackalloc float[16];
        for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadSingleLittleEndian(matrix[(i * 4)..]);
        if (m[12] != 0f || m[13] != 0f || m[14] != 0f || m[15] != 1f)
            throw new InvalidDataException("Cave bounding matrix does not end in 0 0 0 1.");

        Vector3 x = new(m[0], m[4], m[8]), y = new(m[1], m[5], m[9]), z = new(m[2], m[6], m[10]);
        if (!Square(x, y) || !Square(y, z) || !Square(x, z))
            throw new InvalidDataException("Cave bounding matrix is sheared, which no file of the game's is.");

        return new CaveOrientedBox(list, new Vector3(m[3], m[7], m[11]), x, y, z);
    }

    private static bool Within(Vector3 d, Vector3 edge, float margin)
    {
        float length = edge.Length();
        return length > Flat && MathF.Abs(Vector3.Dot(d, edge)) / length <= length * 0.5f + margin;
    }

    // Whether two edges stand at right angles, to within what float matrices keep; a flat edge is square to anything.
    private static bool Square(Vector3 a, Vector3 b)
    {
        float scale = a.Length() * b.Length();
        return scale <= Flat || MathF.Abs(Vector3.Dot(a, b)) <= 1e-3f * scale;
    }
}
