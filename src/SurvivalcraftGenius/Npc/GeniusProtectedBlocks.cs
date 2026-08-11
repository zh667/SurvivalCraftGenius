using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// "Is this something the player built?" — the code-level half of the
/// "绝不拆玩家的建筑和家具" rule, which until now lived only in the prompt and so
/// only held as far as the model chose to honour it.
///
/// Deliberately conservative: it answers YES only for blocks the terrain
/// generator never places, so natural stone, dirt, sand and ore are always
/// fair game. Being wrong in the NO direction costs a wall; being wrong in the
/// YES direction only costs the companion a slightly different route.
/// </summary>
public static class GeniusProtectedBlocks
{
    /// <summary>
    /// Cells our own digging must never destroy: farmland and standing crops.
    ///
    /// These are not "the player's property" — they are OUR work, which is why
    /// the player-build list never covered them and mining walked straight
    /// through a field. Playtest 14: told to fetch saltpeter, the companion dug
    /// down from the middle of the plot it had just tilled, and by the time it
    /// came back only one cell of nine was still farmland ("而且他去挖硝石直接在
    /// 田下面开挖,导致田都被他挖坏了几个").
    ///
    /// Digging the block UNDER farmland is fine — the engine only reverts soil
    /// when something lands ON it — so this is about the field cells themselves.
    /// </summary>
    public static bool IsOurFarmland(int contents) =>
        contents == GeniusFarming.SoilContents || GeniusFarming.IsCrop(contents);

    /// <summary>Anything a work order must not dig out: the player's, or the field.</summary>
    public static bool IsOffLimitsForDigging(int contents) =>
        IsPlayerBuilt(contents) || IsOurFarmland(contents);

    public static bool IsPlayerBuilt(int contents)
    {
        if (contents <= 0 || contents >= BlocksManager.Blocks.Length)
        {
            return false;
        }

        return BlocksManager.Blocks[contents] is
            ChestBlock
            or FurnaceBlock
            or CraftingTableBlock
            or TorchBlock
            or PlanksBlock
            or LadderBlock
            or DoorBlock
            or GlassBlock
            or BricksBlock
            or StairsBlock
            or FenceBlock
            or FenceGateBlock
            or WoodenAttachedSignBlock;
    }
}
