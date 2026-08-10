namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Staircase-shaft geometry, kept engine-free so it can be unit tested.
///
/// This exists because the v0.9.5 descent shipped with a two-cell carve and
/// nothing in the suite could see it: every descend_to in playtest 7 failed
/// with "could not step down into the shaft" after 0-3 levels, and the levels
/// it did manage were purely wherever the terrain was already air. The rule
/// below is small, total, and now covered by a solid-rock simulation.
/// </summary>
public static class GeniusDescentGeometry
{
    /// <summary>
    /// Cells of the creature's own body, as offsets from its feet cell. A human
    /// BoxSize.Y is 1.77 (Database.xml), so the body spans its feet cell and the
    /// one above — never just the feet cell.
    /// </summary>
    public static readonly int[] BodyHeights = [0, 1];

    /// <summary>
    /// The four compass steps, in the order the staircase rotates through them.
    /// Rotating (rather than dropping straight down) keeps the shaft walkable
    /// back up while staying inside a 2x2 footprint.
    /// </summary>
    public static readonly (int X, int Z)[] Directions =
    [
        (1, 0),
        (0, 1),
        (-1, 0),
        (0, -1),
    ];

    /// <summary>
    /// Heights to clear in the destination column, as offsets from the LANDING
    /// cell, ordered top-down.
    ///
    /// Three, not two. Stepping from feet <c>f</c> down to <c>f-1</c> one column
    /// over, the body passes through the new column at its current height
    /// before it drops — so that column must be open at <c>f</c> (landing+1)
    /// and at <c>f+1</c> (landing+2) as well as at the landing cell itself.
    /// Omitting landing+2 leaves the head embedded in rock and the walk never
    /// completes.
    ///
    /// Top-down because sand and gravel fall the instant they lose support:
    /// clear the ceiling before the floor or the floor fills back in.
    /// </summary>
    public static readonly int[] CarveHeights = [2, 1, 0];

    /// <summary>The landing cell for the step taken from <paramref name="feet"/>.</summary>
    public static (int X, int Y, int Z) StepTarget(
        (int X, int Y, int Z) feet, int directionIndex)
    {
        var direction = Directions[((directionIndex % Directions.Length) + Directions.Length)
            % Directions.Length];
        return (feet.X + direction.X, feet.Y - 1, feet.Z + direction.Z);
    }

    /// <summary>
    /// Every cell that must be air before the companion can take this step,
    /// top-down. Equivalently: the body's cells at the current height in the
    /// destination column, plus the landing cell.
    /// </summary>
    public static IEnumerable<(int X, int Y, int Z)> CellsToCarve(
        (int X, int Y, int Z) feet, int directionIndex)
    {
        var target = StepTarget(feet, directionIndex);
        foreach (var height in CarveHeights)
        {
            yield return (target.X, target.Y + height, target.Z);
        }
    }
}
