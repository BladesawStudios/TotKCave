using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using TotkCave.Models;
using TotkCave.PageSource;

namespace TotkCave.Building;

public static class QuadMeshBuilder
{
    private static readonly ConcurrentDictionary<(int Ns, int Top, int Right, int Bottom, int Left, int Single), int[]> IndexCache = new();
    private static readonly ConcurrentDictionary<int, (int A, int B, int C)[]> FaceCache = new();

    public static (int Vertices, int Faces, int Nodes) ExportObjStreaming(
        QuadResource res,
        IPageSource pages,
        string outputPath,
        int? lod = null,
        bool weld = true,
        int maxDegreeOfParallelism = -1,
        Action<int, int, int>? progressCallback = null)
    {
        int targetLod = lod ?? res.MaxLod;

        List<int> matchingNodeIndices = [];
        for (int i = 0; i < res.NodeCount; i++)
        {
            if (res.GetNodeLod(i) == targetLod)
            {
                matchingNodeIndices.Add(i);
            }
        }

        int totalNodes = matchingNodeIndices.Count;
        if (totalNodes == 0) return (0, 0, 0);

        int threads = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        int batchSize = 32;
        var batches = matchingNodeIndices.Chunk(batchSize).ToList();

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using StreamWriter writer = new(outputPath, false, Encoding.UTF8, bufferSize: 1 << 20);
        writer.WriteLine("# TotK Depths (MinusField) quad mesh - TotkCave (.NET 10)");
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# lod {targetLod}, world-space metres"));

        int totalVerts = 0;
        int totalFaces = 0;
        int processedNodes = 0;

        progressCallback?.Invoke(0, 0, totalNodes);

        var parallelQuery = batches
            .AsParallel()
            .AsOrdered()                                    // must directly follow AsParallel()
            .WithDegreeOfParallelism(threads)
            .WithMergeOptions(ParallelMergeOptions.NotBuffered)
            .Select(batch => DecodeBatch(res, pages, batch, targetLod, weld));

        foreach (var (text, batchVerts, batchFaces, nodeCount) in parallelQuery)
        {
            if (batchVerts > 0)
            {
                writer.Write(text);
                totalVerts += batchVerts;
                totalFaces += batchFaces;
            }
            processedNodes += nodeCount;
            progressCallback?.Invoke(processedNodes, totalVerts, totalNodes);
        }

        return (totalVerts, totalFaces, totalNodes);
    }

    private static (string Text, int Vertices, int Faces, int NodeCount) DecodeBatch(
        QuadResource res,
        IPageSource pages,
        int[] nodeIndices,
        int targetLod,
        bool weld)
    {
        StringBuilder sb = new(1024 * 1024);
        CultureInfo ci = CultureInfo.InvariantCulture;
        int batchVerts = 0;
        int batchFaces = 0;
        int nodesProcessed = 0;

        foreach (int i in nodeIndices)
        {
            var (verts, faces, _, _, _, _) = DecodeNodeGeometry(res, pages, i, targetLod, weld);
            nodesProcessed++;

            int vCount = verts.Count;
            if (vCount == 0) continue;

            for (int vIdx = 0; vIdx < vCount; vIdx++)
            {
                Vector3 v = verts[vIdx];
                sb.AppendLine(string.Create(ci, $"v {v.X:F4} {v.Y:F4} {v.Z:F4}"));
            }

            // Relative face indexing (-vCount .. -1)
            for (int fIdx = 0; fIdx < faces.Count; fIdx++)
            {
                var (a, b_, c) = faces[fIdx];
                sb.AppendLine(string.Create(ci, $"f {a - vCount} {b_ - vCount} {c - vCount}"));
            }

            batchVerts += vCount;
            batchFaces += faces.Count;
        }

        return (sb.ToString(), batchVerts, batchFaces, nodesProcessed);
    }

