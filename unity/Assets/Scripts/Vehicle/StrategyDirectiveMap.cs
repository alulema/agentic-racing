using UnityEngine;

namespace AgenticRacing.Vehicle
{
    /// <summary>Discrete strategist levels (CLAUDE.md §6.4). The LLM picks one of
    /// these for <c>aggression</c> and <c>risk_tolerance</c>; never a raw float.</summary>
    public enum DirectiveLevel
    {
        Low,
        Medium,
        High,
    }

    /// <summary>What a directive does to the driver, as concrete numbers the
    /// controller/heuristic consumes.</summary>
    public readonly struct DirectiveModulation
    {
        /// <summary>Multiplier on the curvature-appropriate target speed
        /// (&gt;1 = carry more speed, brake later).</summary>
        public readonly float SpeedScale;

        /// <summary>0..1 blend from racing line (0) toward centreline (1). Higher
        /// = safer, more margin; lower = apex-hugging.</summary>
        public readonly float CentreBlend;

        /// <summary>Extra m/s over target before the driver lifts/brakes.</summary>
        public readonly float BrakeMargin;

        public DirectiveModulation(float speedScale, float centreBlend, float brakeMargin)
        {
            SpeedScale = speedScale;
            CentreBlend = centreBlend;
            BrakeMargin = brakeMargin;
        }
    }

    /// <summary>
    /// The one place that maps a <see cref="RaceDirective"/> to controller
    /// modulation (CLAUDE.md §6.5: "Define este mapeo en un solo
    /// ScriptableObject, no disperso en el código"). Both the RL/heuristic pilot
    /// (<c>RaceAgent</c>) and the Fase 4 strategist read it, so the strategist's
    /// write surface is exactly these channels and nothing else.
    ///
    /// The numeric constants are serialized so Fase 4 can calibrate them without
    /// touching code. <see cref="Default"/> is a code-built instance with the
    /// values below, used wherever no asset is wired (the training/eval arenas
    /// build their agents in code).
    /// </summary>
    [CreateAssetMenu(menuName = "Agentic Racing/Strategy Directive Map", fileName = "StrategyDirectiveMap")]
    public sealed class StrategyDirectiveMap : ScriptableObject
    {
        [Header("Aggression (RaceDirective.Aggression 0..1)")]
        [Tooltip("Target-speed multiplier at aggression 0 and at aggression 1.")]
        [SerializeField] private Vector2 speedScaleRange = new Vector2(0.86f, 1.16f);
        [Tooltip("Brake margin (m/s over target) at aggression 0 and at aggression 1.")]
        [SerializeField] private Vector2 brakeMarginRange = new Vector2(0.3f, 2.2f);

        [Header("Line bias per directive kind (0 = racing line, 1 = centreline)")]
        [SerializeField] private float attackBlend = 0.45f;
        [SerializeField] private float pushBlend = 0.40f;
        [SerializeField] private float defendBlend = 0.78f;
        [SerializeField] private float conserveBlend = 0.92f;

        [Header("Risk tolerance (RaceDirective.RiskTolerance 0..1)")]
        [Tooltip("How much full risk pulls the line back toward the apex/edge.")]
        [SerializeField] private float riskBlendTrim = 0.15f;

        [Header("Conserve")]
        [Tooltip("Extra speed cut applied only when the directive kind is Conserve.")]
        [SerializeField] private float conserveSpeedMult = 0.9f;

        [Header("Discrete level -> 0..1")]
        [Tooltip("Numeric value for Low / Medium / High. Matches RaceDirective.RandomEpisode so the policy sees the same distribution it trained on.")]
        [SerializeField] private Vector3 levelValues = new Vector3(0.15f, 0.5f, 0.85f);

        private static StrategyDirectiveMap _default;

        /// <summary>Code-built instance with the default constants. Use when no
        /// asset is assigned.</summary>
        public static StrategyDirectiveMap Default
        {
            get
            {
                if (_default == null)
                {
                    _default = CreateInstance<StrategyDirectiveMap>();
                    _default.name = "StrategyDirectiveMap (default)";
                }
                return _default;
            }
        }

        /// <summary>Numeric value of a discrete level (§6.4).</summary>
        public float LevelToFloat(DirectiveLevel level) => level switch
        {
            DirectiveLevel.Low => levelValues.x,
            DirectiveLevel.High => levelValues.z,
            _ => levelValues.y,
        };

        /// <summary>Build the <see cref="RaceDirective"/> the pilot consumes from
        /// the strategist's discrete choice (§6.4 -> §6.1 channels).</summary>
        public RaceDirective ToDirective(DirectiveKind kind, DirectiveLevel aggression, DirectiveLevel risk)
            => new RaceDirective
            {
                Kind = kind,
                Aggression = LevelToFloat(aggression),
                RiskTolerance = LevelToFloat(risk),
            };

        /// <summary>Resolve a directive to concrete controller modulation.</summary>
        public DirectiveModulation Resolve(RaceDirective d)
        {
            float agg = Mathf.Clamp01(d.Aggression);
            float risk = Mathf.Clamp01(d.RiskTolerance);

            float speedScale = Mathf.Lerp(speedScaleRange.x, speedScaleRange.y, agg);
            if (d.Kind == DirectiveKind.Conserve) speedScale *= conserveSpeedMult;

            float baseBlend = d.Kind switch
            {
                DirectiveKind.Attack => attackBlend,
                DirectiveKind.Push => pushBlend,
                DirectiveKind.Defend => defendBlend,
                DirectiveKind.Conserve => conserveBlend,
                _ => pushBlend,
            };
            float centreBlend = Mathf.Clamp01(baseBlend - risk * riskBlendTrim);

            float brakeMargin = Mathf.Lerp(brakeMarginRange.x, brakeMarginRange.y, agg);

            return new DirectiveModulation(speedScale, centreBlend, brakeMargin);
        }
    }
}
