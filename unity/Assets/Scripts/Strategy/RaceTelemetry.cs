using System.Collections.Generic;
using AgenticRacing.Vehicle;

namespace AgenticRacing.Strategy
{
    /// <summary>Why a strategy call was triggered (CLAUDE.md §6.6).</summary>
    public enum StrategyEvent
    {
        LapCompleted,
        RivalInRange,
        PositionChange,
        Incident,
        FinalLap,
    }

    /// <summary>How a rival's gap to me is moving (§6.3).</summary>
    public enum GapTrend
    {
        Closing,
        Stable,
        Dropping,
    }

    /// <summary>One rival as the strategist sees it — only what is observable
    /// from outside the car (position and times), never their internal
    /// telemetry (§6.2).</summary>
    public readonly struct RivalSnapshot
    {
        public readonly string CarId;
        public readonly int Position;
        public readonly float GapSeconds; // signed: negative = ahead of me
        public readonly float LastLapTime; // <=0 = unknown
        public readonly GapTrend Trend;

        public RivalSnapshot(string carId, int position, float gapSeconds, float lastLapTime, GapTrend trend)
        {
            CarId = carId;
            Position = position;
            GapSeconds = gapSeconds;
            LastLapTime = lastLapTime;
            Trend = trend;
        }
    }

    /// <summary>My own race state (the strategist's, not the driver's — no
    /// frame-level speed/angle here, §6.2).</summary>
    public readonly struct SelfSnapshot
    {
        public readonly string CarId;
        public readonly int Position;
        public readonly float LastLapTime;   // <=0 = unknown
        public readonly float BestLapTime;   // <=0 = unknown
        public readonly float GapAhead;      // seconds; <0 = I'm the leader
        public readonly float GapBehind;     // seconds; <0 = I'm last
        public readonly DirectiveKind CurrentDirective;
        public readonly int Incidents;

        public SelfSnapshot(string carId, int position, float lastLapTime, float bestLapTime,
            float gapAhead, float gapBehind, DirectiveKind currentDirective, int incidents)
        {
            CarId = carId;
            Position = position;
            LastLapTime = lastLapTime;
            BestLapTime = bestLapTime;
            GapAhead = gapAhead;
            GapBehind = gapBehind;
            CurrentDirective = currentDirective;
            Incidents = incidents;
        }
    }

    /// <summary>The full variable payload for one strategy call (§6.3). The race
    /// scene (Fase 4, next step) builds this; <see cref="RaceStrategist"/>
    /// serialises and sends it.</summary>
    public sealed class TelemetrySnapshot
    {
        public StrategyEvent Event;
        public int Lap;
        public int LapsRemaining;
        public SelfSnapshot Me;
        public IReadOnlyList<RivalSnapshot> Rivals = System.Array.Empty<RivalSnapshot>();

        /// <summary>Lap-over-lap memory (§6.3). Banded by the strategist to the
        /// last few laps plus a small per-corner set — unbounded, the per-call
        /// cost grows with the race.</summary>
        public IReadOnlyList<string> Notes = System.Array.Empty<string>();
    }
}