    private static (List<Vector3> Verts, List<(int A, int B, int C)> Faces,
                    List<(int A, int B, int C)> Slots, List<Vector3> Weights,
                    List<Vector3> Ao, List<int> FaceMats) DecodeNodeGeometry(
        QuadResource res,
        IPageSource pages,
        int i,
        int targetLod,
        bool weld)
    {
        List<Vector3> localVerts = [];
        List<(int A, int B, int C)> localFaces = [];
        List<(int A, int B, int C)> localSlots = [];
        List<Vector3> localWeights = [];
        List<Vector3> localAo = [];
        List<int> localFaceMats = [];
        Dictionary<(Vector3 P, int S0, int S1, int S2), int> vmap = [];

        var (nx, ny, nz, _) = res.GetNode(i);
        var (layout, ns, cornerOff) = res.GetNodeLayout(i);
        int vps = (1 << ns) + 1;
        int nvq = vps * vps;
        int blockBytes = nvq * 4;
        var facesPat = GetFacePattern(ns);
        float sl = res.GetSidelength(targetLod);
        int nsh = res.GetNodeShift(targetLod);

        float bx = res.SingleBounds.MinX;
        float by = res.SingleBounds.MinY;
        float bz = res.SingleBounds.MinZ;

        int pa0 = layout["pos_adjust_offset0"];
        int qdo = layout["quad_data_offset"];

        // Two per-vertex 2-byte maps, back to back: the weights and AO the shader calls
        // cCaveQuadMeshMaterialWeights_Ao, then the normals it calls cCaveQuadMeshNormals.
        //
        // They are easy to mix up - both are RGB565-shaped - so tell them apart by what the
        // fields do across the vertex grid. Weights vary smoothly: in the first block the
        // red and blue channels have a mean neighbour difference of 0.055 and 0.108, against
        // 0.133 for the vertex positions and 0.322 for the same values shuffled. In the
        // second they are 0.239 and 0.257, barely better than shuffled, because they are a
        // normal's tangential pair - and they satisfy |x| + |y| <= 1, the octahedral
        // constraint, on every vertex. Reading the normals as weights makes the dominant
        // material alternate vertex to vertex and the terrain break into flat triangles.
        int weightsOff = layout.TryGetValue("_30", out int a30) ? a30 : -1;

        var (s0, s1) = res.GetStreamRange(i);
        for (uint j = s0; j < s1; j++)
        {
            var (pfi, flags, baseVtx, nquads) = res.GetStream((int)j);
            if (flags != 0) continue;

            byte[] page = pages.GetPage(pfi);

            for (int k = 0; k < nquads; k++)
            {
                int qi = baseVtx + k;
                uint corner = MemoryMarshal.Read<uint>(page.AsSpan(cornerOff + qi * 4));
                uint posFlags = MemoryMarshal.Read<uint>(page.AsSpan(qdo + qi * 8));
                uint matFlags = MemoryMarshal.Read<uint>(page.AsSpan(qdo + qi * 8 + 4));

                int top = (corner & 0xFF) == 0xD ? 0 : 1;
                int right = ((corner >> 8) & 0xFF) == 0xD ? 0 : 1;
                int bottom = ((corner >> 16) & 0xFF) == 0xD ? 0 : 1;
                int left = ((corner >> 24) & 0xFF) == 0xD ? 0 : 1;
                int single = (int)((matFlags >> 31) & 1);

                // Three 7-bit material ids, at the bit positions the quad mesh fragment
                // shader unpacks them from. It picks its third id from bits 24-30 instead
                // on one triangle half of the quad, but the two agree on 99.5% of quads, so
                // the halves are not split here.
                int mat0 = (int)((matFlags >> 3) & 0x7F);
                int mat1 = (int)((matFlags >> 10) & 0x7F);
                int mat2 = (int)((matFlags >> 17) & 0x7F);

                int[] imap = GetIndexMap(ns, top, right, bottom, left, single);

                ReadOnlySpan<uint> block = MemoryMarshal.Cast<byte, uint>(page.AsSpan(pa0 + qi * blockBytes, blockBytes));
                ReadOnlySpan<ushort> attrs = weightsOff >= 0
                    ? MemoryMarshal.Cast<byte, ushort>(page.AsSpan(weightsOff + qi * nvq * 2, nvq * 2))
                    : default;

                int sh = (int)((posFlags >> 18) & 0x1F);
                long ox = ((((nx >> nsh) << 5) + (posFlags & 0x3F)) << 13) - 0x20000;
                long oy = ((((ny >> nsh) << 5) + ((posFlags >> 6) & 0x3F)) << 13) - 0x20000;
                long oz = ((((nz >> nsh) << 5) + ((posFlags >> 12) & 0x3F)) << 13) - 0x20000;

                int[] localIndices = new int[imap.Length];

                for (int slotIdx = 0; slotIdx < imap.Length; slotIdx++)
                {
                    int slot = imap[slotIdx];
                    uint adj = block[slot];

                    int dx = (int)(adj & 0x7FF);
                    if ((dx & 0x400) != 0) dx -= 0x800;

                    int dy = (int)((adj >> 11) & 0x3FF);
                    if ((dy & 0x200) != 0) dy -= 0x400;

                    int dz = (int)((adj >> 21) & 0x7FF);
                    if ((dz & 0x400) != 0) dz -= 0x800;

                    Vector3 v = new(
                        (ox + (dx << sh)) * sl + bx,
                        (oy + ((dy << 1) << sh)) * sl + by,
                        (oz + (dz << sh)) * sl + bz
                    );

                    // A1RGB555: bit 15 is set on every texel of this block, so it is a
                    // constant alpha and the three colour channels are 5 bits each. Green is
                    // the ambient occlusion - it varies smoothly across the vertex grid
                    // (mean neighbour difference 0.087, against 0.133 for the vertex
                    // positions and 0.322 shuffled) and resolves into lit plateau tops and
                    // dark crevices.
                    //
                    // The blend weights are NOT in this block, nor in the normals block, nor
                    // in pos_adjust_offset1. Checked against the mate terrain archive, which
                    // states the visible material per position: for a weight, its value would
                    // have to be high exactly when its own slot is the visible one, and no
                    // contiguous 4-6 bit field of any of those blocks separates the cases by
                    // more than 0.07 where a real weight would separate them by about 0.5.
                    //
                    // The ids are ordered by prevalence instead. Over 21,112 ground samples
                    // the first slot is the visible material 69.9% of the time, the second
                    // 22.0% and the third 8.2%, and some slot holds it 82.8% of the time. So
                    // weight them by that prior until the real weights are found: it agrees
                    // with the terrain archive 57.9% of the time where the misread per-vertex
                    // values managed 36.0%.
                    ushort packed = attrs.IsEmpty ? (ushort)0 : attrs[slot];
                    float ao = ((packed >> 5) & 0x1F) / 31.0f;
                    Vector3 wts = SlotPrior;

                    if (weld)
                    {
                        // Slots are part of a vertex's identity: welding on position alone
                        // would let a vertex shared by quads with different materials keep
                        // only one set, handing the wrong material to the other quad's faces.
                        var key = (v, mat0, mat1, mat2);
                        if (!vmap.TryGetValue(key, out int localIdx))
                        {
                            localIdx = localVerts.Count;
                            vmap[key] = localIdx;
                            localVerts.Add(v);
                            localSlots.Add((mat0, mat1, mat2));
                            localWeights.Add(wts);
                            localAo.Add(new Vector3(ao, ao, ao));
                        }
                        localIndices[slotIdx] = localIdx;
                    }
                    else
                    {
                        int localIdx = localVerts.Count;
                        localVerts.Add(v);
                        localSlots.Add((mat0, mat1, mat2));
                        localWeights.Add(wts);
                        localAo.Add(new Vector3(ao, ao, ao));
                        localIndices[slotIdx] = localIdx;
                    }
                }

                foreach (var (a, b_, c) in facesPat)
                {
                    int iA = localIndices[a];
                    int iB = localIndices[b_];
                    int iC = localIndices[c];

                    if (iA != iB && iB != iC && iA != iC)
                    {
                        localFaces.Add((iA, iB, iC));

                        Vector3 fw = localWeights[iA] + localWeights[iB] + localWeights[iC];
                        localFaceMats.Add(fw.X >= fw.Y && fw.X >= fw.Z ? mat0
                                        : fw.Y >= fw.Z ? mat1 : mat2);
                    }
                }
            }
        }

        return (localVerts, localFaces, localSlots, localWeights, localAo, localFaceMats);
    }

