using AgenticRacing.Track;
using AgenticRacing.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace AgenticRacing.Tests
{
    /// <summary>
    /// Fase 1 iteration 3: the start/finish detector must count exactly one lap
    /// per loop, ignore the finish plane elsewhere on the circuit, and not
    /// double-count while the car sits near the line.
    /// </summary>
    public sealed class LapDetectorTests
    {
        private static readonly Vector3 Start = new Vector3(100f, 0f, 50f);
        private static readonly Vector3 Dir = Vector3.forward;

        [Test]
        public void CrossingForwardNearLine_CountsOneLap()
        {
            var d = new LapDetector(Start, Dir, triggerRadius: 12f);

            Assert.IsFalse(d.Tick(Start - Dir * 5f), "approaching, no lap yet");
            Assert.IsFalse(d.Tick(Start - Dir * 1f), "still behind the line");
            Assert.IsTrue(d.Tick(Start + Dir * 1f), "crossed forward -> lap");
            Assert.AreEqual(1, d.LapsCompleted);
            Assert.AreEqual(2, d.CurrentLap);
        }

        [Test]
        public void CrossingThePlaneFarFromTheLine_DoesNotCount()
        {
            var d = new LapDetector(Start, Dir, triggerRadius: 12f);
            // Same forward crossing but 40 m to the side (elsewhere on a twisty track).
            Vector3 off = new Vector3(40f, 0f, 0f);
            d.Tick(Start + off - Dir * 3f);
            Assert.IsFalse(d.Tick(Start + off + Dir * 3f), "off to the side: not a lap");
            Assert.AreEqual(0, d.LapsCompleted);
        }

        [Test]
        public void SittingOnTheLine_DoesNotRepeatedlyCount()
        {
            var d = new LapDetector(Start, Dir, triggerRadius: 12f);
            d.Tick(Start - Dir * 2f);
            Assert.IsTrue(d.Tick(Start + Dir * 0.5f), "first crossing");
            // Jitter back and forth right on the line.
            for (int i = 0; i < 20; i++)
            {
                d.Tick(Start + Dir * 0.2f);
                d.Tick(Start - Dir * 0.2f);
            }
            Assert.AreEqual(1, d.LapsCompleted, "jitter must not add laps (needs re-arm behind the line)");
        }

        [Test]
        public void FullLoops_CountOncePerLap()
        {
            var d = new LapDetector(Start, Dir, triggerRadius: 12f);
            int expected = 0;

            for (int lap = 0; lap < 5; lap++)
            {
                // behind -> across -> far away around the loop -> back behind
                d.Tick(Start - Dir * 6f);
                bool counted = d.Tick(Start + Dir * 2f);
                if (lap == 0) Assert.IsTrue(counted);
                expected++;
                d.Tick(Start + Dir * 30f);
                d.Tick(Start + new Vector3(0f, 0f, 200f));   // opposite side of the circuit
                d.Tick(Start - Dir * 20f);                    // re-arm well behind the line
                Assert.AreEqual(expected, d.LapsCompleted, $"after loop {lap + 1}");
            }
        }

        [Test]
        public void LapTracker_CountsLapsByDrivingTheCenterline()
        {
            TrackData track = TrackGenerator.Generate(4242);
            var go = new GameObject("lt");
            var carGo = new GameObject("car");
            try
            {
                var lt = go.AddComponent<LapTracker>();
                carGo.transform.position = track.Centerline[0];
                lt.Initialise(track, carGo.transform, laps: 3);

                int lapEvents = 0;
                bool finished = false;
                lt.LapCompleted += _ => lapEvents++;
                lt.RaceFinished += () => finished = true;

                var center = track.Centerline;
                // Drive around the centerline three and a bit times. Step by a few
                // samples per Tick, like a moving car.
                for (int lap = 0; lap < 3; lap++)
                {
                    for (int i = 0; i < center.Count; i += 3)
                    {
                        carGo.transform.position = center[i];
                        lt.Tick();
                    }
                }
                // A little past the line to close the third lap's wrap.
                for (int i = 0; i < 30; i += 3)
                {
                    carGo.transform.position = center[i];
                    lt.Tick();
                }

                Assert.AreEqual(3, lapEvents, "one LapCompleted per loop of the centerline");
                Assert.AreEqual(3, lt.LapsCompleted);
                Assert.IsTrue(finished, "RaceFinished after the target lap");
                Assert.IsTrue(lt.Finished);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(carGo);
            }
        }

        [Test]
        public void LapTracker_DoesNotCountWithoutGoingMostOfTheWayRound()
        {
            TrackData track = TrackGenerator.Generate(99);
            var go = new GameObject("lt");
            var carGo = new GameObject("car");
            try
            {
                var lt = go.AddComponent<LapTracker>();
                carGo.transform.position = track.Centerline[0];
                lt.Initialise(track, carGo.transform, laps: 3);

                int lapEvents = 0;
                lt.LapCompleted += _ => lapEvents++;

                var center = track.Centerline;
                int quarter = center.Count / 4;
                // Nudge forward a quarter lap and back to the line a few times.
                for (int rep = 0; rep < 4; rep++)
                {
                    for (int i = 0; i <= quarter; i += 3) { carGo.transform.position = center[i]; lt.Tick(); }
                    for (int i = quarter; i >= 0; i -= 3) { carGo.transform.position = center[i]; lt.Tick(); }
                }

                Assert.AreEqual(0, lapEvents, "quarter-lap shuffles must not count as laps");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(carGo);
            }
        }
    }
}
