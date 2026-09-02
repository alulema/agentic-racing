using UnityEngine;

namespace AgenticRacing.Vehicle
{
    /// <summary>
    /// Shared physical parameters for a car. In Fase 1 there is one car and the
    /// values live here as defaults; Fase 3 makes a single asset instance the
    /// only source of truth so every car is mechanically identical
    /// (CLAUDE.md §3, and the Fase 3 requirement of no per-instance variation).
    ///
    /// The model is deliberately simple (CLAUDE.md §3: Rigidbody + manual torque,
    /// NO WheelCollider): one drive force along +Z, a braking force, an arcade
    /// yaw response that fades with speed, and a lateral-grip force that cancels
    /// most sideways sliding.
    /// </summary>
    [CreateAssetMenu(menuName = "Agentic Racing/Vehicle Config", fileName = "VehicleConfig")]
    public sealed class VehicleConfig : ScriptableObject
    {
        [Header("Mass / drag")]
        public float Mass = 1200f;
        public float LinearDrag = 0.2f;
        public float AngularDrag = 4f;

        [Header("Longitudinal")]
        [Tooltip("Forward acceleration force at full throttle (N).")]
        public float EngineForce = 12000f;
        [Tooltip("Braking force (N).")]
        public float BrakeForce = 18000f;
        [Tooltip("Engine braking / rolling resistance when coasting (N).")]
        public float CoastForce = 2500f;
        [Tooltip("Hard cap on forward speed (m/s). ~55 m/s ~= 200 km/h.")]
        public float MaxSpeed = 55f;
        [Tooltip("Cap on reverse speed (m/s).")]
        public float MaxReverseSpeed = 12f;

        [Header("Steering")]
        [Tooltip("Yaw rate at low speed, full lock (deg/s).")]
        public float TurnRateDegPerSec = 130f;
        [Tooltip("Fraction of TurnRate still available at MaxSpeed (0..1).")]
        public float HighSpeedTurnFactor = 0.35f;
        [Tooltip("Below this speed (m/s) the car barely steers (prevents spinning in place).")]
        public float SteerFadeInSpeed = 1.5f;

        [Header("Grip")]
        [Tooltip("Lateral grip as an acceleration multiplier; higher = less sliding.")]
        public float LateralGrip = 9f;
        [Tooltip("Extra downward force (N) to keep the car planted over crests.")]
        public float Downforce = 2000f;

        public static VehicleConfig CreateDefault()
        {
            var c = CreateInstance<VehicleConfig>();
            c.name = "VehicleConfig (runtime default)";
            return c;
        }
    }
}