    public static CaveMesh BuildMesh(
        QuadResource res,
        IPageSource pages,
        int? lod = null,
        bool weld = true,
        int maxDegreeOfParallelism = -1,
        Action<int, int>? progressCallback = null,
        IReadOnlyList<QuadRegion>? regions = null)
    {
        int targetLod = lod ?? res.MaxLod;
        CaveMesh mesh = new();

        // A region list rather than one rectangle, so a caller can pick scattered map
        // sections without also pulling in everything between them.
        bool filtered = regions is { Count: > 0 };

        List<int> matchingNodeIndices = [];
        for (int i = 0; i < res.NodeCount; i++)
        {
            if (res.GetNodeLod(i) != targetLod) continue;

            if (filtered)
            {
                var nb = res.GetNodeBounds(i);
                bool hit = false;
                for (int r = 0; r < regions!.Count && !hit; r++)
                    hit = regions[r].Intersects(nb.MinX, nb.MinZ, nb.MaxX, nb.MaxZ);
                if (!hit) continue;
            }

            matchingNodeIndices.Add(i);
        }

        int totalNodes = matchingNodeIndices.Count;
        if (totalNodes == 0) return mesh;

        mesh.Materials = BuildQuadMaterials();

        progressCallback?.Invoke(0, totalNodes);

        int threads = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = threads };
        
