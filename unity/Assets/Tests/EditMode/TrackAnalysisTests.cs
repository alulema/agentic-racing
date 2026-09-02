using AgenticRacing.Track;
using NUnit.Framework;
using UnityEngine;

namespace AgenticRacing.Tests
{
    /// <summary>
    /// Fase 1 iteration 2: numbered corners and the reference racing line must be
    /// deterministic per seed, well-formed, and stay on the track.
    /// </summary>
    public sealed class TrackAnalysisTests
    {
        private static int[] Sweep()
        {
            var s = new int[100];
            for (int i = 0; i < s.Length; i++) s[i] = i + 1;
            return s;
        }

        [Test]
        public void Corners_AreDeterministicForASeed()
        {
            foreach (int seed in new[] { 1, 42, 12345, 777, 999983 })
            {
                var a = TrackGenerator.Generate(seed).Corners;
                var b = TrackGenerator.Generate(seed).Corners;

                Assert.AreEqual(a.Count, b.Count, $"seed {seed}: corner count diverged");
                for (int i = 0; i < a.Count; i++)
                {
                    Assert.AreEqual(a[i].Index, b[i].Index, $"seed {seed}: corner {i} index");
                    Assert.AreEqual(a[i].ApexSample, b[i].ApexSample, $"seed {seed}: corner {i} apex sample");
                    Assert.AreEqual(a[i].Direction, b[i].Direction, $"seed {seed}: corner {i} direction");
                    Assert.AreEqual(a[i].StartSample, b[i].StartSample, $"seed {seed}: corner {i} start");
                    Assert.AreEqual(a[i].EndSample, b[i].EndSample, $"seed {seed}: corner {i} end");
                }
            }
        }

        [Test]
        public void Corners_AreNumbered1ToN_InArcOrder()
        {
            foreach (int seed in Sweep())
            {
                var corners = TrackGenerator.Generate(seed).Corners;
                for (int i = 0; i < corners.Count; i++)
                {
                    Assert.AreEqual(i + 1, corners[i].Index, $"seed {seed}: corner {i} should be numbered {i + 1}");
                    if (i > 0)
                        Assert.GreaterOrEqual(corners[i].StartArc, corners[i - 1].StartArc - 1f,
                            $"seed {seed}: corners not in arc order at {i}");
                }
            }
        }

        [Test]
        public void EveryTrack_HasAtLeastTwoNumberedCorners()
        {
            foreach (int seed in Sweep())
            {
                var corners = TrackGenerator.Generate(seed).Corners;
                Assert.GreaterOrEqual(corners.Count, 2, $"seed {seed}: a closed circuit must have corners");
            }
        }

        [Test]
        public void EveryCorner_TurnsMeaningfully()
        {
            var ap = TrackAnalysisParams.Default;
            foreach (int seed in Sweep())
            {
                foreach (var corner in TrackGenerator.Generate(seed).Corners)
                {
                    Assert.GreaterOrEqual(corner.HeadingChangeDeg, ap.MinCornerHeadingDeg - 0.5f,
                        $"seed {seed}: corner {corner.Index} barely turns ({corner.HeadingChangeDeg:F1} deg)");
                    Assert.Greater(corner.MinRadius, 0f, $"seed {seed}: corner {corner.Index} radius");
                    Assert.LessOrEqual(corner.StartArc, corner.ApexArc + 1f, $"seed {seed}: corner {corner.Index} apex before start");
                    Assert.LessOrEqual(corner.ApexArc, corner.EndArc + 1f, $"seed {seed}: corner {corner.Index} end before apex");
                }
            }
        }

        [Test]
        public void RacingLine_MatchesCenterline_CountAndClosure()
        {
            foreach (int seed in Sweep())
            {
                TrackData t = TrackGenerator.Generate(seed);
                Assert.AreEqual(t.Centerline.Count, t.RacingLine.Count, $"seed {seed}: racing line count");

                float closing = Vector3.Distance(t.RacingLine[t.RacingLine.Count - 1], t.RacingLine[0]);
                Assert.Less(closing, 6f, $"seed {seed}: racing line does not close ({closing:F2} m)");
            }
        }

        [Test]
        public void RacingLine_StaysOnTrack()
        {
            foreach (int seed in Sweep())
            {
                TrackData t = TrackGenerator.Generate(seed);
                float half = t.Width * 0.5f;
                for (int i = 0; i < t.RacingLine.Count; i++)
                {
                    float lateral = Vector3.Distance(t.RacingLine[i], t.Centerline[i]);
                    Assert.LessOrEqual(lateral, half + 0.01f,
                        $"seed {seed}: racing line point {i} is {lateral:F2} m off center (half width {half:F2} m)");
                }
            }
        }

        [Test]
        public void RacingLine_IsDeterministicForASeed()
        {
            foreach (int seed in new[] { 3, 55, 12345, 424242 })
            {
                var a = TrackGenerator.Generate(seed).RacingLine;
                var b = TrackGenerator.Generate(seed).RacingLine;
                Assert.AreEqual(a.Count, b.Count, $"seed {seed}: racing line count diverged");
                for (int i = 0; i < a.Count; i++)
                {
                    Assert.AreEqual(a[i].x, b[i].x, 0f, $"seed {seed}: racing line x[{i}]");
                    Assert.AreEqual(a[i].z, b[i].z, 0f, $"seed {seed}: racing line z[{i}]");
                }
            }
        }
    }
}
