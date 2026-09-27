using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using TotkCave.Decoding;
using TotkCave.Models;
using TotkCave.PageSource;

namespace TotkCave.Building;

/// <summary>
/// A patch of a cave-format mesh - a cave or a sky island - as it sits in its page: the vertices
/// that share one set of three material ids, and where each lands in the world.
/// </summary>
/// <remarks>
/// A vertex names its three materials itself, but every triangle's three vertices name the same
/// ones - on every island and cave looked at, over a million triangles - so the ids belong in
/// effect to the connected run of triangles, and a patch is that run. Painting one of its
/// materials in or out changes all its vertices at once, or triangles would straddle two sets.
/// </remarks>
public sealed class CavePatch
{
    public required int Node { get; init; }
    public required int Lod { get; init; }
    public required int Page { get; init; }

    /// <summary>What the vertices' 9-bit ids are counted from in the crbin's material table.</summary>
    public required int BaseMaterial { get; init; }

    /// <summary>The vertices' indices in the page, ascending.</summary>
    public required int[] Vertices { get; init; }

    /// <summary>Each vertex's position, exactly as the mesh builder places it.</summary>
    public required Vector3[] Positions { get; init; }

    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    /// <summary>
    /// The way the patch faces: its vertices' normals, averaged. A cave's table holds each
    /// material once per projection - onto the ground plane and onto the two upright ones - and
    /// a patch is drawn in the one it faces, so this is how a material painted in is projected.
    /// </summary>
    public required Vector3 Normal { get; init; }

    public long Key => ((long)Page << 32) | (uint)Vertices[0];
}

public static class CaveSites
{
    /// <summary>A vertex is 28 bytes: the material word, then its parent's block and its own.</summary>
    public const int VertexStride = 28;

    /// <summary>Every patch a node draws, placed at the node's own level of detail.</summary>
    public static List<CavePatch> Of(CrBin crbin, IPageSource pages, int nodeIndex)
    {
        CrBinNode node = crbin.Nodes[nodeIndex];

        // The mesh builder's arithmetic, so a vertex lands on the same float it does.
        float sl = crbin.MinSidelength * MathF.Pow(2.0f, crbin.NumSubdivisions - node.Lod);
        float scale = sl / 4096.0f;
        Vector3 origin = crbin.BasePos;
        Vector3 nodeBase = new(
            origin.X - sl * 0.49993896f + node.Cell.X * sl,
            origin.Y - sl * 0.49993896f + node.Cell.Y * sl,
            origin.Z - sl * 0.49993896f + node.Cell.Z * sl
        );

        // Union-find over the vertices of each page the node's streams use.
        Dictionary<(int Page, int Vertex), (int Page, int Vertex)> parent = [];
        (int, int) Find((int, int) v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }
            return v;
        }
        void Add((int, int) v) => parent.TryAdd(v, v);
        void Union((int, int) a, (int, int) b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (!ra.Equals(rb)) parent[rb] = ra;
        }

        int end = (int)(node.BaseStream + node.StreamCount);
        for (int s = (int)node.BaseStream; s < end; s++)
        {
            CrBinStream stream = crbin.Streams[s];
            byte[] page = pages.GetPage(stream.PageFile);
            ReadOnlySpan<ushort> indices = MemoryMarshal.Cast<byte, ushort>(
                page.AsSpan((int)(stream.BaseIndex * 2), (int)stream.Triangles * 6));

            for (int t = 0; t < stream.Triangles; t++)
            {
                (int, int) a = (stream.PageFile, indices[t * 3]);
                (int, int) b = (stream.PageFile, indices[t * 3 + 1]);
                (int, int) c = (stream.PageFile, indices[t * 3 + 2]);
                Add(a); Add(b); Add(c);
                Union(a, b);
                Union(a, c);
            }
        }

        List<CavePatch> patches = [];
        foreach (var group in parent.Keys.GroupBy(Find))
        {
            int pageIndex = group.Key.Item1;
            byte[] page = pages.GetPage(pageIndex);
            int[] verts = [.. group.Select(v => v.Vertex).Order()];
            Vector3[] positions = new Vector3[verts.Length];
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            Vector3 normal = Vector3.Zero;

            for (int i = 0; i < verts.Length; i++)
            {
                ulong self = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(verts[i] * VertexStride + 16));
                int qx = (int)((self >> 6) & 0x1FFF);
                int qy = (int)((self >> 19) & 0x1FFF);
                int qz = (int)((self >> 32) & 0x1FFF);
                Vector3 p = nodeBase + new Vector3(qx * scale, qy * scale, qz * scale);
                positions[i] = p;

                uint high = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(verts[i] * VertexStride + 24));
                normal += OctNormalDecoder.Decode((int)(high & 0x7F), (int)((high >> 7) & 0x7F));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            patches.Add(new CavePatch
            {
                Node = nodeIndex,
                Lod = node.Lod,
                Page = pageIndex,
                BaseMaterial = node.BaseMaterial,
                Vertices = verts,
                Positions = positions,
                Min = min,
                Max = max,
                Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY,
            });
        }
        return patches;
    }

    /// <summary>The nodes, at one level of detail or all of them, whose bounds reach a box.</summary>
    public static IEnumerable<int> NodesIn(CrBin crbin, Vector3 min, Vector3 max, int? lod = null)
    {
        for (int i = 0; i < crbin.Nodes.Count; i++)
        {
            CrBinNode n = crbin.Nodes[i];
            if (lod is { } l && n.Lod != l) continue;
            var b = n.Aabb;
            if (b.MaxX < min.X || b.MinX > max.X || b.MaxY < min.Y || b.MinY > max.Y || b.MaxZ < min.Z || b.MinZ > max.Z) continue;
            yield return i;
        }
    }
}
