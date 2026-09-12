using System.Numerics;

namespace TotkCave.Models;

public sealed class CaveMesh
{
    public List<Vector3> Vertices { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector3> Colors { get; } = [];
    public List<(int A, int B, int C)> Faces { get; } = [];
    public List<int> FaceMaterials { get; } = [];
    public List<CrBinMaterial> Materials { get; set; } = [];
    public int DroppedFaces { get; set; }

    /// <summary>
    /// The texture coordinate a vertex was authored with, for geometry that carries one.
    /// Terrain has none - its materials are projected on world axes - but a placed model
    /// does, and its textures cannot be laid out without it.
    /// </summary>
    public List<Vector2> Uvs { get; } = [];

    /// <summary>
    /// The second coordinate, which a model's baked lighting is laid out in. Separate
    /// because it is a per-model atlas rather than a tiling coordinate.
    /// </summary>
    public List<Vector2> BakeUvs { get; } = [];

    /// <summary>
    /// How many distinct ids <see cref="FaceMaterials"/> uses, when the table they index
    /// is not <see cref="Materials"/> but one held beside the mesh.
    /// </summary>
    public int FaceMaterialCount { get; set; }

    /// <summary>
    /// The second texture coordinate, for a surface whose material layers two textures and
    /// lays the upper one out separately from the lower.
    /// </summary>
    public List<Vector2> Uvs2 { get; } = [];

    /// <summary>
    /// The direction a vertex's texture runs in, with the handedness of its other axis in W.
    /// A normal map is stored in that frame, and without it the frame has to be guessed from
    /// how the coordinate changes across the screen.
    /// </summary>
    public List<Vector4> Tangents { get; } = [];

    /// <summary>
    /// The alpha of a vertex's own colour, which is how a placed model fades one surface into
    /// another - a path into the grass beside it, a crack into the wall it is painted on.
    /// Separate from <see cref="Colors"/>, which carries only the three colour channels.
    /// </summary>
    public List<float> Alphas { get; } = [];

    /// <summary>True when every vertex carries that alpha.</summary>
    public bool HasAlphas => Alphas.Count == Vertices.Count && Vertices.Count > 0;

    /// <summary>True when every vertex carries a texture coordinate.</summary>
    public bool HasUvs => Uvs.Count == Vertices.Count && Vertices.Count > 0;

    /// <summary>True when every vertex carries the baked-lighting coordinate as well.</summary>
    public bool HasBakeUvs => BakeUvs.Count == Vertices.Count && Vertices.Count > 0;

    /// <summary>
    /// The three material slots each vertex blends between, already offset by the owning
    /// node's base material. <see cref="FaceMaterials"/> only records the dominant slot,
    /// which is enough for export but loses the blend the game actually renders.
    /// </summary>
    public List<(int A, int B, int C)> VertexMaterials { get; } = [];

    /// <summary>
    /// Blend weights for <see cref="VertexMaterials"/>, normalised to sum to 1. The encoded
    /// weights are 5-bit and sum to 31.
    /// </summary>
    public List<Vector3> VertexWeights { get; } = [];

    /// <summary>True when the per-vertex blend data was populated alongside the geometry.</summary>
    public bool HasVertexBlend => VertexMaterials.Count == Vertices.Count && Vertices.Count > 0;
}
