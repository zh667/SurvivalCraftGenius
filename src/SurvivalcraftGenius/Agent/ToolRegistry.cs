namespace SurvivalcraftGenius.Agent;

/// <summary>
/// Holds the tools offered to the LLM for one agent session. Registration order is
/// preserved because it becomes the order of tool definitions in the request payload.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<IGeniusTool> _tools = [];
    private readonly Dictionary<string, IGeniusTool> _byName = new(StringComparer.Ordinal);

    public IReadOnlyList<IGeniusTool> Tools => _tools;

    public void Register(IGeniusTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("Tool name must be non-empty.", nameof(tool));
        }

        if (!_byName.TryAdd(tool.Name, tool))
        {
            throw new InvalidOperationException($"Duplicate tool name: {tool.Name}");
        }

        _tools.Add(tool);
    }

    public bool TryGet(string name, out IGeniusTool tool)
    {
        return _byName.TryGetValue(name, out tool!);
    }
}
