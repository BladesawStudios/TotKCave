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
            var (verts, faces, _, _, _, _, _) = DecodeNodeGeometry(res, pages, i, targetLod, weld);
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
                    List<Vector3> Ao, List<Vector3> Normals, List<int> FaceMats) DecodeNodeGeometry(
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
        List<Vector3> localNormals = [];
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

        // Two per-vertex 2-byte maps, back to back: the normals the shader calls
        // cCaveQuadMeshNormals, then the weights and AO it calls
        // cCaveQuadMeshMaterialWeights_Ao. They used to be taken the other way round.
        //
        // _30 is the normal, R5G5B5A1 with each channel decoded as (v - 16) / 15 - the
        // shader's fma(t, 2.0667, -1.0667) on a 5-bit channel. Decoded that way it agrees
        // with the normal of the vertex grid itself at a mean dot of 0.982, signed; every
        // decode of _34 as a normal scores about 0.5, which is chance. Its three channels
        // all peak at 10, 16 and 22, symmetric about zero, as a vector's components do.
        //
        // _34 is R5G6B5. Red and blue are the blend weights - both sit at 0 or 31 most of
        // the time, as a weight painted between two materials does - and green is the
        // occlusion, which the shader square-roots into the G-buffer's AO and never uses
        // as a weight. The two had been read as the normal's tangential pair.
        int normalsOff = layout.TryGetValue("_30", out int a30) ? a30 : -1;
        int weightsOff = layout.TryGetValue("_34", out int a34) ? a34 : -1;

        // The side of the square the game makes of that block, and how many quad tiles fit
        // across it. QuadMeshMgr::setupTextures picks 0x140 for the two large page sizes and
        // 0xa0 for the two small ones; side * side * 2 is exactly one block either way.
        int texSide = layout.TryGetValue("file_size", out int fileSize) ? TextureSide(fileSize) : 0;
        int tilesPerRow = texSide / vps;
        if (texSide <= 0 || tilesPerRow <= 0) weightsOff = normalsOff = -1;

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
                // shader unpacks them from, weighted by red, blue and what the two leave.
                // It picks its third id from bits 24-30 instead on the far triangle half of
                // the quad, but the two agree on 99.5% of quads, so the halves are not split.
                int mat0 = (int)((matFlags >> 3) & 0x7F);
                int mat1 = (int)((matFlags >> 10) & 0x7F);
                int mat2 = (int)((matFlags >> 17) & 0x7F);

                int[] imap = GetIndexMap(ns, top, right, bottom, left, single);

                ReadOnlySpan<uint> block = MemoryMarshal.Cast<byte, uint>(page.AsSpan(pa0 + qi * blockBytes, blockBytes));

                // The weights block is a texture, not an array of per-quad runs. The game
                // uploads it whole as a square R5G5B5A1 image - QuadMeshMgr::setupTextures
                // builds it from this offset at TextureSide(fileSize) on a side - and each
                // quad owns one vps by vps tile of it, laid out left to right then top to
                // bottom. Reading nvq consecutive texels instead walks across several
                // quads' tiles and hands most vertices another quad's data.
                ReadOnlySpan<ushort> weights = weightsOff >= 0
                    ? MemoryMarshal.Cast<byte, ushort>(page.AsSpan(weightsOff, texSide * texSide * 2))
                    : default;
                ReadOnlySpan<ushort> normals = normalsOff >= 0
                    ? MemoryMarshal.Cast<byte, ushort>(page.AsSpan(normalsOff, texSide * texSide * 2))
                    : default;
                // A page can hold more quads than the texture has tiles - the two large page
                // sizes fit exactly 4096 at five texels a side, but a 160 square one holds
                // only 1024 - so a quad past the end has no tile to read and keeps the prior.
                bool hasTile = !weights.IsEmpty && qi < tilesPerRow * tilesPerRow;
                int tileX = hasTile ? (qi % tilesPerRow) * vps : 0;
                int tileY = hasTile ? (qi / tilesPerRow) * vps : 0;

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

                    // R5G6B5: red at bits 0-4, green 5-10, blue 11-15. The shader's three
                    // weights are red, blue, and whatever the two leave of one, each clamped
                    // - so where red and blue already sum past one the third slot is out.
                    int texel = (tileY + slot / vps) * texSide + tileX + slot % vps;
                    Vector3 wts = SlotPrior;
                    float ao = 1f;
                    Vector3 n = Vector3.Zero;

                    if (hasTile)
                    {
                        ushort packed = weights[texel];
                        float r = (packed & 0x1F) / 31f;
                        float b = (packed >> 11) / 31f;
                        wts = new Vector3(r, b, Math.Clamp(1f - r - b, 0f, 1f));
                        ao = ((packed >> 5) & 0x3F) / 63f;

                        ushort nt = normals[texel];
                        n = new Vector3(
                            ((nt & 0x1F) - 16) / 15f,
                            (((nt >> 5) & 0x1F) - 16) / 15f,
                            (((nt >> 10) & 0x1F) - 16) / 15f);
                        n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.Zero;
                    }

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
                            localNormals.Add(n);
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
                        localNormals.Add(n);
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

        return (localVerts, localFaces, localSlots, localWeights, localAo, localNormals, localFaceMats);
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
                               List<Vector3> Ao, List<Vector3> Normals, List<int> FaceMats)[totalNodes];
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
            mesh.Normals.AddRange(resNode.Normals);

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
    /// The axes are nominal: the shader picks its projection from the surface normal, which
    /// the viewer does under triplanar. The tiling is per layer; see <see cref="LayerUvScale"/>.
    /// </remarks>
    /// <summary>
    /// How much each material slot contributes where a quad has no tile in the page's weight
    /// texture to read. Measured from how often each slot turns out to be the visible material.
    /// </summary>
    private static readonly Vector3 SlotPrior = new(0.699f, 0.220f, 0.082f);

    /// <summary>
    /// The side of the square texture the game builds from a page's per-vertex blocks, by
    /// page size. Mirrors the sizes QuadMeshMgr::setupTextures accepts; anything else gets
    /// zero, and the caller falls back to <see cref="SlotPrior"/> rather than guessing.
    /// </summary>
    private static int TextureSide(int fileSize) => fileSize switch
    {
        0x90000 or 0xec000 => 0x140,
        0x48000 or 0x76000 => 0xa0,
        _ => 0,
    };

    private const int QuadMaterialCount = 121;

    /// <summary>
    /// Each array layer's tiling, in repeats per metre. The shader reads it per layer from
    /// cave_StaticDataUBO (a vec4 per layer from 0x610, x the scale), which is filled at
    /// runtime rather than shipped. Every chunked cave's material table names a layer and a
    /// scale, though, and across all 392 of them each layer only ever has one scale - so the
    /// table is global, and this is it. Layers no cave uses take the commonest value, 0.05.
    /// </summary>
    private static readonly float[] LayerUvScale =
    [
        0.1f, 0.05f, 0.1f, 0.04f, 0.05f, 0.1f, 0.05f, 0.05f, 0.1f, 0.1f,                // 0
        1f / 15f, 0.09f, 0.05f, 0.2f, 0.2f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f,          // 10
        0.05f, 0.07f, 0.07f, 0.05f, 0.15f, 0.1f, 0.1f, 0.07f, 0.1f, 0.05f,              // 20
        0.03f, 0.05f, 0.2f, 0.2f, 0.1f, 0.2f, 0.15f, 0.2f, 0.35f, 0.2f,                 // 30
        0.1f, 0.05f, 1f / 6f, 0.15f, 0.2f, 0.05f, 0.05f, 0.1f, 0.05f, 0.05f,            // 40
        0.1f, 0.1f, 0.05f, 0.05f, 0.05f, 0.1f, 0.08f, 0.04f, 0.1f, 0.05f,               // 50
        0.08f, 0.25f, 0.04f, 0.08f, 0.08f, 0.2f, 0.1f, 0.15f, 0.04f, 0.25f,             // 60
        0.05f, 0.15f, 0.08f, 0.1f, 0.07f, 0.05f, 0.23f, 0.16f, 0.16f, 0.04f,            // 70
        0.1f, 0.05f, 0.1f, 0.2f, 0.09f, 0.07f, 0.09f, 0.2f, 0.05f, 0.05f,               // 80
        0.25f, 0.05f, 0.05f, 0.09f, 0.2f, 0.1f, 0.125f, 0.05f, 0.05f, 0.05f,            // 90
        0.05f, 0.1f, 0.1f, 0.05f, 0.32f, 0.07f, 0.1f, 0.3f, 0.07f, 0.05f,               // 100
        0.025f, 0.08f, 0.05f, 0.32f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.1f,           // 110
        0.05f,                                                                          // 120
    ];

    private static List<CrBinMaterial> BuildQuadMaterials()
    {
        List<CrBinMaterial> materials = new(QuadMaterialCount);
        for (int id = 0; id < QuadMaterialCount; id++)
        {
            materials.Add(new CrBinMaterial(
                new Vector3(1f, 0f, 0f), id,
                new Vector3(0f, 0f, 1f), LayerUvScale[id]));
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
