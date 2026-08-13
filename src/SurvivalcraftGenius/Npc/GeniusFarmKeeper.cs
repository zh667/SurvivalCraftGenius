// The scan-decide-act loop, the 0.2s action cooldown and the 10s state timeout
// are ported from 工具人 1.1 (ComponentGuardFarmer) by 基岩, used with the
// author's permission — see docs/ATTRIBUTION.md.
// Changes: work is issued as ordinary GeniusOrders rather than a private state
// machine, so the mode inherits our approach retry, farmland protection and
// crop rules instead of duplicating them; and the mode yields the body to any
// LLM order rather than competing with one.

using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// The standing farm order: keeps a plot without asking anyone.
///
/// <para>Costs nothing per cycle, which is the point. A maintenance round done
/// through the LLM is four-plus steps at ~13k tokens each, repeated every time
/// the player returns; here the model pays once to set the mode up and the body
/// does the rest forever.</para>
///
/// <para>It is the lowest bidder for the body: any LLM order or instinct takes
/// over, and the mode simply resumes when they are done.</para>
/// </summary>
public sealed class GeniusFarmKeeper
{
    private double _nextActionTime;
    private double _stateStartedAt;
    private GeniusOrder? _work;

    public bool Enabled { get; private set; }

    public Point3 Centre { get; private set; }

    public int Radius { get; private set; } = GeniusFarmMode.DefaultRadius;

    /// <summary>What to plant into bare farmland; empty means whatever is in the bag.</summary>
    public string SeedName { get; private set; } = "";

    public int Harvested { get; private set; }

    public int Planted { get; private set; }

    public int PickedUp { get; private set; }

    /// <summary>Last reason the mode had nothing to do, for status reporting.</summary>
    public string IdleReason { get; private set; } = "";

    public void Start(Point3 centre, int radius, string seedName)
    {
        Enabled = true;
        Centre = centre;
        Radius = Math.Clamp(radius, 2, 32);
        SeedName = seedName ?? "";
        Harvested = Planted = PickedUp = 0;
        IdleReason = "";
        _work = null;
        _stateStartedAt = 0.0;
    }

    public void Stop()
    {
        Enabled = false;
        _work = null;
    }

    public string Describe() =>
        !Enabled
            ? "看田模式:关"
            : $"看田模式:开(中心 {Centre.X},{Centre.Y},{Centre.Z},半径 {Radius}" +
              (SeedName.Length > 0 ? $",种{SeedName}" : "") +
              $");已收 {Harvested}、已种 {Planted}、已捡 {PickedUp}" +
              (IdleReason.Length > 0 ? $";当前{IdleReason}" : "");

    /// <summary>
    /// One tick. Does nothing at all unless the mode is on AND the body is
    /// otherwise free — the LLM's orders and the survival instincts both outrank
    /// it, and yielding is cheaper than arbitrating.
    /// </summary>
    public void Tick(ComponentGeniusBrain brain, float dt)
    {
        if (!Enabled || brain.CurrentOrder is not null || brain.IsFollowing)
        {
            return;
        }

        var now = brain.GameTime;
        if (_work is not null)
        {
            // Our own sub-order is running. Abandon it if it stalls, so a single
            // unreachable cell cannot wedge the mode forever.
            if (_work.Completion.IsCompleted)
            {
                _work = null;
            }
            else if (now - _stateStartedAt > GeniusFarmMode.StateTimeoutSeconds)
            {
                _work = null;
                _stateStartedAt = now;
            }

            return;
        }

        if (now < _nextActionTime)
        {
            return;
        }

        _nextActionTime = now + GeniusFarmMode.ActionCooldownSeconds;

        var world = Survey(brain);
        switch (GeniusFarmMode.Decide(world))
        {
            case FarmAction.PickUp:
                PickedUp++;
                brain.VacuumNearbyPickables(Radius);
                IdleReason = "";
                break;

            case FarmAction.Harvest:
                Harvested++;
                IdleReason = "";
                Begin(brain, new HarvestCropsOrder(Centre, Radius, includeWild: false), now);
                break;

            case FarmAction.Plant:
                Planted++;
                IdleReason = "";
                Begin(brain, new PlantSeedOrder(Centre, SeedName, count: Radius * Radius), now);
                break;

            default:
                IdleReason = GeniusFarmMode.ExplainIdle(world);
                break;
        }
    }

    private void Begin(ComponentGeniusBrain brain, GeniusOrder order, double now)
    {
        _work = order;
        _stateStartedAt = now;
        // Uses the ordinary order slot, dispatched as turn 0 so it can never
        // look like a same-turn duplicate. Tick only runs when the slot is free,
        // and any LLM dispatch supersedes it — which is exactly right: the
        // player's instruction always outranks a standing order.
        brain.StartOrder(order, turnId: 0);
    }

    /// <summary>No empty slot left to put anything in.</summary>
    private static bool IsFull(IInventory inventory)
    {
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            if (inventory.GetSlotCount(slot) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the world the way 工具人 does — a small box, rescanned each time,
    /// 11x11x5 rather than the ~139,000-cell sweeps elsewhere in this codebase.
    /// </summary>
    private FarmSnapshot Survey(ComponentGeniusBrain brain)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var ripe = false;
        var bare = false;
        for (var dx = -Radius; dx <= Radius && !(ripe && bare); dx++)
        {
            for (var dz = -Radius; dz <= Radius && !(ripe && bare); dz++)
            {
                var x = Centre.X + dx;
                var z = Centre.Z + dz;
                if (!GeniusTerrainReady.HasCells(terrain, x, z))
                {
                    continue;
                }

                for (var dy = -2; dy <= 2; dy++)
                {
                    var y = Centre.Y + dy;
                    var contents = Terrain.ExtractContents(terrain.GetCellValue(x, y, z));
                    if (GeniusHarvestRules.IsCrop(contents))
                    {
                        var data = Terrain.ExtractData(terrain.GetCellValue(x, y, z));
                        var (size, isWild) = HarvestCropsOrder.DecodeCrop(contents, data);
                        ripe |= !isWild && GeniusHarvestRules.IsRipe(contents, size, isWild);
                    }
                    else if (contents == GeniusFarming.SoilContents
                        && Terrain.ExtractContents(terrain.GetCellValue(x, y + 1, z)) == 0)
                    {
                        bare = true;
                    }
                }
            }
        }

        var inventory = brain.Miner.Inventory;
        return new FarmSnapshot(
            PickableInRange: brain.HasPickableWithin(Radius),
            RipeCropInRange: ripe,
            BareFarmlandInRange: bare,
            HasSeeds: inventory is not null
                && GeniusFarming.FindSeedSlot(brain, inventory, SeedName, out _) is not null,
            InventoryFull: inventory is not null && IsFull(inventory),
            UnderAttack: brain.Instincts.ActiveInstinct is not null);
    }
}
