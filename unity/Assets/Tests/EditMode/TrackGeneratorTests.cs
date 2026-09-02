using System.Collections.Generic;
using AgenticRacing.Track;
using NUnit.Framework;
using UnityEngine;

namespace AgenticRacing.Tests
{
    /// <summary>
    /// Locks in the Fase 1 acceptance properties of the procedural circuit:
    /// determinism, a closed C1 seam, length in range, no self-intersection,
    /// a navigable tightest corner, and genuine variety across seeds.
    /// </summary>
    public sealed class TrackGeneratorTests
    {
        // A fixed sweep so a regression shows up on the same seeds every run.
        private static readonly int[] SweepSeeds = BuildSweep(1, 120);

        private static int[] BuildSweep(int start, int count)
        {
            var s = new int[count];
            for (int i = 0; i < count; i++) s[i] = start + i;
            return s;
        }

        [Test]
        public void SameSeed_ProducesIdenticalCenterline()
        {
            foreach (int seed in new[] { 1, 42, 12345, 999983 })
            {
                TrackData a = TrackGenerator.Generate(seed);
                TrackData b = TrackGenerator.Generate(seed);

                Assert.AreEqual(a.EffectiveSeed, b.EffectiveSeed, $"seed {seed}: effective seed diverged");
                Assert.AreEqual(a.Centerline.Count, b.Centerline.Count, $"seed {seed}: point count diverged");
                for (int i = 0; i < a.Centerline.Count; i++)
                {
                    Assert.AreEqual(a.Centerline[i].x, b.Centerline[i].x, 0f, $"seed {seed}: x[{i}] diverged");
                    Assert.AreEqual(a.Centerline[i].z, b.Centerline[i].z, 0f, $"seed {seed}: z[{i}] diverged");
                }
            }
        }

        [Test]
        public void Centerline_IsClosedLoop_WithContinuousTangentAtSeam()
        {
            var p = TrackParams.Default;
            foreach (int seed in SweepSeeds)
            {
                TrackData t = TrackGenerator.Generate(seed, p);
                var c = t.Centerline;
                int n = c.Count;

                // Spacing between the last point and the first must match the
                // resample spacing, i.e. the loop closes without a gap or overlap.
                float closingGap = Vector3.Distance(c[n - 1], c[0]);
                Assert.LessOrEqual(closingGap, p.CenterlineSpacing * 2f,
                    $"seed {seed}: closing gap {closingGap:F2} m too large");

                // Heading just before the seam vs just after: no kink.
                Vector3 before = (c[0] - c[n - 1]).normalized;
                Vector3 after = (c[1] - c[0]).normalized;
                float turnDeg = Vector3.Angle(before, after);
                Assert.Less(turnDeg, 12f, $"seed {seed}: {turnDeg:F1} deg kink at the start/finish seam");
            }
        }

        [Test]
        public void Length_IsWithinConfiguredRange()
        {
            var p = TrackParams.Default;
            foreach (int seed in SweepSeeds)
            {
                TrackData t = TrackGenerator.Generate(seed, p);
                Assert.GreaterOrEqual(t.Length, p.MinLength - 1f, $"seed {seed}: too short ({t.Length:F0} m)");
                Assert.LessOrEqual(t.Length, p.MaxLength + 1f, $"seed {seed}: too long ({t.Length:F0} m)");
            }
        }

        [Test]
        public void Centerline_DoesNotSelfIntersect()
        {
            foreach (int seed in SweepSeeds)
            {
                TrackData t = TrackGenerator.Generate(seed);
                Assert.IsFalse(SelfIntersects(t.Centerline), $"seed {seed}: centerline self-intersects");
            }
        }

        [Test]
        public void TightestCorner_IsNavigable()
        {
            var p = TrackParams.Default;
            foreach (int seed in SweepSeeds)
            {
                TrackData t = TrackGenerator.Generate(seed, p);
                Assert.GreaterOrEqual(t.MinCornerRadius, p.MinCornerRadius,
                    $"seed {seed}: tightest corner {t.MinCornerRadius:F1} m below limit");
            }
        }

        [Test]
        public void DistinctSeeds_ProduceDistinctTracks()
        {
            var signatures = new HashSet<string>();
            foreach (int seed in SweepSeeds)
            {
                TrackData t = TrackGenerator.Generate(seed);
                // Coarse fingerprint: length + a few sampled points.
                var sig = $"{t.Length:F1}|{Sample(t, 0)}|{Sample(t, 0.25f)}|{Sample(t, 0.5f)}|{Sample(t, 0.75f)}";
                Assert.IsTrue(signatures.Add(sig), $"seed {seed}: produced a track already seen from another seed");
            }
        }

        [Test]
        public void Fallback_WhenTriggered_IsDeterministicAndStillValid()
        {
            // A 22 m minimum corner radius sits above the observed ~13 m floor
            // (median ~35 m), so a minority of seeds fail on the first attempt
            // and exercise the deterministic re-derivation path, while a valid
            // layout stays common enough to be found quickly.
            var hard = TrackParams.Default;
            hard.MinCornerRadius = 22f;
            hard.MaxAttempts = 128;

            int fellBack = 0;
            for (int seed = 1; seed <= 60; seed++)
            {
                TrackData a = TrackGenerator.Generate(seed, hard);
                TrackData b = TrackGenerator.Generate(seed, hard);

                Assert.AreEqual(a.EffectiveSeed, b.EffectiveSeed, $"seed {seed}: derived seed not reproducible");
                Assert.AreEqual(a.Attempts, b.Attempts, $"seed {seed}: attempt count not reproducible");
                Assert.GreaterOrEqual(a.MinCornerRadius, hard.MinCornerRadius,
                    $"seed {seed}: generator returned a track below the required corner radius");
                Assert.AreEqual(a.Centerline.Count, b.Centerline.Count, $"seed {seed}: point count not reproducible");
                for (int i = 0; i < a.Centerline.Count; i++)
                    Assert.AreEqual(a.Centerline[i], b.Centerline[i], $"seed {seed}: centerline[{i}] not reproducible");

                if (a.Attempts > 1) fellBack++;
            }

            Assert.Greater(fellBack, 0, "expected at least one seed in 1..60 to need a deterministic fallback at 22 m");
        }

        private static string Sample(TrackData t, float frac)
        {
            int i = Mathf.Clamp(Mathf.RoundToInt(frac * (t.Centerline.Count - 1)), 0, t.Centerline.Count - 1);
            Vector3 v = t.Centerline[i];
            return $"{v.x:F1},{v.z:F1}";
        }

        private static bool SelfIntersects(IReadOnlyList<Vector3> loop)
        {
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a1 = new Vector2(loop[i].x, loop[i].z);
                Vector2 a2 = new Vector2(loop[(i + 1) % n].x, loop[(i + 1) % n].z);
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    Vector2 b1 = new Vector2(loop[j].x, loop[j].z);
                    Vector2 b2 = new Vector2(loop[(j + 1) % n].x, loop[(j + 1) % n].z);
                    if (Cross(b1, b2, a1) * Cross(b1, b2, a2) < 0f &&
                        Cross(a1, a2, b1) * Cross(a1, a2, b2) < 0f)
                        return true;
                }
            }
            return false;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }
}
