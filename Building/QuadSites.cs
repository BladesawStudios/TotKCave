using System.Numerics;
using System.Runtime.InteropServices;
using TotkCave.Models;
using TotkCave.PageSource;

namespace TotkCave.Building;

/// <summary>
/// One quad of a quad resource as it sits in its page: where its material ids and its tile of
/// the weight texture are, and where each of its vertex slots lands in the world.
/// </summary>
/// <remarks>
/// What an editor needs to paint a quad and write it back, which the mesh builder throws away:
/// the mesh it makes is welded and chunked, and says nothing of which page a vertex came from.
/// </remarks>
public sealed class QuadSite
{
    public required int Node { get; init; }
    public required int Lod { get; init; }

    /// <summary>The page file's index in the resource, and the quad's index within the page.</summary>
    public required int Page { get; init; }
    public required int Quad { get; init; }

    /// <summary>Byte offset in the page of the word holding the quad's four 7-bit material ids.</summary>
    public required int IdsOffset { get; init; }

    /// <summary>
    /// Byte offset in the page of the R5G6B5 weight and occlusion texture, or -1 when the page
    /// has none or the quad is past the tiles it holds.
    /// </summary>
    public required int WeightsOffset { get; init; }

    public required int TexSide { get; init; }
    public required int TileX { get; init; }
    public required int TileY { get; init; }

    /// <summary>Vertex slots along a side: five for a normal node, two for a far one.</summary>
    public required int Vps { get; init; }

    /// <summary>Each slot's position, as the mesh builder places it, before any lift the caller applies.</summary>
    public required Vector3[] Slots { get; init; }

    public required float MinX { get; init; }
    public required float MinZ { get; init; }
    public required float MaxX { get; init; }
    public required float MaxZ { get; init; }

    public bool HasTile => WeightsOffset >= 0;
    public long Key => ((long)Page << 32) | (uint)Quad;

    /// <summary>The texel of the weight texture a slot reads.</summary>
    public int TexelOf(int slot) => (TileY + slot / Vps) * TexSide + TileX + slot % Vps;

    /// <summary>The byte offset in the page of a slot's weight texel.</summary>
    public int WeightOffsetOf(int slot) => WeightsOffset + TexelOf(slot) * 2;
}

public static class QuadSites
{
    /// <summary>
    /// Every quad a node draws, at the node's own level of detail - streams flagged off are
    /// skipped, as the builder skips them.
    /// </summary>
    public static List<QuadSite> Of(QuadResource res, IPageSource pages, int node)
    {
        List<QuadSite> sites = [];
        int lod = res.GetNodeLod(node);

        var (nx, ny, nz, _) = res.GetNode(node);
        var (layout, ns, _) = res.GetNodeLayout(node);
        int vps = (1 << ns) + 1;
        int nvq = vps * vps;
        int blockBytes = nvq * 4;
        float sl = res.GetSidelength(lod);
        int nsh = res.GetNodeShift(lod);
        float bx = res.SingleBounds.MinX, by = res.SingleBounds.MinY, bz = res.SingleBounds.MinZ;

        int pa0 = layout["pos_adjust_offset0"];
        int qdo = layout["quad_data_offset"];
        int weightsOff = layout.TryGetValue("_34", out int a34) ? a34 : -1;
        int texSide = layout.TryGetValue("file_size", out int fileSize) ? QuadMeshBuilder.TextureSideOf(fileSize) : 0;
        int tilesPerRow = texSide / vps;
        if (texSide <= 0 || tilesPerRow <= 0) weightsOff = -1;

        var (s0, s1) = res.GetStreamRange(node);
        for (uint j = s0; j < s1; j++)
        {
            var (pfi, flags, baseVtx, nquads) = res.GetStream((int)j);
            if (flags != 0) continue;

            byte[] page = pages.GetPage(pfi);
            for (int k = 0; k < nquads; k++)
            {
                int qi = baseVtx + k;
                uint posFlags = MemoryMarshal.Read<uint>(page.AsSpan(qdo + qi * 8));
                ReadOnlySpan<uint> block = MemoryMarshal.Cast<byte, uint>(page.AsSpan(pa0 + qi * blockBytes, blockBytes));

                int sh = (int)((posFlags >> 18) & 0x1F);
                long ox = ((((nx >> nsh) << 5) + (posFlags & 0x3F)) << 13) - 0x20000;
                long oy = ((((ny >> nsh) << 5) + ((posFlags >> 6) & 0x3F)) << 13) - 0x20000;
                long oz = ((((nz >> nsh) << 5) + ((posFlags >> 12) & 0x3F)) << 13) - 0x20000;

                Vector3[] slots = new Vector3[nvq];
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                for (int slot = 0; slot < nvq; slot++)
                {
                    // Exactly the builder's arithmetic, so a slot lands on the very float the
                    // mesh has there and the two can be matched by equality.
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
                    slots[slot] = v;
                    minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X);
                    minZ = MathF.Min(minZ, v.Z); maxZ = MathF.Max(maxZ, v.Z);
                }

                bool hasTile = weightsOff >= 0 && qi < tilesPerRow * tilesPerRow;
                sites.Add(new QuadSite
                {
                    Node = node,
                    Lod = lod,
                    Page = pfi,
                    Quad = qi,
                    IdsOffset = qdo + qi * 8 + 4,
                    WeightsOffset = hasTile ? weightsOff : -1,
                    TexSide = texSide,
                    TileX = hasTile ? (qi % tilesPerRow) * vps : 0,
                    TileY = hasTile ? (qi / tilesPerRow) * vps : 0,
                    Vps = vps,
                    Slots = slots,
                    MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ,
                });
            }
        }

        return sites;
    }

    /// <summary>The nodes, at one level of detail or all of them, whose bounds reach a rectangle.</summary>
    public static IEnumerable<int> NodesIn(QuadResource res, float minX, float minZ, float maxX, float maxZ, int? lod = null)
    {
        for (int i = 0; i < res.NodeCount; i++)
        {
            if (lod is { } l && res.GetNodeLod(i) != l) continue;
            var b = res.GetNodeBounds(i);
            if (b.MaxX < minX || b.MinX > maxX || b.MaxZ < minZ || b.MinZ > maxZ) continue;
            yield return i;
        }
    }
}
