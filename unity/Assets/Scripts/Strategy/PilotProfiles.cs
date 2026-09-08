using AgenticRacing.Vehicle;

namespace AgenticRacing.Strategy
{
    /// <summary>
    /// One-line pilot profile for each population member (CLAUDE.md §6.2: the
    /// strategist knows "qué snapshot lleva, sus tendencias observadas"). Camino
    /// A's population is directive presets on one controller, so the "tendency"
    /// is the preset's character. Goes into the stable prefix (§6.7).
    /// </summary>
    public static class PilotProfiles
    {
        public static string For(string populationName) => populationName switch
        {
            "P1-Balanced" => "Neutral line, consistent lap to lap, no strong tendency either way.",
            "P2-LateBrake" => "Brakes very late and gets on the power early; quick but can run wide on entry.",
            "P3-Defensive" => "Holds the inside line, brakes early, hard to pass but not the fastest in clear air.",
            "P4-Smooth" => "Smooth inputs, strong through slow corners, needs a higher pace call to match the others.",
            "P5-Aggro" => "Runs wheel-to-wheel, takes narrow gaps, high risk tolerance, occasional lock-up.",
            "P6-Steady" => "Wants to push but brakes conservatively; very repeatable, rarely makes mistakes.",
            _ => "No strong tendency on record.",
        };

        /// <summary>Fallback profile from the raw directive when the member name
        /// isn't known.</summary>
        public static string For(RaceDirective d)
        {
            string pace = d.Aggression < 0.4f ? "measured pace"
                : d.Aggression > 0.6f ? "aggressive pace" : "moderate pace";
            return $"{d.Kind} bias, {pace}, risk tolerance {(d.RiskTolerance > 0.55f ? "high" : "moderate")}.";
        }
    }
}
