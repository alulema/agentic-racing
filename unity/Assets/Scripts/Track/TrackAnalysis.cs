using System;
using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>Tunables for corner detection and the reference racing line.</summary>
    [Serializable]
    public struct TrackAnalysisParams
    {
        /// <summary>Span (metres) over which signed curvature is measured.</summary>
        public float CurvatureStencil;
        /// <summary>Half-width (metres) of the moving average that smooths curvature.</summary>
        public float CurvatureSmoothing;
        /// <summary>A point counts as "cornering" when its radius is below this (metres).</summary>
        public float StraightRadius;
        /// <summary>Corners shorter than this arc length (metres) are discarded as noise.</summary>
        public float MinCornerArc;
        /// <summary>Corners that turn less than this (degrees) are discarded.</summary>
        public float MinCornerHeadingDeg;
        /// <summary>Two corner runs closer than this (metres) merge into one.</summary>
        public float MergeGap;

        /// <summary>Lateral clearance (metres) kept from each edge by the racing line.</summary>
        public float RacingLineMargin;
        /// <summary>Half-width (metres) of the moving average that smooths the racing line.</summary>
        public float RacingLineSmoothing;
        /// <summary>Smoothing passes over the racing-line offsets.</summary>
        public int RacingLineSmoothingPasses;

        public static TrackAnalysisParams Default => new TrackAnalysisParams
        {
            CurvatureStencil = 6f,
            CurvatureSmoothing = 8f,
            StraightRadius = 220f,
            MinCornerArc = 12f,
            MinCornerHeadingDeg = 14f,
            MergeGap = 14f,
            RacingLineMargin = 1.5f,
            RacingLineSmoothing = 18f,
            RacingLineSmoothingPasses = 3,
        };
    }

    /// <summary>
    /// Derives the numbered corners and a geometric reference racing line from a
    /// track centerline. Both are deterministic functions of the centerline, so
    /// they are deterministic for a given seed (CLAUDE.md Fase 1).
    ///
    /// The racing line is a simple geometry-based reference — outside on the
    /// approach, tight to the apex, unwinding on exit — not a lap-time optimum.
    /// </summary>
    public static class TrackAnalysis
    {
        /// <summary>
        /// Signed curvature per centerline sample. Positive = left-hand bend,
        /// negative = right-hand, in the XZ plane. Units: 1/metre.
        /// </summary>
        public static float[] SignedCurvature(IReadOnlyList<Vector3> loop, float spacing, TrackAnalysisParams p)
        {
            int n = loop.Count;
            int k = Mathf.Max(1, Mathf.RoundToInt(p.CurvatureStencil / spacing));
            var kappa = new float[n];

            for (int i = 0; i < n; i++)
            {
                Vector2 a = Flat(loop[((i - k) % n + n) % n]);
                Vector2 b = Flat(loop[i]);
                Vector2 c = Flat(loop[(i + k) % n]);

                float ab = Vector2.Distance(a, b);
                float bc = Vector2.Distance(b, c);
                float ca = Vector2.Distance(c, a);
                float cross = Cross(a, b, c);          // 2*signed area
                float denom = ab * bc * ca;
                kappa[i] = denom > 1e-4f ? (2f * cross) / denom : 0f;
            }

            return MovingAverage(kappa, Mathf.Max(1, Mathf.RoundToInt(p.CurvatureSmoothing / spacing)), true);
        }

        /// <summary>
        /// Detects the circuit's corners and numbers them 1..N from the
        /// start/finish line in lap direction.
        /// </summary>
        public static IReadOnlyList<TrackCorner> DetectCorners(
            IReadOnlyList<Vector3> centerline, float spacing, TrackAnalysisParams p)
        {
            int n = centerline.Count;
            float[] kappa = SignedCurvature(centerline, spacing, p);
            float kappaThreshold = 1f / p.StraightRadius;

            // Mark cornering samples, then walk the loop once collecting maximal runs.
            var cornering = new bool[n];
            for (int i = 0; i < n; i++) cornering[i] = Mathf.Abs(kappa[i]) >= kappaThreshold;

            var runs = new List<(int start, int end)>();
            int startIndex = -1;
            for (int i = 0; i < n; i++) if (!cornering[i]) { startIndex = i; break; }
            if (startIndex < 0)
            {
                // Whole loop is "cornering" (near-circular): treat as no discrete corners.
                return Array.Empty<TrackCorner>();
            }

            int runStart = -1;
            for (int step = 0; step <= n; step++)
            {
                int i = (startIndex + step) % n;
                bool inCorner = step < n && cornering[i];
                if (inCorner && runStart < 0) runStart = i;
                else if (!inCorner && runStart >= 0)
                {
                    runs.Add((runStart, (i - 1 + n) % n));
                    runStart = -1;
                }
            }

            MergeCloseRuns(runs, n, spacing, p.MergeGap);

            var corners = new List<TrackCorner>();
            int number = 1;
            foreach (var (rs, re) in runs)
            {
                int len = RunLength(rs, re, n);
                float arc = len * spacing;
                if (arc < p.MinCornerArc) continue;

                float headingDeg = HeadingChange(centerline, rs, re, n);
                if (headingDeg < p.MinCornerHeadingDeg) continue;

                int apex = rs;
                float maxAbs = 0f;
                float signSum = 0f;
                for (int s = 0; s < len; s++)
                {
                    int idx = (rs + s) % n;
                    float ak = Mathf.Abs(kappa[idx]);
                    signSum += kappa[idx];
                    if (ak > maxAbs) { maxAbs = ak; apex = idx; }
                }

                float minRadius = maxAbs > 1e-5f ? 1f / maxAbs : float.MaxValue;
                var dir = signSum >= 0f ? CornerDirection.Left : CornerDirection.Right;

                corners.Add(new TrackCorner(
                    number++, rs, apex, re,
                    rs * spacing, apex * spacing, re * spacing,
                    dir, headingDeg, minRadius));
            }

            return corners;
        }

        /// <summary>
        /// Builds a geometric reference racing line as an offset from the
        /// centerline: biased to the outside ahead of a corner, to the inside at
        /// the apex, unwinding after. Returned as world points (Y = 0), closed.
        /// </summary>
        public static IReadOnlyList<Vector3> BuildRacingLine(
            IReadOnlyList<Vector3> centerline, float trackWidth,
            IReadOnlyList<TrackCorner> corners, float spacing, TrackAnalysisParams p)
        {
            IReadOnlyList<Vector3> center = centerline;
            int n = center.Count;
            float maxOffset = trackWidth * 0.5f - p.RacingLineMargin;
            if (maxOffset < 0f) maxOffset = 0f;

            // Target lateral offset (+left / -right) per sample.
            var target = new float[n];
            foreach (var corner in corners)
            {
                float apexOffset = corner.Direction == CornerDirection.Left ? -maxOffset : maxOffset;
                float entryOffset = -apexOffset; // opposite side on the way in and out

                int approach = Mathf.RoundToInt(Mathf.Clamp(corner.MinRadius, 20f, 90f) / spacing);
                int half = Mathf.Max(1, RunLength(corner.StartSample, corner.EndSample, n) / 2);

                StampRamp(target, n, corner.StartSample - approach, corner.StartSample, entryOffset, entryOffset);
                StampRamp(target, n, corner.StartSample, corner.ApexSample, entryOffset, apexOffset);
                StampRamp(target, n, corner.ApexSample, corner.EndSample, apexOffset, entryOffset);
                StampRamp(target, n, corner.EndSample, corner.EndSample + approach, entryOffset, 0f);
                _ = half;
            }

            int window = Mathf.Max(1, Mathf.RoundToInt(p.RacingLineSmoothing / spacing));
            for (int pass = 0; pass < Mathf.Max(1, p.RacingLineSmoothingPasses); pass++)
                target = MovingAverage(target, window, true);

            var line = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 fwd = center[(i + 1) % n] - center[(i - 1 + n) % n];
                fwd.y = 0f;
                fwd.Normalize();
                Vector3 left = new Vector3(-fwd.z, 0f, fwd.x);
                float clamped = Mathf.Clamp(target[i], -maxOffset, maxOffset);
                line[i] = center[i] + left * clamped;
            }

            return line;
        }

        /// <summary>
        /// Centerline index at the middle of the longest straight (largest gap
        /// between one corner's end and the next corner's start). This is where
        /// the start/finish line belongs, so corner numbering starts on a
        /// straight and no corner straddles the line. Returns 0 if there are no
        /// corners.
        /// </summary>
        public static int LongestStraightMidpoint(IReadOnlyList<TrackCorner> corners, int sampleCount)
        {
            if (corners == null || corners.Count == 0) return 0;

            int n = sampleCount;
            int bestGap = -1;
            int bestMid = 0;
            for (int i = 0; i < corners.Count; i++)
            {
                int endSample = corners[i].EndSample;
                int nextStart = corners[(i + 1) % corners.Count].StartSample;
                int gap = ((nextStart - endSample) % n + n) % n;
                if (gap > bestGap)
                {
                    bestGap = gap;
                    bestMid = (endSample + gap / 2) % n;
                }
            }
            return bestMid;
        }

        /// <summary>
        /// Returns a copy of <paramref name="loop"/> rotated so that index
        /// <paramref name="newStart"/> becomes index 0. Order and spacing are
        /// preserved; the loop stays closed.
        /// </summary>
        public static List<Vector3> RotateLoop(IReadOnlyList<Vector3> loop, int newStart)
        {
            int n = loop.Count;
            int s = ((newStart % n) + n) % n;
            var rotated = new List<Vector3>(n);
            for (int i = 0; i < n; i++) rotated.Add(loop[(s + i) % n]);
            return rotated;
        }

        // --- helpers -----------------------------------------------------------

        private static void StampRamp(float[] arr, int n, int from, int to, float vFrom, float vTo)
        {
            int len = RunLength(((from % n) + n) % n, ((to % n) + n) % n, n);
            if (len <= 0) return;
            for (int s = 0; s <= len; s++)
            {
                int idx = ((from + s) % n + n) % n;
                float t = s / (float)len;
                float v = Mathf.Lerp(vFrom, vTo, t);
                // Keep the stronger bias where ramps overlap.
                if (Mathf.Abs(v) > Mathf.Abs(arr[idx])) arr[idx] = v;
            }
        }

        private static void MergeCloseRuns(List<(int start, int end)> runs, int n, float spacing, float mergeGap)
        {
            if (runs.Count < 2) return;
            int gapSamples = Mathf.RoundToInt(mergeGap / spacing);
            for (int i = runs.Count - 1; i >= 1; i--)
            {
                int gap = RunLength((runs[i - 1].end + 1) % n, (runs[i].start - 1 + n) % n, n);
                if (gap <= gapSamples)
                {
                    runs[i - 1] = (runs[i - 1].start, runs[i].end);
                    runs.RemoveAt(i);
                }
            }
        }

        private static int RunLength(int start, int end, int n) => ((end - start + n) % n) + 1;

        private static float HeadingChange(IReadOnlyList<Vector3> loop, int start, int end, int n)
        {
            Vector2 hIn = Flat(loop[(start + 1) % n]) - Flat(loop[start]);
            Vector2 hOut = Flat(loop[(end + 1) % n]) - Flat(loop[end]);
            if (hIn.sqrMagnitude < 1e-6f || hOut.sqrMagnitude < 1e-6f) return 0f;
            return Vector2.Angle(hIn, hOut);
        }

        private static float[] MovingAverage(float[] src, int halfWindow, bool wrap)
        {
            int n = src.Length;
            var dst = new float[n];
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int count = 0;
                for (int d = -halfWindow; d <= halfWindow; d++)
                {
                    int idx = wrap ? ((i + d) % n + n) % n : Mathf.Clamp(i + d, 0, n - 1);
                    sum += src[idx];
                    count++;
                }
                dst[i] = sum / count;
            }
            return dst;
        }

        private static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }
}
