using System;
using AgenticRacing.Track;
using UnityEngine;

namespace AgenticRacing.Vehicle
{
    /// <summary>
    /// Wires a <see cref="LapDetector"/> to a car transform and a
    /// <see cref="TrackData"/>, counts laps against a target, and reports
    /// normalised progress around the loop. The HUD is DOM in the real demo
    /// (CLAUDE.md §2.2); this component just owns the state and raises events.
    /// </summary>
    public sealed class LapTracker : MonoBehaviour
    {
        [SerializeField] private Transform car;
        [SerializeField, Min(1)] private int totalLaps = 5;
        [SerializeField] private float triggerRadius = 12f;

        private LapDetector _detector;
        private TrackData _track;
        private int _nearestSample;

        /// <summary>Fires with the just-completed lap number (1-based).</summary>
        public event Action<int> LapCompleted;
        /// <summary>Fires once when the final lap is completed.</summary>
        public event Action RaceFinished;

        public int CurrentLap => _detector?.CurrentLap ?? 1;
        public int LapsCompleted => _detector?.LapsCompleted ?? 0;
        public int TotalLaps => totalLaps;
        public bool Finished { get; private set; }

        /// <summary>0..1 position around the centerline from the start/finish line.</summary>
        public float Progress01 { get; private set; }

        public void Initialise(TrackData track, Transform carTransform, int laps)
        {
            _track = track;
            car = carTransform;
            totalLaps = Mathf.Max(1, laps);
            _detector = new LapDetector(track.StartPosition, track.StartDirection, triggerRadius);
            _nearestSample = 0;
            Finished = false;
        }

        private void Awake()
        {
            // Allow drag-and-drop wiring in a hand-built scene too.
            if (_track == null)
            {
                var builder = FindAnyObjectByType<TrackBuilder>();
                if (builder != null && builder.Data != null)
                    Initialise(builder.Data, car, totalLaps);
            }
        }

        private void FixedUpdate() => Tick();

        /// <summary>
        /// Advances lap detection and progress by one physics step. Called from
        /// <see cref="FixedUpdate"/>; public so tests can step it deterministically.
        /// </summary>
        public void Tick()
        {
            if (_detector == null || _track == null || car == null) return;

            Vector3 pos = car.position;
            if (_detector.Tick(pos))
            {
                int lap = _detector.LapsCompleted;
                LapCompleted?.Invoke(lap);
                Debug.Log($"[LapTracker] lap {lap}/{totalLaps} completed.");

                if (lap >= totalLaps && !Finished)
                {
                    Finished = true;
                    RaceFinished?.Invoke();
                    Debug.Log("[LapTracker] race finished.");
                }
            }

            UpdateProgress(pos);
        }

        private void UpdateProgress(Vector3 pos)
        {
            var center = _track.Centerline;
            int n = center.Count;

            // Track the nearest sample within a moving window so this stays O(1)
            // instead of scanning the whole ~1000-point centerline each step.
            const int window = 40;
            int best = _nearestSample;
            float bestSqr = (center[best] - pos).sqrMagnitude;
            for (int d = -window; d <= window; d++)
            {
                int i = ((_nearestSample + d) % n + n) % n;
                float sqr = (center[i] - pos).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = i; }
            }
            _nearestSample = best;
            Progress01 = best / (float)n;
        }
    }
}
