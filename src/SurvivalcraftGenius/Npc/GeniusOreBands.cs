namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Ore generation bands read off the engine's TerrainContentsGenerator (all
/// four generator versions agree) — see docs/MECHANICS-MINING.md.
/// Knowing these turns "dig down and hope" into "go to the depth where the ore
/// actually exists, then search": a surface scan for 铁 can never succeed, and
/// the playtest showed the companion burning whole minutes learning that the
/// hard way, one block at a time.
/// </summary>
public static class GeniusOreBands
{
    /// <summary>Vertical band an ore generates in, plus its host rock.</summary>
    public readonly record struct Band(string Ore, int MinY, int MaxY, string HostRock, string Note)
    {
        /// <summary>A good Y to stand at while searching: mid-band, above lava pockets.</summary>
        public int SearchY => Ore == "钻石" ? 12 : (MinY + MaxY) / 2;
    }

    private static readonly Band[] Bands =
    [
        new("煤", 5, 200, "花岗岩", "最好找,矿脉中心带一块煤块"),
        new("铜", 20, 65, "花岗岩", "孔雀石,竖向矿脉"),
        new("硝石", 50, 90, "砂岩", "热干群系地下的扁平大矿脉"),
        new("铁", 2, 40, "玄武岩", "地表搜不到属正常"),
        new("硫", 2, 40, "玄武岩", ""),
        new("锗", 2, 40, "玄武岩", ""),
        new("钻石", 2, 15, "玄武岩", "最稀;y15-20 上方常有岩浆窝"),
    ];

    /// <summary>Matches a resource query against the table; null when unknown.</summary>
    public static Band? Match(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var band in Bands)
        {
            if (query.Contains(band.Ore, StringComparison.OrdinalIgnoreCase))
            {
                return band;
            }
        }

        // English and the copper ore's own display name.
        if (query.Contains("孔雀", StringComparison.OrdinalIgnoreCase)
            || query.Contains("copper", StringComparison.OrdinalIgnoreCase)
            || query.Contains("malachite", StringComparison.OrdinalIgnoreCase))
        {
            return Bands[1];
        }

        if (query.Contains("coal", StringComparison.OrdinalIgnoreCase))
        {
            return Bands[0];
        }

        if (query.Contains("saltpeter", StringComparison.OrdinalIgnoreCase)
            || query.Contains("niter", StringComparison.OrdinalIgnoreCase))
        {
            return Bands[2];
        }

        if (query.Contains("iron", StringComparison.OrdinalIgnoreCase))
        {
            return Bands[3];
        }

        if (query.Contains("sulphur", StringComparison.OrdinalIgnoreCase)
            || query.Contains("sulfur", StringComparison.OrdinalIgnoreCase))
        {
            return Bands[4];
        }

        if (query.Contains("germanium", StringComparison.OrdinalIgnoreCase))
        {
            return Bands[5];
        }

        if (query.Contains("diamond", StringComparison.OrdinalIgnoreCase))
        {
            return Bands[6];
        }

        return null;
    }

    /// <summary>
    /// One-line explanation of why a search found nothing here, and where to
    /// go instead. Empty for non-ore queries.
    /// </summary>
    public static string Hint(string query, float myY)
    {
        if (Match(query) is not { } band)
        {
            return "";
        }

        var y = (int)myY;
        if (y >= band.MinY && y <= band.MaxY)
        {
            return $"; hint: {band.Ore}矿生成在 y{band.MinY}-{band.MaxY} 的{band.HostRock}层,我在 y={y} 已经在带内 — " +
                "矿脉是散布的,横向换个位置再搜(或加大 radius)";
        }

        var note = band.Note.Length > 0 ? $"({band.Note})" : "";
        return $"; hint: {band.Ore}矿只生成在 y{band.MinY}-{band.MaxY} 的{band.HostRock}层{note},我在 y={y} — " +
            $"先用 descend_to(y={band.SearchY}) 挖梯井下到矿层,再 find_blocks/mine_resource";
    }
}