        var nodeResults = new (List<Vector3> Verts, List<(int A, int B, int C)> Faces,
                               List<(int A, int B, int C)> Slots, List<Vector3> Weights,
                               List<Vector3> Ao, List<int> FaceMats)[totalNodes];
        int completedCount = 0;

        Parallel.For(0, totalNodes, parallelOptions, idx =>
        {
            int i = matchingNodeIndices[idx];
            nodeResults[idx] = DecodeNodeGeometry(res, pages, i, targetLod, weld);

            if (progressCallback != null)
            {
                int current = Interlocked.Increment(ref completedCount);
                progressCallback(current, totalNodes);
            }
        });

        foreach (var resNode in nodeResults)
        {
            int baseV = mesh.Vertices.Count;
            mesh.Vertices.AddRange(resNode.Verts);
            mesh.VertexMaterials.AddRange(resNode.Slots);
            mesh.VertexWeights.AddRange(resNode.Weights);
            mesh.Colors.AddRange(resNode.Ao);

            for (int f = 0; f < resNode.Faces.Count; f++)
            {
                var (a, b_, c) = resNode.Faces[f];
                mesh.Faces.Add((a + baseV, b_ + baseV, c + baseV));
                mesh.FaceMaterials.Add(resNode.FaceMats[f]);
            }
        }

        return mesh;
    }


    /// <summary>
    /// The material table for a quad resource. Unlike a cave, a quad resource ships no
    /// table: its shader uses the 7-bit material id directly as the array layer, with no
    /// indirection, so the entries here are generated rather than read.
    /// </summary>
    /// <remarks>
    /// The projection is planar on world XZ, which is what a heightfield wants. The tiling
    /// is a stand-in: the shader scales its UVs per material from a constant buffer that is
    /// not in the archive, so the real per-material scales are not recoverable from the
    /// shader alone. Everything else here - the layer, the projection - is exact.
    /// </remarks>
    /// <summary>
    /// How much each material slot contributes, from how often each turns out to be the
    /// material the terrain archive says is visible. A stand-in for the per-vertex weights,
    /// which are in the pages somewhere but have not been located.
    /// </summary>
    private static readonly Vector3 SlotPrior = new(0.699f, 0.220f, 0.082f);

    private const int QuadMaterialCount = 121;
    private const float QuadUvScale = 1.0f / 33.0f;

    private static List<CrBinMaterial> BuildQuadMaterials()
    {
        List<CrBinMaterial> materials = new(QuadMaterialCount);
        for (int id = 0; id < QuadMaterialCount; id++)
        {
            materials.Add(new CrBinMaterial(
                new Vector3(1f, 0f, 0f), id,
                new Vector3(0f, 0f, 1f), QuadUvScale));
        }
        return materials;
    }

    private static int[] GetIndexMap(int ns, int top, int right, int bottom, int left, int single)
    {
        var key = (ns, top, right, bottom, left, single);
        if (IndexCache.TryGetValue(key, out int[]? cached)) return cached;

        int vps = (1 << ns) + 1;
        int mx = 1 << ns;
        int[] outMap = new int[vps * vps];
        int idx = 0;

        for (int vy = 0; vy < vps; vy++)
        {
            for (int vx = 0; vx < vps; vx++)
            {
                int ax, ay;
                if (ns == 0)
                {
                    ax = ay = 0;
                }
                else if (single != 0)
                {
                    int diag = (vx + vy == mx) ? 1 : 0;
                    ax = (vx & diag & right) - (vx & (vy == 0 ? 1 : 0) & top);
                    ay = (vy & (vx == 0 ? 1 : 0) & left) - (vy & diag & right);
                }
                else
                {
                    ax = (vx & (vy == mx ? 1 : 0) & bottom) - (vx & (vy == 0 ? 1 : 0) & top);
                    ay = (vy & (vx == 0 ? 1 : 0) & left) - (vy & (vx == mx ? 1 : 0) & right);
                }
                outMap[idx++] = (vx + ax) + (vy + ay) * vps;
            }
        }

        return IndexCache.GetOrAdd(key, outMap);
    }

    private static (int A, int B, int C)[] GetFacePattern(int ns)
    {
        if (FaceCache.TryGetValue(ns, out var cached)) return cached;

        int vps = (1 << ns) + 1;
        List<(int A, int B, int C)> faces = [];

        for (int yy = 0; yy < vps - 1; yy++)
        {
            for (int xx = 0; xx < vps - 1; xx++)
            {
                int q = xx + yy * vps;
                faces.Add((q, q + 1, q + vps));
                faces.Add((q + vps, q + 1, q + vps + 1));
            }
        }

        return FaceCache.GetOrAdd(ns, faces.ToArray());
    }
}
