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
