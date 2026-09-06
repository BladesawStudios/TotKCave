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
