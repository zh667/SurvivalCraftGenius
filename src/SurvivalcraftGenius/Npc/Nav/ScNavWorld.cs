using Engine;
using Game;

namespace SurvivalcraftGenius.Npc.Nav;

/// <summary>
/// Maps Survivalcraft terrain to <see cref="NavCell"/>s. Built on the main
/// thread (snapshots dig times per block content with the best inventory tool,
/// and counts placeable blocks); <see cref="At"/> is then called from the
/// planner thread, reading the live terrain the same way the vanilla
/// pathfinder thread does. Unloaded chunks read as air (optimistic); the
/// executor re-checks cells on the main thread as it walks.
/// </summary>
public sealed class ScNavWorld : INavWorld
{
    private readonly Terrain _terrain;
    private readonly float[] _digSeconds;
    private readonly bool[] _treatAsAir;

    private ScNavWorld(Terrain terrain, float[] digSeconds, bool[] treatAsAir)
    {
        _terrain = terrain;
        _digSeconds = digSeconds;
        _treatAsAir = treatAsAir;
    }

    public NavCell At(int x, int y, int z)
    {
        if (y < 1)
        {
            return NavCell.Bedrock;
        }

        if (y > 254)
        {
            return NavCell.Air;
        }

        var value = _terrain.GetCellValue(x, y, z);
        var contents = Terrain.ExtractContents(value);
        if (contents == 0)
        {
            return NavCell.Air;
        }

        var block = BlocksManager.Blocks[contents];
        if (block is WaterBlock)
        {
            return new NavCell(NavKind.Water, float.PositiveInfinity, standable: false);
        }

        if (block is FluidBlock)
        {
            return new NavCell(NavKind.Lava, float.PositiveInfinity, standable: false);
        }

        if (block is CactusBlock or FireBlock or SpikedPlankBlock || block.ShouldAvoid(value))
        {
            return new NavCell(NavKind.Hazard, float.PositiveInfinity, standable: false);
        }

        var data = Terrain.ExtractData(value);
        if (block is DoorBlock)
        {
            return DoorBlock.GetOpen(data)
                ? NavCell.Air
                : new NavCell(NavKind.Door, _digSeconds[contents], standable: false);
        }

        if (block is FenceGateBlock)
        {
            return FenceGateBlock.GetOpen(data)
                ? NavCell.Air
                : new NavCell(NavKind.Door, _digSeconds[contents], standable: false);
        }

        if (block is TrapdoorBlock)
        {
            return TrapdoorBlock.GetOpen(data)
                ? NavCell.Air
                : new NavCell(NavKind.Door, _digSeconds[contents], standable: true);
        }

        if (!block.IsCollidable || _treatAsAir[contents])
        {
            return NavCell.Air;
        }

        // Fences are collidable but too tall to hop onto or stand on reliably.
        var standable = block is not FenceBlock;
        return new NavCell(NavKind.Solid, _digSeconds[contents], standable);
    }

    /// <summary>Captures the world view plus NPC capabilities. Main thread only.</summary>
    public static (ScNavWorld World, NavCapabilities Caps) Capture(
        ComponentGeniusBrain brain, bool allowDigging)
    {
        var blocks = BlocksManager.Blocks;
        var digSeconds = new float[blocks.Length];
        var treatAsAir = new bool[blocks.Length];
        var inventory = brain.Miner.Inventory;
        for (var contents = 1; contents < blocks.Length; contents++)
        {
            var block = blocks[contents];
            if (block is null)
            {
                digSeconds[contents] = float.PositiveInfinity;
                continue;
            }

            var value = Terrain.MakeBlockValue(contents);
            var best = brain.Miner.CalculateDigTime(value, 0);
            if (inventory is not null)
            {
                for (var slot = 0; slot < inventory.SlotsCount; slot++)
                {
                    var tool = Terrain.ExtractContents(inventory.GetSlotValue(slot));
                    if (tool != 0)
                    {
                        best = MathF.Min(best, brain.Miner.CalculateDigTime(value, tool));
                    }
                }
            }

            // Never casually chew through the player's crafted infrastructure —
            // route around it unless it is overwhelmingly shorter (Numen's
            // blocksToAvoidBreaking, as a 10x cost multiplier).
            if (block is CraftingTableBlock or FurnaceBlock or ChestBlock
                or DoorBlock or TrapdoorBlock or FenceGateBlock)
            {
                best *= 10f;
            }

            digSeconds[contents] = best;

            // Short-collision decorations (snow layers, fallen leaves) are
            // walked over, not dug through.
            if (block.IsCollidable)
            {
                try
                {
                    var boxes = block.GetCustomCollisionBoxes(brain.SubsystemTerrain, value);
                    if (boxes is { Length: > 0 })
                    {
                        var maxY = 0f;
                        foreach (var box in boxes)
                        {
                            maxY = MathF.Max(maxY, box.Max.Y);
                        }

                        treatAsAir[contents] = maxY <= 0.35f;
                    }
                }
                catch
                {
                    // Some blocks need world context for their boxes; keep the
                    // conservative default (solid).
                }
            }
        }

        var placeable = 0;
        if (inventory is not null)
        {
            for (var slot = 0; slot < inventory.SlotsCount; slot++)
            {
                var value = inventory.GetSlotValue(slot);
                if (value == 0 || inventory.GetSlotCount(slot) <= 0)
                {
                    continue;
                }

                var block = blocks[Terrain.ExtractContents(value)];
                if (block.IsPlaceable && block.IsCollidable && block is not FluidBlock)
                {
                    placeable += inventory.GetSlotCount(slot);
                }
            }
        }

        var caps = new NavCapabilities
        {
            AllowDig = allowDigging,
            AllowPlace = true,
            PlaceableBlocks = placeable,
        };
        return (new ScNavWorld(brain.SubsystemTerrain.Terrain, digSeconds, treatAsAir), caps);
    }
}
