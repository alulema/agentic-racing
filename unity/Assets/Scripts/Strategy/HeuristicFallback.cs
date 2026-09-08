using AgenticRacing.Vehicle;

namespace AgenticRacing.Strategy
{
    /// <summary>
    /// The fixed heuristic strategy (CLAUDE.md Fase 4 "Fallback" + §6.3).
    ///
    /// Two jobs:
    ///  * safety net — when Ollama fails, times out, or the load circuit-breaker
    ///    is open, the car keeps racing on this instead of breaking (§7);
    ///  * the control group — selectable per car for the Fase 6.3 mixed field
    ///    (3 cars LLM vs 3 cars this), so it must be a real, reasonable strategy,
    ///    not a stub.
    ///
    /// Deterministic, no history: it reads the current snapshot only. Rules are
    /// intentionally simple and legible.
    /// </summary>
    public static class HeuristicFallback
    {
        // A rival is "in play" for attack/defend once inside this gap.
        private const float EngageGapS = 1.2f;
        // Comfortable margin: nobody within this either way -> just run your race.
        private const float ClearGapS = 5f;

        public static RaceDirective Decide(TelemetrySnapshot t, StrategyDirectiveMap map)
        {
            map = map != null ? map : StrategyDirectiveMap.Default;
            var me = t.Me;

            // Last lap: commit, whatever the situation.
            if (t.LapsRemaining <= 0 || t.Event == StrategyEvent.FinalLap)
                return map.ToDirective(DirectiveKind.Push, DirectiveLevel.High, DirectiveLevel.High);

            bool underPressure = me.GapBehind >= 0f && me.GapBehind < EngageGapS;
            bool canAttack = me.GapAhead >= 0f && me.GapAhead < EngageGapS;

            // Being hunted and not also attacking -> defend the position.
            if (underPressure && !canAttack)
                return map.ToDirective(DirectiveKind.Defend, DirectiveLevel.Medium, DirectiveLevel.Medium);

            // Someone catchable ahead -> go after them; harder if nobody is on me.
            if (canAttack)
            {
                var risk = underPressure ? DirectiveLevel.Medium : DirectiveLevel.High;
                return map.ToDirective(DirectiveKind.Attack, DirectiveLevel.High, risk);
            }

            // Clear air both ways: early race conserve, otherwise steady push.
            bool clear = (me.GapAhead < 0f || me.GapAhead > ClearGapS)
                         && (me.GapBehind < 0f || me.GapBehind > ClearGapS);
            if (clear && t.Lap <= 1)
                return map.ToDirective(DirectiveKind.Conserve, DirectiveLevel.Low, DirectiveLevel.Low);

            return map.ToDirective(DirectiveKind.Push, DirectiveLevel.Medium, DirectiveLevel.Medium);
        }
    }
}
