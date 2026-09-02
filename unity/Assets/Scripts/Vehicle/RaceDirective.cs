namespace AgenticRacing.Vehicle
{
    /// <summary>High-level tactical stance the team boss issues (CLAUDE.md §6.4).</summary>
    public enum DirectiveKind
    {
        Attack,
        Defend,
        Conserve,
        Push,
    }

    /// <summary>
    /// The strategist's directive as the driver policy sees it. In Fase 2/3 these
    /// values are randomised every episode so the policy learns to condition its
    /// driving on them (CLAUDE.md §6.1 — must be part of the observation vector
    /// from the start; retrofitting in Fase 4 means retraining, §11). In Fase 4
    /// the LLM strategist fills them in instead.
    ///
    /// Discrete levels, not free 0..1 floats, everywhere the LLM chooses them
    /// (§6.4); here they are already numeric for the observation vector.
    /// </summary>
    public struct RaceDirective
    {
        /// <summary>0..1 — braking margin and corner-exit aggressiveness (§6.5).</summary>
        public float Aggression;

        /// <summary>0..1 — tolerance to proximity and contact (§6.5).</summary>
        public float RiskTolerance;

        /// <summary>Line bias / priority (§6.5).</summary>
        public DirectiveKind Kind;

        /// <summary>Floats this contributes to an observation vector: 2 scalars + 4-way one-hot.</summary>
        public const int ObservationSize = 6;

        public static RaceDirective Neutral => new RaceDirective
        {
            Aggression = 0.5f,
            RiskTolerance = 0.5f,
            Kind = DirectiveKind.Push,
        };

        /// <summary>
        /// A random directive for one training episode. Aggression and risk are
        /// snapped to a few discrete levels to match how the LLM will pick them
        /// (§6.4), so the policy sees the same value distribution in Fase 4.
        /// </summary>
        public static RaceDirective RandomEpisode(System.Random rng)
        {
            float[] levels = { 0.15f, 0.5f, 0.85f };
            return new RaceDirective
            {
                Aggression = levels[rng.Next(levels.Length)],
                RiskTolerance = levels[rng.Next(levels.Length)],
                Kind = (DirectiveKind)rng.Next(4),
            };
        }
    }
}
