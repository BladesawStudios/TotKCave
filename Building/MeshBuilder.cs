using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using TotkCave.Decoding;
using TotkCave.Models;
using TotkCave.PageSource;

namespace TotkCave.Building;

public static class MeshBuilder
{
    public static CaveMesh BuildMesh(
        CrBin crbin,
        IPageSource pages,
        int? lod = null,
        bool weld = true,
        float clean = 0.0f,
        int maxDegreeOfParallelism = -1,
        Action<int, int>? progressCallback = null)
    {
        int targetLod = lod ?? crbin.NumSubdivisions;
        if (targetLod > crbin.NumSubdivisions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lod),
                $"Requested LOD {targetLod} exceeds CRBIN max subdivisions {crbin.NumSubdivisions}.");
        }

        float sl = crbin.MinSidelength * MathF.Pow(2.0f, crbin.NumSubdivisions - targetLod);
        float scale = sl / 4096.0f;

        if (clean > 0.0f)
        {
            clean *= (sl / crbin.MinSidelength);
        }

        float cleanSq = clean * clean;
        Vector3 origin = crbin.BasePos;

        CaveMesh mesh = new()
        {
            Materials = crbin.Materials
        };

        var matchingNodes = crbin.Nodes.Where(n => n.Lod == targetLod).ToList();
        int totalNodes = matchingNodes.Count;
        if (totalNodes == 0) return mesh;

        int threads = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = threads };

        var nodeResults = new NodeResult[totalNodes];
        int completedCount = 0;

        Parallel.For(0, totalNodes, parallelOptions, nodeIdx =>
        {
            CrBinNode node = matchingNodes[nodeIdx];
            int streamEnd = (int)(node.BaseStream + node.StreamCount);

            // Sized from the streams up front: growing these lists a vertex at a time was a
            // large part of a cave's decode, spent copying.
            int nodeTris = 0;
            for (int sIdx = (int)node.BaseStream; sIdx < streamEnd; sIdx++)
                nodeTris += (int)crbin.Streams[sIdx].Triangles;

            NodeResult res = new(nodeTris);

            // Typed rather than keyed on object: boxing every vertex's key was most of what a
            // decode allocated, and most of the collector's time with it.
            Dictionary<WeldKey, int>? weldMap = weld ? new(nodeTris / 2 + 16) : null;
            Dictionary<int, int>? pageMap = weld ? null : new(nodeTris / 2 + 16);

            Vector3 nodeBase = new(
                origin.X - sl * 0.49993896f + node.Cell.X * sl,
                origin.Y - sl * 0.49993896f + node.Cell.Y * sl,
                origin.Z - sl * 0.49993896f + node.Cell.Z * sl
            );

            StreamMap map = StreamMap.ForThread();

            for (int sIdx = (int)node.BaseStream; sIdx < streamEnd; sIdx++)
            {
                CrBinStream stream = crbin.Streams[sIdx];
                byte[] page = pages.GetPage(stream.PageFile);

                int triCount = (int)stream.Triangles;
                int indexOffset = (int)(stream.BaseIndex * 2);
                ReadOnlySpan<ushort> indices = MemoryMarshal.Cast<byte, ushort>(page.AsSpan(indexOffset, triCount * 3 * 2));

                // Which of this stream's vertices have been seen, by index: an array over the
                // whole index space, stamped per stream rather than cleared.
                int stamp = map.Next();

                foreach (ushort v in indices)
                {
                    if (map.Stamp[v] == stamp) continue;

                    int o = v * VertexDecoder.VertexStride;
                    if (o + VertexDecoder.VertexStride > page.Length)
                        throw new ArgumentOutOfRangeException(nameof(pages), "Vertex index exceeds page buffer boundary.");

                    // VertexDecoder.DecodeVertex, inline and without its per-vertex array.
                    uint patch = MemoryMarshal.Read<uint>(page.AsSpan(o));
                    int m0 = (int)((patch >> 5) & 0x1FF);
                    int m1 = (int)((patch >> 14) & 0x1FF);
                    int m2 = (int)((patch >> 23) & 0x1FF);

                    ulong low64 = MemoryMarshal.Read<ulong>(page.AsSpan(o + 16));
                    uint high32 = MemoryMarshal.Read<uint>(page.AsSpan(o + 24));
                    int qx = (int)((low64 >> 6) & 0x1FFF);
                    int qy = (int)((low64 >> 19) & 0x1FFF);
                    int qz = (int)((low64 >> 32) & 0x1FFF);
                    int w1 = (int)((low64 >> 45) & 0x1F);
                    int w2 = (int)((low64 >> 50) & 0x1F);

                    Vector3 worldPos = nodeBase + new Vector3(qx * scale, qy * scale, qz * scale);

                    // The game reads the two stored 5-bit weights into slots 0 and 1 and
                    // derives slot 2 as the clamped remainder:
                    //   w[0] = raw0/31, w[1] = raw1/31, w[2] = clamp(1 - w[0] - w[1], 0, 1)
                    // Putting the remainder in lane 0 instead rotates every weight one slot
                    // against the material ids, which are in the same order in both, so each
                    // vertex blended - and reported - the wrong dominant material. The clamp
                    // matters too: the stored pair may sum past 31, and the remainder is
                    // floored at zero rather than allowed to go negative.
                    float wa = w1 / 31.0f;
                    float wb = w2 / 31.0f;
                    float wc = Math.Clamp(1.0f - wa - wb, 0.0f, 1.0f);
                    int domSlot = GetDominantSlotIndex(wa, wb, wc);
                    int matIdx = node.BaseMaterial + (domSlot == 0 ? m0 : domSlot == 1 ? m1 : m2);

                    int s0 = node.BaseMaterial + m0, s1 = node.BaseMaterial + m1, s2 = node.BaseMaterial + m2;

                    // Welding by position alone merges vertices from different nodes that
                    // coincide in space, and the survivor keeps one slot set - handing a
                    // wrong material to every face that referenced the other. Node base
                    // materials differ, so this corrupted about half of all faces. The
                    // material slots are part of the identity of a vertex.
                    int localIdx = res.Verts.Count;
                    bool added = weld
                        ? weldMap!.TryAdd(new WeldKey(worldPos, s0, s1, s2), localIdx)
                        : pageMap!.TryAdd((stream.PageFile << 16) | v, localIdx);

                    if (!added)
                    {
                        localIdx = weld
                            ? weldMap![new WeldKey(worldPos, s0, s1, s2)]
                            : pageMap![(stream.PageFile << 16) | v];
                    }
                    else
                    {
                        int nu = (int)(high32 & 0x7F);
                        int nv = (int)((high32 >> 7) & 0x7F);
                        int r = (int)((high32 >> 14) & 0x3F);
                        int g = (int)((high32 >> 20) & 0x3F);
                        int b = (int)((high32 >> 26) & 0x3F);

                        res.Verts.Add(worldPos);
                        res.Norms.Add(OctNormalDecoder.Decode(nu, nv));

                        // 1/64, as VertexDecoder.DecodeBlock explains.
                        res.Cols.Add(new Vector3(r / 64.0f, g / 64.0f, b / 64.0f));

                        // Keep the whole blend, not just the dominant slot: every vertex
                        // carries three materials and about half of them are genuinely mixed.
                        res.Slots.Add((s0, s1, s2));
                        res.Weights.Add(new Vector3(wa, wb, wc));
                    }

                    map.Stamp[v] = stamp;
                    map.Local[v] = localIdx;
                    map.Material[v] = matIdx;
                }

                for (int t = 0; t < triCount; t++)
                {
                    ushort vA = indices[t * 3];
                    ushort vB = indices[t * 3 + 1];
                    ushort vC = indices[t * 3 + 2];

                    int lA = map.Local[vA], matA = map.Material[vA];
                    int lB = map.Local[vB];
                    int lC = map.Local[vC];

                    if (lA == lB || lB == lC || lA == lC) continue;

                    if (clean > 0.0f)
                    {
                        Vector3 pA = res.Verts[lA];
                        Vector3 pB = res.Verts[lB];
                        Vector3 pC = res.Verts[lC];

                        if (Vector3.DistanceSquared(pA, pB) > cleanSq ||
                            Vector3.DistanceSquared(pB, pC) > cleanSq ||
                            Vector3.DistanceSquared(pC, pA) > cleanSq)
                        {
                            res.Dropped++;
                            continue;
                        }
                    }

                    res.Faces.Add((lA, lB, lC));
                    res.Mats.Add(matA);
                }
            }

            nodeResults[nodeIdx] = res;

            if (progressCallback != null)
            {
                int current = Interlocked.Increment(ref completedCount);
                progressCallback(current, totalNodes);
            }
        });

        int allVerts = 0, allFaces = 0;
        foreach (NodeResult res in nodeResults)
        {
            allVerts += res.Verts.Count;
            allFaces += res.Faces.Count;
        }

        mesh.Vertices.Capacity = allVerts;
        mesh.Normals.Capacity = allVerts;
        mesh.Colors.Capacity = allVerts;
        mesh.VertexMaterials.Capacity = allVerts;
        mesh.VertexWeights.Capacity = allVerts;
        mesh.Faces.Capacity = allFaces;
        mesh.FaceMaterials.Capacity = allFaces;

        Dictionary<WeldKey, int>? globalVmap = weld ? new(allVerts) : null;
        int[] remap = [];

        foreach (NodeResult res in nodeResults)
        {
            mesh.DroppedFaces += res.Dropped;
            if (remap.Length < res.Verts.Count) remap = new int[res.Verts.Count];

            for (int i = 0; i < res.Verts.Count; i++)
            {
                Vector3 pos = res.Verts[i];

                if (weld)
                {
                    var (ms0, ms1, ms2) = res.Slots[i];
                    WeldKey key = new(pos, ms0, ms1, ms2);
                    int gIdx = mesh.Vertices.Count;
                    if (globalVmap!.TryAdd(key, gIdx))
                    {
                        mesh.Vertices.Add(pos);
                        mesh.Normals.Add(res.Norms[i]);
                        mesh.Colors.Add(res.Cols[i]);
                        mesh.VertexMaterials.Add(res.Slots[i]);
                        mesh.VertexWeights.Add(res.Weights[i]);
                    }
                    else
                    {
                        gIdx = globalVmap[key];
                    }
                    remap[i] = gIdx;
                }
                else
                {
                    remap[i] = mesh.Vertices.Count;
                    mesh.Vertices.Add(pos);
                    mesh.Normals.Add(res.Norms[i]);
                    mesh.Colors.Add(res.Cols[i]);
                    mesh.VertexMaterials.Add(res.Slots[i]);
                    mesh.VertexWeights.Add(res.Weights[i]);
                }
            }

            for (int f = 0; f < res.Faces.Count; f++)
            {
                var (a, b, c) = res.Faces[f];
                mesh.Faces.Add((remap[a], remap[b], remap[c]));
                mesh.FaceMaterials.Add(res.Mats[f]);
            }
        }

        return mesh;
    }

    /// <summary>What one node decodes to, before the nodes are welded together.</summary>
    private sealed class NodeResult(int triangles)
    {
        // A stream's triangles share most of their corners: about half as many vertices.
        public readonly List<Vector3> Verts = new(triangles / 2 + 16);
        public readonly List<Vector3> Norms = new(triangles / 2 + 16);
        public readonly List<Vector3> Cols = new(triangles / 2 + 16);
        public readonly List<(int A, int B, int C)> Slots = new(triangles / 2 + 16);
        public readonly List<Vector3> Weights = new(triangles / 2 + 16);
        public readonly List<(int A, int B, int C)> Faces = new(triangles);
        public readonly List<int> Mats = new(triangles);
        public int Dropped;
    }

    /// <summary>
    /// A vertex's identity for welding: its position to four decimals, and its three material
    /// slots. Rounded in here, so the per-node and the global weld cannot round differently.
    /// </summary>
    private readonly struct WeldKey(Vector3 pos, int s0, int s1, int s2) : IEquatable<WeldKey>
    {
        private readonly float _x = MathF.Round(pos.X, 4), _y = MathF.Round(pos.Y, 4), _z = MathF.Round(pos.Z, 4);
        private readonly int _s0 = s0, _s1 = s1, _s2 = s2;

        // float.Equals, as the tuple this replaces compared: -0 matches 0, NaN matches NaN.
        public bool Equals(WeldKey o) =>
            _x.Equals(o._x) && _y.Equals(o._y) && _z.Equals(o._z) &&
            _s0 == o._s0 && _s1 == o._s1 && _s2 == o._s2;

        public override bool Equals(object? obj) => obj is WeldKey k && Equals(k);

        public override int GetHashCode() => HashCode.Combine(_x, _y, _z, _s0, _s1, _s2);
    }

    /// <summary>
    /// Where each vertex index of the current stream landed: its local index and dominant
    /// material. One per thread, over every index a stream can hold, and never cleared - a
    /// stamp says which stream last wrote an entry.
    /// </summary>
    private sealed class StreamMap
    {
        [ThreadStatic] private static StreamMap? _current;

        public readonly int[] Stamp = new int[ushort.MaxValue + 1];
        public readonly int[] Local = new int[ushort.MaxValue + 1];
        public readonly int[] Material = new int[ushort.MaxValue + 1];
        private int _stamp;

        public static StreamMap ForThread() => _current ??= new StreamMap();

        public int Next()
        {
            if (++_stamp == int.MaxValue)
            {
                Array.Clear(Stamp);
                _stamp = 1;
            }
            return _stamp;
        }
    }

    private static int GetDominantSlotIndex(float w0, float w1, float w2)
    {
        if (w0 >= w1 && w0 >= w2) return 0;
        if (w1 >= w0 && w1 >= w2) return 1;
        return 2;
    }
}
