using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Tunable inputs for <see cref="TrackGenerator"/>. <see cref="Default"/>
    /// targets a ~2 km circuit; the generator scales any concrete layout into
    /// <see cref="MinLength"/>..<see cref="MaxLength"/> afterwards.
    /// </summary>
    [Serializable]
    public struct TrackParams
    {
        public int MinControlPoints;
        public int MaxControlPoints;
        public float BaseRadius;        // metres, before shaping
        public int MinHarmonics;       // low-frequency radial lobes (creates straights + corners)
        public int MaxHarmonics;
        public int MinHarmonicFreq;    // lobe count per harmonic, e.g. 2
        public int MaxHarmonicFreq;    // e.g. 5
        public float HarmonicAmpMin;   // per-harmonic amplitude, fraction of BaseRadius
        public float HarmonicAmpMax;
        public float RadialJitterMin;   // small per-point local jitter, fraction of BaseRadius
        public float RadialJitterMax;
        public float RadiusClampMin;    // hard clamp on shaped radius, fraction of BaseRadius
        public float RadiusClampMax;
        public float AngularJitter;     // fraction of the even angular step, [0..1)
        public float MinLength;         // metres  (CLAUDE.md Fase 1: 1500)
        public float MaxLength;         // metres  (CLAUDE.md Fase 1: 2500)
        public float MinCornerRadius;   // metres; tighter than this => regenerate
        public float CenterlineSpacing; // metres between resampled centerline points
        public float CurvatureStencil;  // metres; span used to measure corner radius
        public float TrackWidth;        // metres, full width of the drivable ribbon
        public int SamplesPerSegment;   // spline density before arc-length resample
        public int MaxAttempts;         // deterministic seed re-derivations before giving up

        public static TrackParams Default => new TrackParams
        {
            MinControlPoints = 16,
            MaxControlPoints = 22,
            BaseRadius = 300f,
            MinHarmonics = 2,
            MaxHarmonics = 3,
            MinHarmonicFreq = 2,
            MaxHarmonicFreq = 5,
            HarmonicAmpMin = 0.12f,
            HarmonicAmpMax = 0.30f,
            RadialJitterMin = -0.05f,
            RadialJitterMax = 0.05f,
            RadiusClampMin = 0.45f,
            RadiusClampMax = 1.75f,
            AngularJitter = 0.35f,
            MinLength = 1500f,
            MaxLength = 2500f,
            MinCornerRadius = 12f,
            CenterlineSpacing = 2f,
            CurvatureStencil = 6f,
            TrackWidth = 12f,
            SamplesPerSegment = 120,
            MaxAttempts = 40,
        };
    }

    /// <summary>Immutable result of one track generation.</summary>
    public sealed class TrackData
    {
        /// <summary>Seed the caller asked for.</summary>
        public int RequestedSeed { get; }

        /// <summary>
        /// Seed actually used. Differs from <see cref="RequestedSeed"/> only when
        /// the requested layout failed validation and the generator fell back to
        /// a deterministically derived seed (see <see cref="Attempts"/>).
        /// </summary>
        public int EffectiveSeed { get; }

        /// <summary>1 = the requested seed was valid; N = N-1 re-derivations.</summary>
        public int Attempts { get; }

        /// <summary>
        /// Evenly spaced points around the closed centerline, in the XZ plane
        /// (Y = 0). The loop is implicit: <c>Centerline[Count-1]</c> connects back
        /// to <c>Centerline[0]</c>; the first point is not repeated at the end.
        /// </summary>
        public IReadOnlyList<Vector3> Centerline { get; }

        /// <summary>Total centerline length in metres, within params' range.</summary>
        public float Length { get; }

        /// <summary>Smallest corner radius found along the loop, in metres.</summary>
        public float MinCornerRadius { get; }

        /// <summary>Full drivable width of the ribbon, in metres.</summary>
        public float Width { get; }

        /// <summary>
        /// Numbered corners, 1..N from the start/finish line in lap direction.
        /// Deterministic for a given seed. Populated by <see cref="TrackAnalysis"/>.
        /// </summary>
        public IReadOnlyList<TrackCorner> Corners { get; }

        /// <summary>
        /// Geometric reference racing line as world points (Y = 0), same count and
        /// ordering as <see cref="Centerline"/>, closed. Not a lap-time optimum.
        /// </summary>
        public IReadOnlyList<Vector3> RacingLine { get; }

        /// <summary>World position of the start/finish line (== Centerline[0]).</summary>
        public Vector3 StartPosition => Centerline[0];

        /// <summary>Unit tangent at the start/finish line, pointing along lap direction.</summary>
        public Vector3 StartDirection { get; }

        internal TrackData(int requestedSeed, int effectiveSeed, int attempts,
            IReadOnlyList<Vector3> centerline, float length, float minCornerRadius,
            float width, Vector3 startDirection,
            IReadOnlyList<TrackCorner> corners, IReadOnlyList<Vector3> racingLine)
        {
            RequestedSeed = requestedSeed;
            EffectiveSeed = effectiveSeed;
            Attempts = attempts;
            Centerline = centerline;
            Length = length;
            MinCornerRadius = minCornerRadius;
            Width = width;
            StartDirection = startDirection;
            Corners = corners;
            RacingLine = racingLine;
        }
    }

    /// <summary>
    /// Deterministic procedural generator for a closed race circuit
    /// (CLAUDE.md Fase 1). A given seed always yields an identical track:
    /// randomness comes solely from <see cref="System.Random"/> seeded with the
    /// integer seed, never from <see cref="UnityEngine.Random"/> (CLAUDE.md §10).
    /// </summary>
    public static class TrackGenerator
    {
        public static TrackData Generate(int seed) => Generate(seed, TrackParams.Default);

        public static TrackData Generate(int seed, TrackParams p)
        {
            ValidateParams(p);

            int effectiveSeed = seed;
            var rejections = new List<string>();

            for (int attempt = 1; attempt <= p.MaxAttempts; attempt++)
            {
                if (TryBuild(seed, effectiveSeed, attempt, p, out TrackData data, out string reason))
                {
                    if (attempt > 1)
                    {
                        // One line, not one-per-retry: the requested seed was not
                        // directly navigable, so a deterministically derived seed
                        // was used (CLAUDE.md Fase 1: "re-genera con seed derivada
                        // de forma determinista ... y déjalo registrado").
                        Debug.Log($"[TrackGenerator] seed {seed} not directly navigable; used derived seed " +
                                  $"{effectiveSeed} after {attempt} attempts (rejections: {Summarize(rejections)}).");
                    }
                    return data;
                }

                rejections.Add(reason);
                effectiveSeed = SplitMix32(unchecked((uint)effectiveSeed));
            }

            throw new InvalidOperationException(
                $"TrackGenerator could not produce a valid circuit for seed {seed} within {p.MaxAttempts} " +
                $"attempts (rejections: {Summarize(rejections)}).");
        }

        private static bool TryBuild(int requestedSeed, int effectiveSeed, int attempt, TrackParams p,
            out TrackData data, out string reason)
        {
            data = null;
            var rng = new System.Random(effectiveSeed);

            // Low-frequency radial harmonics give the loop real shape — long
            // straights between lobes, distinct corners at the transitions —
            // instead of the near-circular blob that plain per-point jitter
            // produces (CLAUDE.md Fase 1: curvas numeradas que sean referentes).
            int harmonics = rng.Next(p.MinHarmonics, p.MaxHarmonics + 1);
            var freq = new int[harmonics];
            var amp = new float[harmonics];
            var phase = new float[harmonics];
            for (int h = 0; h < harmonics; h++)
            {
                freq[h] = rng.Next(p.MinHarmonicFreq, p.MaxHarmonicFreq + 1);
                amp[h] = Mathf.Lerp(p.HarmonicAmpMin, p.HarmonicAmpMax, (float)rng.NextDouble());
                phase[h] = (float)rng.NextDouble() * Mathf.PI * 2f;
            }

            int controlCount = rng.Next(p.MinControlPoints, p.MaxControlPoints + 1);
            var control = new List<Vector2>(controlCount);
            float step = Mathf.PI * 2f / controlCount;
            for (int i = 0; i < controlCount; i++)
            {
                // Angle stays monotonic: even slot + bounded jitter that cannot
                // reach the next slot, so the loop never folds back on itself.
                float angle = i * step + ((float)rng.NextDouble() - 0.5f) * step * p.AngularJitter;

                float modulation = 0f;
                for (int h = 0; h < harmonics; h++)
                    modulation += amp[h] * Mathf.Sin(angle * freq[h] + phase[h]);
                modulation += Mathf.Lerp(p.RadialJitterMin, p.RadialJitterMax, (float)rng.NextDouble());

                float radius = p.BaseRadius * Mathf.Clamp(1f + modulation, p.RadiusClampMin, p.RadiusClampMax);
                control.Add(new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius));
            }

            var fine = CatmullRomSpline.SampleClosed(control, p.SamplesPerSegment);

            // Scale the whole layout so the centerline length lands in range.
            float rawLength = CatmullRomSpline.PerimeterLength(fine, closed: true);
            float scale = 1f;
            if (rawLength < p.MinLength) scale = p.MinLength / rawLength;
            else if (rawLength > p.MaxLength) scale = p.MaxLength / rawLength;
            if (!Mathf.Approximately(scale, 1f))
            {
                for (int i = 0; i < fine.Count; i++) fine[i] *= scale;
            }

            var centerline2D = CatmullRomSpline.ResampleByArcLength(fine, p.CenterlineSpacing, closed: true);
            float length = CatmullRomSpline.PerimeterLength(centerline2D, closed: true);

            if (length < p.MinLength - 1f || length > p.MaxLength + 1f)
            {
                reason = "length";
                return false;
            }

            if (HasSelfIntersection(centerline2D))
            {
                reason = "self-intersection";
                return false;
            }

            float minRadius = MinCornerRadius(centerline2D, p.CenterlineSpacing, p.CurvatureStencil);
            if (minRadius < p.MinCornerRadius)
            {
                reason = "corner-radius";
                return false;
            }

            var rawCenterline = new List<Vector3>(centerline2D.Count);
            foreach (var v in centerline2D) rawCenterline.Add(new Vector3(v.x, 0f, v.y));

            var analysis = TrackAnalysisParams.Default;

            // Put the start/finish line on the longest straight and number the
            // corners from there (CLAUDE.md Fase 1). Detect once to find that
            // straight, rotate the loop so its midpoint is index 0, then detect
            // again so corners are numbered 1..N from the line with none
            // straddling it.
            var provisionalCorners = TrackAnalysis.DetectCorners(rawCenterline, p.CenterlineSpacing, analysis);
            int startIndex = TrackAnalysis.LongestStraightMidpoint(provisionalCorners, rawCenterline.Count);
            var centerline3D = TrackAnalysis.RotateLoop(rawCenterline, startIndex);

            var corners = TrackAnalysis.DetectCorners(centerline3D, p.CenterlineSpacing, analysis);
            var racingLine = TrackAnalysis.BuildRacingLine(
                centerline3D, p.TrackWidth, corners, p.CenterlineSpacing, analysis);

            Vector3 startDir = (centerline3D[1] - centerline3D[0]).normalized;

            data = new TrackData(requestedSeed, effectiveSeed, attempt, centerline3D, length, minRadius,
                p.TrackWidth, startDir, corners, racingLine);
            reason = null;
            return true;
        }

        /// <summary>
        /// True if any pair of non-adjacent centerline segments cross. O(n^2) over
        /// ~1000 segments; only runs at generation time, not per frame.
        /// </summary>
        private static bool HasSelfIntersection(IReadOnlyList<Vector2> loop)
        {
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a1 = loop[i];
                Vector2 a2 = loop[(i + 1) % n];
                // Skip this segment's two neighbours (they share an endpoint).
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // wrap-around neighbour
                    Vector2 b1 = loop[j];
                    Vector2 b2 = loop[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float d1 = Cross(p3, p4, p1);
            float d2 = Cross(p3, p4, p2);
            float d3 = Cross(p1, p2, p3);
            float d4 = Cross(p1, p2, p4);

            if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
                ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
                return true;

            return false; // ignore collinear/touching: adjacent segments touch by design
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        /// <summary>
        /// Smallest osculating-circle radius along the loop. Curvature is measured
        /// over a stencil spanning ~<paramref name="stencilMetres"/> (points
        /// <c>k</c> steps apart, where one step is <paramref name="spacing"/> m),
        /// not between adjacent points: a tight stencil would read the piecewise-
        /// linear resampling as spurious hairpins.
        /// </summary>
        private static float MinCornerRadius(IReadOnlyList<Vector2> loop, float spacing, float stencilMetres)
        {
            int n = loop.Count;
            int k = Mathf.Max(1, Mathf.RoundToInt(stencilMetres / spacing));
            float minRadius = float.MaxValue;

            for (int i = 0; i < n; i++)
            {
                Vector2 a = loop[((i - k) % n + n) % n];
                Vector2 b = loop[i];
                Vector2 c = loop[(i + k) % n];

                float ab = Vector2.Distance(a, b);
                float bc = Vector2.Distance(b, c);
                float ca = Vector2.Distance(c, a);
                float area = Mathf.Abs(Cross(a, b, c)) * 0.5f;
                if (area < 1e-4f) continue; // effectively straight over this stencil

                float radius = (ab * bc * ca) / (4f * area);
                if (radius < minRadius) minRadius = radius;
            }

            return minRadius;
        }

        private static string Summarize(List<string> reasons)
        {
            if (reasons.Count == 0) return "none";
            return string.Join(", ", reasons
                .GroupBy(r => r)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}x{g.Count()}"));
        }

        /// <summary>Finalising step of SplitMix; deterministic int -> int hash.</summary>
        private static int SplitMix32(uint x)
        {
            x += 0x9E3779B9u;
            x = (x ^ (x >> 16)) * 0x21F0AAADu;
            x = (x ^ (x >> 15)) * 0x735A2D97u;
            x ^= x >> 15;
            return unchecked((int)x);
        }

        private static void ValidateParams(TrackParams p)
        {
            if (p.MinControlPoints < 4)
                throw new ArgumentException("MinControlPoints must be >= 4 for a closed Catmull-Rom loop.");
            if (p.MaxControlPoints < p.MinControlPoints)
                throw new ArgumentException("MaxControlPoints must be >= MinControlPoints.");
            if (p.BaseRadius <= 0f) throw new ArgumentException("BaseRadius must be > 0.");
            if (p.MinHarmonics < 1 || p.MaxHarmonics < p.MinHarmonics)
                throw new ArgumentException("Require 1 <= MinHarmonics <= MaxHarmonics.");
            if (p.MinHarmonicFreq < 1 || p.MaxHarmonicFreq < p.MinHarmonicFreq)
                throw new ArgumentException("Require 1 <= MinHarmonicFreq <= MaxHarmonicFreq.");
            if (p.RadiusClampMin <= 0f || p.RadiusClampMax <= p.RadiusClampMin)
                throw new ArgumentException("Require 0 < RadiusClampMin < RadiusClampMax.");
            if (p.MinLength <= 0f || p.MaxLength <= p.MinLength)
                throw new ArgumentException("Require 0 < MinLength < MaxLength.");
            if (p.CenterlineSpacing <= 0f) throw new ArgumentException("CenterlineSpacing must be > 0.");
            if (p.CurvatureStencil < p.CenterlineSpacing)
                throw new ArgumentException("CurvatureStencil must be >= CenterlineSpacing.");
            if (p.TrackWidth <= 0f) throw new ArgumentException("TrackWidth must be > 0.");
            if (p.SamplesPerSegment < 1) throw new ArgumentException("SamplesPerSegment must be >= 1.");
            if (p.MaxAttempts < 1) throw new ArgumentException("MaxAttempts must be >= 1.");
        }
    }
}
