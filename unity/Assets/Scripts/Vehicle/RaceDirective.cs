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

        /// <summary>One named member of the Fase 3 pilot population.</summary>
        public readonly struct PopulationMember
        {
            public readonly string Name;
            public readonly RaceDirective Directive;

            public PopulationMember(string name, DirectiveKind kind, float aggression, float risk)
            {
                Name = name;
                Directive = new RaceDirective
                {
                    Kind = kind,
                    Aggression = aggression,
                    RiskTolerance = risk,
                };
            }
        }

        /// <summary>
        /// Fixed pilot population for Fase 3 (CLAUDE.md §5). Camino A drops RL
        /// snapshots: the population is the one scripted controller
        /// (<c>RaceAgent.Heuristic</c>) driven by six directive presets, so it is
        /// pace-matched by construction instead of by hand-picking checkpoints
        /// (§5 warns that spaced checkpoints just make the last one win every
        /// time). The presets stay mid-range and close together on purpose — a
        /// wide Aggression spread makes one "pilot" lap seconds faster than
        /// another and contaminates the Fase 6.3 LLM-vs-heuristic comparison
        /// (§11). The eval harness <c>-population</c> mode runs them head to head
        /// on the fixed oval and reports mean lap time per member — the §5
        /// baseline every Fase 6.3 result is read against.
        /// </summary>
        public static readonly PopulationMember[] Population =
        {
            new PopulationMember("P1-Balanced",  DirectiveKind.Push,     0.50f, 0.50f),
            new PopulationMember("P2-LateBrake", DirectiveKind.Attack,   0.62f, 0.55f),
            new PopulationMember("P3-Defensive", DirectiveKind.Defend,   0.45f, 0.40f),
            new PopulationMember("P4-Smooth",    DirectiveKind.Conserve, 0.52f, 0.45f),
            new PopulationMember("P5-Aggro",     DirectiveKind.Attack,   0.58f, 0.62f),
            new PopulationMember("P6-Steady",    DirectiveKind.Push,     0.46f, 0.48f),
        };
    }
}
