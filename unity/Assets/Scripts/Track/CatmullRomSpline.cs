using System;
using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Closed (cyclic) centripetal Catmull-Rom spline through a set of control
    /// points in the XZ plane, represented here as <see cref="Vector2"/> (x, y)
    /// where y maps to world Z.
    ///
    /// Centripetal parameterisation (alpha = 0.5) is used deliberately: unlike
    /// the uniform variant it never produces cusps or self-intersections *within*
    /// a segment even when control points are unevenly spaced, which matters for
    /// a procedurally jittered circuit (CLAUDE.md Fase 1: "un cambio brusco de
    /// curvatura en el punto de cierre arruina el render y el entrenamiento").
    /// The curve is C1-continuous, including across the wrap-around seam.
    /// </summary>
    public static class CatmullRomSpline
    {
        private const float Alpha = 0.5f;      // centripetal
        private const float MinKnotDelta = 1e-4f;

        /// <summary>
        /// Densely samples the closed spline through <paramref name="controlPoints"/>.
        /// Returns an open polyline (the closing point equals the first and is
        /// NOT repeated) with <paramref name="samplesPerSegment"/> points per
        /// control-point interval.
        /// </summary>
        public static List<Vector2> SampleClosed(IReadOnlyList<Vector2> controlPoints, int samplesPerSegment)
        {
            if (controlPoints == null) throw new ArgumentNullException(nameof(controlPoints));
            if (controlPoints.Count < 4)
                throw new ArgumentException("Need at least 4 control points for a closed Catmull-Rom loop.", nameof(controlPoints));
            if (samplesPerSegment < 1) throw new ArgumentException("samplesPerSegment must be >= 1.", nameof(samplesPerSegment));

            int n = controlPoints.Count;
            var result = new List<Vector2>(n * samplesPerSegment);

            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = controlPoints[Mod(i - 1, n)];
                Vector2 p1 = controlPoints[Mod(i, n)];
                Vector2 p2 = controlPoints[Mod(i + 1, n)];
                Vector2 p3 = controlPoints[Mod(i + 2, n)];

                float t0 = 0f;
                float t1 = t0 + KnotDelta(p0, p1);
                float t2 = t1 + KnotDelta(p1, p2);
                float t3 = t2 + KnotDelta(p2, p3);

                // Sample the segment between p1 and p2, i.e. t in [t1, t2).
                for (int s = 0; s < samplesPerSegment; s++)
                {
                    float u = s / (float)samplesPerSegment;
                    float t = Mathf.Lerp(t1, t2, u);
                    result.Add(Evaluate(p0, p1, p2, p3, t0, t1, t2, t3, t));
                }
            }

            return result;
        }

        /// <summary>
        /// Resamples a polyline so that consecutive points are (approximately)
        /// <paramref name="spacing"/> metres apart. When <paramref name="closed"/>
        /// the gap between the last input point and the first is included, and
        /// the returned polyline is again open (closing point not repeated).
        /// </summary>
        public static List<Vector2> ResampleByArcLength(IReadOnlyList<Vector2> polyline, float spacing, bool closed)
        {
            if (polyline == null) throw new ArgumentNullException(nameof(polyline));
            if (polyline.Count < 2) throw new ArgumentException("Polyline needs at least 2 points.", nameof(polyline));
            if (spacing <= 0f) throw new ArgumentException("spacing must be > 0.", nameof(spacing));

            float total = PerimeterLength(polyline, closed);
            int count = Mathf.Max(2, Mathf.RoundToInt(total / spacing));
            float step = total / count;

            var result = new List<Vector2>(count);
            int segIndex = 0;
            float segStart = 0f;
            int lastPoint = closed ? polyline.Count : polyline.Count - 1;
            float segLen = SegmentLength(polyline, 0, closed);

            for (int i = 0; i < count; i++)
            {
                float target = i * step;
                while (segIndex < lastPoint - 1 && segStart + segLen < target)
                {
                    segStart += segLen;
                    segIndex++;
                    segLen = SegmentLength(polyline, segIndex, closed);
                }

                float localT = segLen > 1e-6f ? Mathf.Clamp01((target - segStart) / segLen) : 0f;
                Vector2 a = polyline[segIndex % polyline.Count];
                Vector2 b = polyline[(segIndex + 1) % polyline.Count];
                result.Add(Vector2.Lerp(a, b, localT));
            }

            return result;
        }

        /// <summary>Total length of a polyline, optionally closing the loop.</summary>
        public static float PerimeterLength(IReadOnlyList<Vector2> polyline, bool closed)
        {
            float total = 0f;
            int segs = closed ? polyline.Count : polyline.Count - 1;
            for (int i = 0; i < segs; i++)
                total += Vector2.Distance(polyline[i % polyline.Count], polyline[(i + 1) % polyline.Count]);
            return total;
        }

        private static float SegmentLength(IReadOnlyList<Vector2> polyline, int i, bool closed)
        {
            int segs = closed ? polyline.Count : polyline.Count - 1;
            if (i >= segs) return 0f;
            return Vector2.Distance(polyline[i % polyline.Count], polyline[(i + 1) % polyline.Count]);
        }

        private static Vector2 Evaluate(
            Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
            float t0, float t1, float t2, float t3, float t)
        {
            Vector2 a1 = Blend(p0, p1, t0, t1, t);
            Vector2 a2 = Blend(p1, p2, t1, t2, t);
            Vector2 a3 = Blend(p2, p3, t2, t3, t);
            Vector2 b1 = Blend(a1, a2, t0, t2, t);
            Vector2 b2 = Blend(a2, a3, t1, t3, t);
            return Blend(b1, b2, t1, t2, t);
        }

        private static Vector2 Blend(Vector2 a, Vector2 b, float ta, float tb, float t)
        {
            float denom = tb - ta;
            if (Mathf.Abs(denom) < MinKnotDelta) return a;
            float w = (tb - t) / denom;
            return w * a + (1f - w) * b;
        }

        private static float KnotDelta(Vector2 a, Vector2 b)
        {
            float d = Mathf.Pow(Vector2.Distance(a, b), Alpha);
            return Mathf.Max(d, MinKnotDelta);
        }

        private static int Mod(int x, int m) => ((x % m) + m) % m;
    }
}
