using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// "Is there real terrain here yet?" — the question every chunk check in this
/// mod was getting wrong.
///
/// <c>Terrain.GetChunkAtCell</c> returns a chunk object as soon as one is
/// ALLOCATED, long before the generator has filled it in. Until then every cell
/// reads 0 (air) and <c>GetTopHeight</c> returns 0. Code that only null-checks
/// therefore sees "loaded, and it's all air".
///
/// That is how the companion died in playtest 11: a teleport to (-41,110,313)
/// hovered waiting for the area, saw a freshly allocated chunk, took
/// GetTopHeight=0 as the ground, dropped the body to y≈1 — and seven seconds
/// later the generator filled the chunk in around it. Cause of death: 压死.
///
/// The engine's own answer is <c>State &gt;= TerrainChunkState.InvalidLight</c>:
/// that is what <c>ComponentIntro</c> (:148) requires before it will read cells
/// to place a player at spawn. Anything below it has not finished the four
/// content-generation passes.
/// </summary>
public static class GeniusTerrainReady
{
    /// <summary>
    /// Whether a chunk in this state has trustworthy cell contents. Below
    /// InvalidLight the four content passes have not all run.
    /// </summary>
    public static bool IsReadable(TerrainChunkState state) =>
        state >= TerrainChunkState.InvalidLight;

    /// <summary>Whether the column at (x, z) has real, generated terrain.</summary>
    public static bool HasCells(Terrain terrain, int x, int z) =>
        terrain.GetChunkAtCell(x, z) is { } chunk && IsReadable(chunk.State);

    /// <summary>Whether the column at (x, z) has real, generated terrain.</summary>
    public static bool HasCells(SubsystemTerrain subsystemTerrain, int x, int z) =>
        HasCells(subsystemTerrain.Terrain, x, z);
}
