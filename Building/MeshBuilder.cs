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
        
        var nodeResults = new (List<Vector3> Verts, List<Vector3> Norms, List<Vector3> Cols, List<(int A, int B, int C)> Faces, List<int> Mats, List<(int A, int B, int C)> Slots, List<Vector3> Weights, int Dropped)[totalNodes];
        int completedCount = 0;

        Parallel.For(0, totalNodes, parallelOptions, nodeIdx =>
        {
            CrBinNode node = matchingNodes[nodeIdx];
            List<Vector3> localVerts = [];
            List<Vector3> localNorms = [];
            List<Vector3> localCols = [];
            List<(int A, int B, int C)> localFaces = [];
            List<int> localMats = [];
            List<(int A, int B, int C)> localSlots = [];
            List<Vector3> localWeights = [];
            int localDropped = 0;

            Dictionary<object, int> vmap = [];

            Vector3 nodeBase = new(
                origin.X - sl * 0.49993896f + node.Cell.X * sl,
                origin.Y - sl * 0.49993896f + node.Cell.Y * sl,
                origin.Z - sl * 0.49993896f + node.Cell.Z * sl
            );

            int streamEnd = (int)(node.BaseStream + node.StreamCount);
            for (int sIdx = (int)node.BaseStream; sIdx < streamEnd; sIdx++)
            {
                CrBinStream stream = crbin.Streams[sIdx];
                byte[] page = pages.GetPage(stream.PageFile);

                int triCount = (int)stream.Triangles;
                int indexOffset = (int)(stream.BaseIndex * 2);
                ReadOnlySpan<ushort> indices = MemoryMarshal.Cast<byte, ushort>(page.AsSpan(indexOffset, triCount * 3 * 2));

                Dictionary<ushort, (int LocalIdx, int MaterialIdx)> localMap = [];

                foreach (ushort v in indices)
                {
                    if (localMap.ContainsKey(v)) continue;

                    DecodedVertex dec = VertexDecoder.DecodeVertex(page, v);
                    var (qx, qy, qz) = dec.Self.QPos;
                    var (w1, w2) = dec.Self.Weights;
                    var (nu, nv) = dec.Self.NormalRaw;

                    Vector3 worldPos = nodeBase + new Vector3(qx * scale, qy * scale, qz * scale);

                    int w0 = 31 - w1 - w2;
                    int domSlot = GetDominantSlotIndex(w0, w1, w2);
                    int domMat = dec.Materials[domSlot];
                    int matIdx = node.BaseMaterial + domMat;

                    object key = weld
                        ? (MathF.Round(worldPos.X, 4), MathF.Round(worldPos.Y, 4), MathF.Round(worldPos.Z, 4))
                        : (stream.PageFile, v);

                    if (!vmap.TryGetValue(key, out int localIdx))
                    {
                        localIdx = localVerts.Count;
                        vmap[key] = localIdx;

                        localVerts.Add(worldPos);
                        localNorms.Add(OctNormalDecoder.Decode(nu, nv));
                        localCols.Add(dec.Self.ColorRgb);

                        // Keep the whole blend, not just the dominant slot: every vertex
                        // carries three materials and about half of them are genuinely mixed.
                        localSlots.Add((
                            node.BaseMaterial + dec.Materials[0],
                            node.BaseMaterial + dec.Materials[1],
                            node.BaseMaterial + dec.Materials[2]));
                        localWeights.Add(new Vector3(w0, w1, w2) / 31.0f);
                    }

                    localMap[v] = (localIdx, matIdx);
                }

                for (int t = 0; t < triCount; t++)
                {
                    ushort vA = indices[t * 3];
                    ushort vB = indices[t * 3 + 1];
                    ushort vC = indices[t * 3 + 2];

                    var (lA, matA) = localMap[vA];
                    var (lB, _) = localMap[vB];
                    var (lC, _) = localMap[vC];

                    if (lA == lB || lB == lC || lA == lC) continue;

                    if (clean > 0.0f)
                    {
                        Vector3 pA = localVerts[lA];
                        Vector3 pB = localVerts[lB];
                        Vector3 pC = localVerts[lC];

                        if (Vector3.DistanceSquared(pA, pB) > cleanSq ||
                            Vector3.DistanceSquared(pB, pC) > cleanSq ||
                            Vector3.DistanceSquared(pC, pA) > cleanSq)
                        {
                            localDropped++;
                            continue;
                        }
                    }

                    localFaces.Add((lA, lB, lC));
                    localMats.Add(matA);
                }
            }

            nodeResults[nodeIdx] = (localVerts, localNorms, localCols, localFaces, localMats, localSlots, localWeights, localDropped);

            if (progressCallback != null)
            {
                int current = Interlocked.Increment(ref completedCount);
                progressCallback(current, totalNodes);
            }
        });
        
        Dictionary<(float X, float Y, float Z), int> globalVmap = [];

        foreach (var res in nodeResults)
        {
            mesh.DroppedFaces += res.Dropped;
            int[] remap = new int[res.Verts.Count];

            for (int i = 0; i < res.Verts.Count; i++)
            {
                Vector3 pos = res.Verts[i];

                if (weld)
                {
                    var key = (MathF.Round(pos.X, 4), MathF.Round(pos.Y, 4), MathF.Round(pos.Z, 4));
                    if (!globalVmap.TryGetValue(key, out int gIdx))
                    {
                        gIdx = mesh.Vertices.Count;
                        globalVmap[key] = gIdx;

                        mesh.Vertices.Add(pos);
                        mesh.Normals.Add(res.Norms[i]);
                        mesh.Colors.Add(res.Cols[i]);
                        mesh.VertexMaterials.Add(res.Slots[i]);
                        mesh.VertexWeights.Add(res.Weights[i]);
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

    private static int GetDominantSlotIndex(int w0, int w1, int w2)
    {
        if (w0 >= w1 && w0 >= w2) return 0;
        if (w1 >= w0 && w1 >= w2) return 1;
        return 2;
    }
}
