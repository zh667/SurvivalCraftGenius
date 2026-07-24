using System.Xml.Linq;

namespace SurvivalcraftGenius.Mod;

/// <summary>
/// Merges mod.netxdb into the game's template database. Top-level elements of
/// the source are anchors (matched by Guid against the base database); their
/// children are appended under the matching node unless already present.
/// </summary>
public static class GeniusDatabaseInjector
{
    public static void Inject(XElement source, XElement database)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(database);
        if (source.Name.LocalName != "SurvivalCraftMap")
        {
            throw new InvalidDataException("mod.netxdb root must be SurvivalCraftMap.");
        }

        foreach (var anchor in source.Elements())
        {
            var anchorGuid = GetRequiredGuid(anchor);
            var target = FindByGuid(database, anchorGuid)
                ?? throw new InvalidDataException(
                    $"Anchor {anchor.Name.LocalName} ({anchorGuid}) not found in the base database.");
            foreach (var child in anchor.Elements())
            {
                var childGuid = GetRequiredGuid(child);
                var existing = FindByGuid(database, childGuid);
                if (existing is not null)
                {
                    if (!ReferenceEquals(existing.Parent, target) || !XNode.DeepEquals(existing, child))
                    {
                        throw new InvalidDataException(
                            $"Guid collision for {childGuid}: a different element already exists.");
                    }

                    continue;
                }

                target.Add(new XElement(child));
            }
        }
    }

    private static Guid GetRequiredGuid(XElement element)
    {
        var raw = element.Attribute("Guid")?.Value
            ?? throw new InvalidDataException($"{element.Name.LocalName} is missing a Guid attribute.");
        return Guid.Parse(raw);
    }

    private static XElement? FindByGuid(XElement root, Guid guid)
    {
        var text = guid.ToString();
        foreach (var element in root.DescendantsAndSelf())
        {
            if (string.Equals(element.Attribute("Guid")?.Value, text, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }
        }

        return null;
    }
}
