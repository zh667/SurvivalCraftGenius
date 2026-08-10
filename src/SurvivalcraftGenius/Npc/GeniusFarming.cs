using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Survivalcraft's farming rules, read out of the engine rather than guessed.
/// See docs/MECHANICS-FARMING.md for the full write-up; the short version:
///
///  - <b>Tilling is a two-step ladder.</b> SubsystemRakeBlockBehavior.OnUse maps
///    grass(8) → dirt(2) and dirt(2) → soil(168). Raking grass does NOT give
///    farmland; it has to be raked twice.
///  - <b>Only 168 grows crops.</b> Rye and cotton check the cell below is 168
///    and read hydration/nitrogen out of its data.
///  - <b>Hydration is proximity to water</b>, not a watering action:
///    DetermineHydration walks up to 3 steps through dirt/soil looking for
///    water(18), where a vertical step costs 2. So a channel within ~3 blocks
///    hydrates the whole patch; hydration only halves growth time, it is not
///    required.
///  - <b>Nitrogen comes from saltpeter</b> (block 102), which sets nitrogen=3
///    over a 3x3. A harvest consumes one nitrogen.
///  - <b>Light ≥ 9 above the plant</b> or it does not grow at all.
///  - <b>Farmland is fragile</b>: putting any collidable, non-face-transparent
///    block on top reverts it to dirt, and a body heavier than 20 landing on it
///    does the same. Plants are fine; walking across a fresh field is not.
/// </summary>
public static class GeniusFarming
{
    public const int GrassContents = GrassBlock.Index;
    public const int DirtContents = DirtBlock.Index;
    public const int SoilContents = SoilBlock.Index;
    public const int WaterContents = WaterBlock.Index;

    /// <summary>Saltpeter chunk — SubsystemFertilizerBlockBehavior's only handled block.</summary>
    public const int FertilizerContents = 102;

    /// <summary>Inventory slot holding a rake, or null. Any tier works.</summary>
    public static int? FindRakeSlot(IInventory inventory) =>
        FindSlot(inventory, contents => BlocksManager.Blocks[contents] is RakeBlock);

    /// <summary>Inventory slot holding saltpeter, or null.</summary>
    public static int? FindFertilizerSlot(IInventory inventory) =>
        FindSlot(inventory, contents => contents == FertilizerContents);

    /// <summary>
    /// Inventory slot holding seeds whose display name matches, or null.
    /// Seeds are all one block (SeedsBlock, 173) distinguished by data, so the
    /// match has to run on the per-value display name, not the block name.
    /// </summary>
    public static int? FindSeedSlot(
        ComponentGeniusBrain brain, IInventory inventory, string query, out string? matchedName)
    {
        matchedName = null;
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var value = inventory.GetSlotValue(slot);
            if (inventory.GetSlotCount(slot) <= 0)
            {
                continue;
            }

            var block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            if (block is not SeedsBlock)
            {
                continue;
            }

            var name = block.GetDisplayName(brain.SubsystemTerrain, value);
            if (string.IsNullOrWhiteSpace(query)
                || name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || query.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                matchedName = name;
                return slot;
            }
        }

        return null;
    }

    /// <summary>Every seed the companion is carrying, for a helpful error.</summary>
    public static IEnumerable<string> CarriedSeedNames(
        ComponentGeniusBrain brain, IInventory inventory)
    {
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var value = inventory.GetSlotValue(slot);
            if (inventory.GetSlotCount(slot) > 0
                && BlocksManager.Blocks[Terrain.ExtractContents(value)] is SeedsBlock block)
            {
                yield return block.GetDisplayName(brain.SubsystemTerrain, value);
            }
        }
    }

    /// <summary>
    /// What the plant block above this seed should be, via the engine's own
    /// SeedsBlock.GetPlacementValue — so a new seed type added by an update is
    /// handled without touching this code. Face 4 is +Y (CellFace.m_faceToPoint3).
    /// </summary>
    public static int SeedPlacementValue(
        ComponentGeniusBrain brain, int seedValue, Point3 supportCell)
    {
        var block = BlocksManager.Blocks[Terrain.ExtractContents(seedValue)];
        var raycast = new TerrainRaycastResult
        {
            CellFace = new CellFace(supportCell.X, supportCell.Y, supportCell.Z, 4),
            Value = seedValue,
        };
        return block.GetPlacementValue(brain.SubsystemTerrain, brain.Miner, seedValue, raycast).Value;
    }

    /// <summary>
    /// Is this cell resting on real ground? Used before building or farming on
    /// it — a plot laid over a hole looks fine until the first block falls
    /// through ("盖房子和农田的时候应该看下面是不是浮空的").
    /// </summary>
    public static bool HasSolidSupport(ComponentGeniusBrain brain, Point3 cell) =>
        cell.Y > 0
        && BlocksManager.Blocks[Terrain.ExtractContents(
            brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y - 1, cell.Z))].IsCollidable;

    /// <summary>
    /// Farmland or a crop within one cell of this position. Water placed here
    /// washes the crop out and reverts the soil, which is why place_block
    /// refuses it ("放水的时候也应该注意不要浇到农田").
    /// </summary>
    public static Point3? FarmlandNear(ComponentGeniusBrain brain, Point3 cell)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var y = cell.Y + dy;
                    if (y is < 0 or > 255)
                    {
                        continue;
                    }

                    var contents = Terrain.ExtractContents(
                        terrain.GetCellValue(cell.X + dx, y, cell.Z + dz));
                    if (contents == SoilContents || IsCrop(contents))
                    {
                        return new Point3(cell.X + dx, y, cell.Z + dz);
                    }
                }
            }
        }

        return null;
    }

    public static bool IsCrop(int contents) =>
        contents == RyeBlock.Index || contents == CottonBlock.Index;

    /// <summary>
    /// Why this soil cell will or will not grow anything, in the model's terms.
    /// Mirrors GrowRye/GrowCotton: light, then hydration and nitrogen as speed
    /// modifiers rather than requirements.
    /// </summary>
    public static string DescribeSoil(ComponentGeniusBrain brain, Point3 cell)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var value = terrain.GetCellValue(cell.X, cell.Y, cell.Z);
        if (Terrain.ExtractContents(value) != SoilContents)
        {
            return "not farmland";
        }

        var data = Terrain.ExtractData(value);
        var light = Terrain.ExtractLight(terrain.GetCellValue(cell.X, cell.Y + 1, cell.Z));
        var parts = new List<string>
        {
            SoilBlock.GetHydration(data) ? "已湿润" : "干燥(附近3格内没水,长得慢一倍)",
            $"氮{SoilBlock.GetNitrogen(data)}" + (SoilBlock.GetNitrogen(data) > 0 ? "" : "(施硝石可加速)"),
        };

        if (light < 9)
        {
            parts.Add($"光照{light}<9,完全不会生长");
        }

        return string.Join("、", parts);
    }

    private static int? FindSlot(IInventory inventory, Func<int, bool> predicate)
    {
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            if (inventory.GetSlotCount(slot) > 0
                && predicate(Terrain.ExtractContents(inventory.GetSlotValue(slot))))
            {
                return slot;
            }
        }

        return null;
    }
}
