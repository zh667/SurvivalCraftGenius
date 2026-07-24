namespace SurvivalcraftGenius.Agent;

/// <summary>
/// A single capability exposed to the LLM via tool-calling. Implementations live in
/// Perception/ and Actions/; the agent layer only sees this contract, so it stays
/// free of game types and unit-testable off-device.
/// </summary>
public interface IGeniusTool
{
    /// <summary>Stable identifier sent to the LLM, e.g. "scan_surroundings".</summary>
    string Name { get; }

    /// <summary>One-line purpose statement included in the tool definition.</summary>
    string Description { get; }

    /// <summary>JSON Schema (object) describing the tool's arguments.</summary>
    string ParametersJsonSchema { get; }
}
