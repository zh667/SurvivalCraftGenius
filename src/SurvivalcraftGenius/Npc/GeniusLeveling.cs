using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Engine-side half of <see cref="GeniusGroundLevel"/>: reading the plot's
/// current shape and choosing what to backfill with.
/// </summary>
public static class GeniusLeveling
{
    /// <summary>
    /// Ground height of every column in the footprint, or null if any column
    /// has no readable ground (unloaded, or a bottomless hole).
    /// </summary>
    public static List<GeniusGroundLevel.Column>? Columns(
        ComponentGeniusBrain brain, int x, int z, int width, int length)
    {
        var columns = new List<GeniusGroundLevel.Column>(width * length);
        for (var dx = 0; dx < width; dx++)
        {
            for (var dz = 0; dz < length; dz++)
            {
                if (GeniusSiteSurvey.GroundHeight(brain, x + dx, z + dz) is not { } groundY)
                {
                    return null;
                }

                columns.Add(new GeniusGroundLevel.Column(x + dx, z + dz, groundY));
            }
        }

        return columns;
    }

    /// <summary>
    /// A slot to backfill from.
    ///
    /// <paramref name="forFarm"/> matters more than it looks: a hole filled with
    /// cobblestone is a hole you can never till. Farm plots take dirt or grass
    /// only, and say so rather than quietly laying stone under a field.
    /// </summary>
    public static int? FindFillSlot(ComponentGeniusBrain brain, IInventory inventory, bool forFarm)
    {
        var best = -1;
        var bestCount = 0;
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var count = inventory.GetSlotCount(slot);
            if (count <= 0)
            {
                continue;
            }

            var value = inventory.GetSlotValue(slot);
            var contents = Terrain.ExtractContents(value);
            var block = BlocksManager.Blocks[contents];
            if (!block.IsPlaceable || !block.IsCollidable)
            {
                continue;
            }

            if (forFarm)
            {
                if (contents != GeniusFarming.DirtContents
                    && contents != GeniusFarming.GrassContents
                    && contents != GeniusFarming.SoilContents)
                {
                    continue;
                }
            }
            else if (GeniusBuildMaterials.IsTooValuableForAWall(
                block, block.GetDisplayName(brain.SubsystemTerrain, value)))
            {
                continue;
            }

            if (count > bestCount)
            {
                best = slot;
                bestCount = count;
            }
        }

        return best >= 0 ? best : null;
    }
    /// <summary>
    /// Executes a levelling plan a cell at a time, driven from an order's tick.
    /// Keeps its own cursor so the caller only has to ask "are we done yet".
    /// </summary>
    public sealed class Runner(List<GeniusGroundLevel.LevelOp> ops, bool forFarm)
    {
        private const float ReachDistance = 4.5f;

        private int _index;
        private int _stuckCount;
        private float _elapsed;

        public int Cut { get; private set; }

        public int Filled { get; private set; }

        public int Skipped { get; private set; }

        public bool Done => _index >= ops.Count;

        /// <summary>Non-null = stop everything and report this to the player.</summary>
        public string? Failure { get; private set; }

        public string Summary() =>
            Cut == 0 && Filled == 0
                ? ""
                : $"整地:削平 {Cut} 格、垫高 {Filled} 格" +
                    (Skipped > 0 ? $"({Skipped} 格没做成)" : "");

        /// <summary>One tick of work. Returns when the caller should try again.</summary>
        public void Step(ComponentGeniusBrain brain, float dt)
        {
            if (Done)
            {
                return;
            }

            var op = ops[_index];
            var center = new Vector3(op.X + 0.5f, op.Y + 0.5f, op.Z + 0.5f);
            switch (GeniusApproach.Step(brain, center, ReachDistance, ref _stuckCount))
            {
                case GeniusApproach.Result.Walking:
                    return;
                case GeniusApproach.Result.Unreachable:
                    Skipped++;
                    _index++;
                    return;
            }

            _elapsed += dt;
            if (_elapsed < 0.25f)
            {
                return;
            }

            _elapsed = 0f;
            var terrain = brain.SubsystemTerrain.Terrain;
            var existing = Terrain.ExtractContents(terrain.GetCellValue(op.X, op.Y, op.Z));
            if (op.Fill)
            {
                if (existing != 0)
                {
                    _index++;
                    return;
                }

                var inventory = brain.Miner.Inventory;
                if (inventory is null || FindFillSlot(brain, inventory, forFarm) is not { } slot)
                {
                    Failure = forFarm
                        ? "error[missing_material]: 要垫平这块地得用泥土或草皮(石头填了就永远耕不了)——" +
                          "先 mine_resource 泥土 挖一些回来,或者换一块更平的地"
                        : "error[missing_material]: 要垫平这块地缺填充方块——" +
                          "mine_resource 泥土/石头 挖一些回来再叫我";
                    return;
                }

                brain.SubsystemTerrain.DestroyCell(
                    0, op.X, op.Y, op.Z, inventory.GetSlotValue(slot),
                    noDrop: false, noParticleSystem: true);
                inventory.RemoveSlotItems(slot, 1);
                Filled++;
                _index++;
                return;
            }

            if (existing == 0)
            {
                _index++;
                return;
            }

            if (GeniusProtectedBlocks.IsPlayerBuilt(existing))
            {
                // Never level away the player's own building.
                Skipped++;
                _index++;
                return;
            }

            brain.SubsystemTerrain.DestroyCell(
                0, op.X, op.Y, op.Z, 0, noDrop: false, noParticleSystem: true);
            Cut++;
            _index++;
        }
    }
}
