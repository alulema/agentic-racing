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
        public void LapTracker_RaisesFinishedOnTargetLap()
        {
            TrackData track = TrackGenerator.Generate(4242);
            var go = new GameObject("lt");
            var carGo = new GameObject("car");
            try
            {
                var lt = go.AddComponent<LapTracker>();
                lt.Initialise(track, carGo.transform, laps: 3);

                int lapEvents = 0;
                bool finished = false;
                lt.LapCompleted += _ => lapEvents++;
                lt.RaceFinished += () => finished = true;

                // Drive the car transform straight through the line three times.
                Vector3 s = track.StartPosition;
                Vector3 dir = track.StartDirection;
                for (int i = 0; i < 3; i++)
                {
                    carGo.transform.position = s - dir * 6f;
                    lt.Tick();
                    carGo.transform.position = s + dir * 2f;
                    lt.Tick();
                    carGo.transform.position = s + dir * 25f;
                    lt.Tick();
                    carGo.transform.position = s - dir * 25f;
                    lt.Tick();
                }

                Assert.AreEqual(3, lapEvents, "one LapCompleted per loop");
                Assert.IsTrue(finished, "RaceFinished after the target lap");
                Assert.IsTrue(lt.Finished);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(carGo);
            }
        }
    }
}
