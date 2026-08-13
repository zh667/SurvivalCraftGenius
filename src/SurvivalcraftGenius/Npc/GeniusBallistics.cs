// Ported from 铁器风云 1.0.0 (ProjectileAiming.CalculateAimPoint) by
// Abrams、Bradley, used with the authors' permission — see docs/ATTRIBUTION.md.
// Changes: variables named, the degenerate straight-up case documented, and the
// gravity constant lifted into a parameter so tests can pin it. Behaviour is
// unchanged: iterate time-of-flight against gravity plus target motion, return
// the point to aim AT, or null when the shot cannot be made.

using Engine;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Where to aim so the arrow and the target arrive together.
///
/// <para>This is the piece hunting birds actually needs. Aiming at where a duck
/// IS misses twice over — it has moved by the time the arrow lands, and the
/// arrow has fallen. Playtest after playtest reported "还是打不到鸟"; melee was
/// only half the reason.</para>
///
/// <para>Pure math, no engine types beyond Vector3, so unlike the rest of the
/// combat code it is fully testable on Linux.</para>
/// </summary>
public static class GeniusBallistics
{
    /// <summary>Survivalcraft projectile gravity (blocks/s²), matching the engine.</summary>
    public const float Gravity = 10f;

    private const int MaxIterations = 10;
    private const float ConvergenceSeconds = 0.0001f;

    /// <summary>
    /// The aim point, or null when the target cannot be hit — it outruns the
    /// projectile, or the solution is imaginary. Null is a real answer: it is
    /// how the caller knows to close in instead of wasting arrows.
    /// </summary>
    public static Vector3? AimPoint(
        Vector3 launchPoint,
        Vector3 targetPosition,
        Vector3 targetVelocity,
        float projectileSpeed,
        float gravity = Gravity)
    {
        if (projectileSpeed <= 0f)
        {
            return null;
        }

        var gravityVector = new Vector3(0f, -gravity, 0f);
        var gravityMagnitude = gravityVector.Length();
        if (gravityMagnitude < 1e-6f)
        {
            return AimWithoutGravity(launchPoint, targetPosition, targetVelocity, projectileSpeed);
        }

        var up = -Vector3.Normalize(gravityVector);
        var toTarget = targetPosition - launchPoint;
        var flightTime = Math.Max(toTarget.Length() / projectileSpeed, 0.01f);
        var previous = 0f;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            // Where the target will be if it keeps moving for flightTime.
            var lead = targetPosition + (targetVelocity * flightTime) - launchPoint;
            var vertical = Vector3.Dot(lead, up);
            var horizontalVector = lead - (vertical * up);
            var horizontal = horizontalVector.Length();

            if (horizontal < 1e-5f)
            {
                // Straight up or straight down: no horizontal component to
                // solve, so fall back to the 1-D throw equation.
                var discriminant = (projectileSpeed * projectileSpeed)
                    - (2f * gravityMagnitude * vertical);
                if (discriminant < 0f)
                {
                    return null;
                }

                var root = MathF.Sqrt(discriminant);
                var rising = (projectileSpeed - root) / gravityMagnitude;
                var falling = (projectileSpeed + root) / gravityMagnitude;
                flightTime = rising > 0f ? rising : falling;
                if (flightTime <= 0f)
                {
                    return null;
                }
            }
            else
            {
                var speedSquared = projectileSpeed * projectileSpeed;
                var discriminant = (speedSquared * speedSquared)
                    - (gravityMagnitude
                        * ((gravityMagnitude * horizontal * horizontal) + (2f * vertical * speedSquared)));
                if (discriminant < 0f)
                {
                    // The target is out of range at this speed, however we aim.
                    return null;
                }

                var slope = (speedSquared - MathF.Sqrt(discriminant)) / (gravityMagnitude * horizontal);
                var cosine = 1f / MathF.Sqrt(1f + (slope * slope));
                var solved = horizontal / (projectileSpeed * cosine);
                if (solved <= 0f)
                {
                    return null;
                }

                flightTime = solved;
            }

            if (iteration > 0 && MathF.Abs(flightTime - previous) < ConvergenceSeconds)
            {
                break;
            }

            previous = flightTime;
        }

        // Aim above the lead point by exactly the drop over the flight.
        return targetPosition
            + (targetVelocity * flightTime)
            - (0.5f * gravityVector * flightTime * flightTime);
    }

    /// <summary>Pure interception, for the gravity-free case.</summary>
    private static Vector3? AimWithoutGravity(
        Vector3 launchPoint, Vector3 targetPosition, Vector3 targetVelocity, float projectileSpeed)
    {
        var toTarget = targetPosition - launchPoint;
        var a = targetVelocity.LengthSquared() - (projectileSpeed * projectileSpeed);
        var b = 2f * Vector3.Dot(toTarget, targetVelocity);
        var c = toTarget.LengthSquared();
        var discriminant = (b * b) - (4f * a * c);
        if (discriminant < 0f)
        {
            return null;
        }

        var time = (-b - MathF.Sqrt(discriminant)) / (2f * a);
        if (time < 0f)
        {
            time = (-b + MathF.Sqrt(discriminant)) / (2f * a);
        }

        return time <= 0f ? null : targetPosition + (targetVelocity * time);
    }
}
