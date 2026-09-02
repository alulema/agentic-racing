using UnityEngine;

namespace AgenticRacing.Track
{
    public enum CornerDirection
    {
        Left,
        Right,
    }

    /// <summary>
    /// A numbered corner of the circuit. Indices are stable and deterministic for
    /// a given seed (CLAUDE.md Fase 1: "asígnales índices estables ... deben ser
    /// deterministas para una seed dada"). Arc-length fields are metres measured
    /// from the start/finish line along the centerline, in lap direction.
    /// </summary>
    public readonly struct TrackCorner
    {
        /// <summary>1-based corner number, counting from the start/finish line.</summary>
        public int Index { get; }

        /// <summary>Centerline index where the corner begins.</summary>
        public int StartSample { get; }

        /// <summary>Centerline index of the tightest point (apex).</summary>
        public int ApexSample { get; }

        /// <summary>Centerline index where the corner ends.</summary>
        public int EndSample { get; }

        public float StartArc { get; }
        public float ApexArc { get; }
        public float EndArc { get; }

        public CornerDirection Direction { get; }

        /// <summary>Total change of heading through the corner, in degrees (always positive).</summary>
        public float HeadingChangeDeg { get; }

        /// <summary>Tightest radius inside the corner, in metres.</summary>
        public float MinRadius { get; }

        public TrackCorner(int index, int startSample, int apexSample, int endSample,
            float startArc, float apexArc, float endArc,
            CornerDirection direction, float headingChangeDeg, float minRadius)
        {
            Index = index;
            StartSample = startSample;
            ApexSample = apexSample;
            EndSample = endSample;
            StartArc = startArc;
            ApexArc = apexArc;
            EndArc = endArc;
            Direction = direction;
            HeadingChangeDeg = headingChangeDeg;
            MinRadius = minRadius;
        }

        /// <summary>World position of the apex on the centerline.</summary>
        public Vector3 ApexPosition(TrackData track) => track.Centerline[ApexSample];
    }
}
