namespace TotkCave.Models;

/// <summary>
/// A world-space XZ rectangle used to build part of a quad resource instead of all of it.
/// The Depths is a single 10 x 8 km resource of over 100M triangles at the finest LOD, so
/// loading it whole is rarely what a caller wants.
/// </summary>
/// <remarks>
/// Deliberately has no notion of map sections or tile names: those are a presentation
/// concern, and callers that have them convert to world coordinates first.
/// </remarks>
public readonly record struct QuadRegion(float MinX, float MinZ, float MaxX, float MaxZ)
{
    /// <summary>True when this region shares any area with the given node bounds.</summary>
    public bool Intersects(float minX, float minZ, float maxX, float maxZ)
        => maxX >= MinX && minX <= MaxX && maxZ >= MinZ && minZ <= MaxZ;

    /// <summary>The smallest region covering both, for building a set of sections at once.</summary>
    public QuadRegion Union(QuadRegion other) => new(
        Math.Min(MinX, other.MinX), Math.Min(MinZ, other.MinZ),
        Math.Max(MaxX, other.MaxX), Math.Max(MaxZ, other.MaxZ));
}
