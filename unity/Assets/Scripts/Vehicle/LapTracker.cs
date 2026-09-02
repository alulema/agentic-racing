using System;
using AgenticRacing.Track;
using UnityEngine;

namespace AgenticRacing.Vehicle
{
    /// <summary>
    /// Counts laps for a car against a <see cref="TrackData"/> and reports
    /// normalised progress around the loop. The HUD is DOM in the real demo
    /// (CLAUDE.md §2.2); this component just owns the state and raises events.
    ///
    /// A lap is detected by <b>progress wrap</b>: the nearest centerline sample
    /// index, normalised to 0..1 from the start/finish line, has to climb past a
    /// good fraction of the loop and then jump back down near zero. This is
    /// robust on a twisty circuit, unlike a start-line plane crossing which is
    /// sensitive to how the straight is oriented. A <see cref="LapDetector"/>
    /// plane test runs alongside as a cross-check and only logs when it disagrees.
    /// </summary>
    public sealed class LapTracker : MonoBehaviour
    {
        [SerializeField] private Transform car;
        [SerializeField, Min(1)] private int totalLaps = 5;
        [SerializeField] private float triggerRadius = 25f;

        [Tooltip("Progress the car must reach before a wrap counts as a lap.")]
        [SerializeField, Range(0.5f, 0.95f)] private float lapArmProgress = 0.65f;
        [Tooltip("Progress the car must drop below to complete the wrap.")]
        [SerializeField, Range(0.02f, 0.3f)] private float lapWrapProgress = 0.15f;

        private LapDetector _detector;
        private TrackData _track;
        private int _nearestSample;
        private float _maxProgressThisLap;
        private bool _lapArmed;

        /// <summary>Fires with the just-completed lap number (1-based).</summary>
        public event Action<int> LapCompleted;
        /// <summary>Fires once when the final lap is completed.</summary>
        public event Action RaceFinished;

        public int LapsCompleted { get; private set; }
        public int CurrentLap => LapsCompleted + 1;
        public int TotalLaps => totalLaps;
        public bool Finished { get; private set; }

        /// <summary>0..1 position around the centerline from the start/finish line.</summary>
        public float Progress01 { get; private set; }

        /// <summary>Internal state for an on-screen debug readout.</summary>
        public string DetectorDebug =>
            $"prog={Progress01:F2}  max={_maxProgressThisLap:F2}  armed={_lapArmed}  " +
            $"plane[{(_detector == null ? "-" : $"ahead={_detector.LastAhead:F0} laps={_detector.LapsCompleted}")}]";

        public void Initialise(TrackData track, Transform carTransform, int laps)
        {
            _track = track;
            car = carTransform;
            totalLaps = Mathf.Max(1, laps);
            _detector = new LapDetector(track.StartPosition, track.StartDirection, triggerRadius);

            // Seed the nearest sample from the car's current position so the
            // windowed search starts locked on, not at index 0.
            _nearestSample = NearestSampleFullScan(carTransform != null ? carTransform.position : track.StartPosition);
            Progress01 = _nearestSample / (float)track.Centerline.Count;
            _maxProgressThisLap = Progress01;
            _lapArmed = false;
            LapsCompleted = 0;
            Finished = false;
        }

        private void Awake()
        {
            if (_track == null)
            {
                var builder = FindAnyObjectByType<TrackBuilder>();
                if (builder != null && builder.Data != null)
                    Initialise(builder.Data, car, totalLaps);
            }
        }

        private void FixedUpdate() => Tick();

        /// <summary>
        /// Advances lap detection and progress by one step. Called from
        /// <see cref="FixedUpdate"/>; public so tests can step it deterministically.
        /// </summary>
        public void Tick()
        {
            if (_track == null || car == null) return;

            Vector3 pos = car.position;
            float prev = Progress01;
            UpdateProgress(pos);

            if (Progress01 > _maxProgressThisLap) _maxProgressThisLap = Progress01;
            if (_maxProgressThisLap >= lapArmProgress) _lapArmed = true;

            // Wrap: progress was high last step and has dropped near zero.
            bool wrapped = _lapArmed && prev > 0.5f && Progress01 < lapWrapProgress;
            if (wrapped) CompleteLap();

            _detector?.Tick(pos);
            if (_detector != null && _detector.LapsCompleted != LapsCompleted)
                Debug.Log($"[LapTracker] plane detector ({_detector.LapsCompleted}) disagrees with " +
                          $"progress detector ({LapsCompleted}).");
        }

        private void CompleteLap()
        {
            LapsCompleted++;
            _maxProgressThisLap = Progress01;
            _lapArmed = false;

            LapCompleted?.Invoke(LapsCompleted);
            Debug.Log($"[LapTracker] lap {LapsCompleted}/{totalLaps} completed.");

            if (LapsCompleted >= totalLaps && !Finished)
            {
                Finished = true;
                RaceFinished?.Invoke();
                Debug.Log("[LapTracker] race finished.");
            }
        }

        private void UpdateProgress(Vector3 pos)
        {
            int n = _track.Centerline.Count;
            const int window = 60;
            int best = _nearestSample;
            float bestSqr = (_track.Centerline[best] - pos).sqrMagnitude;
            for (int d = -window; d <= window; d++)
            {
                int i = ((_nearestSample + d) % n + n) % n;
                float sqr = (_track.Centerline[i] - pos).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = i; }
            }
            _nearestSample = best;
            Progress01 = best / (float)n;
        }

        private int NearestSampleFullScan(Vector3 pos)
        {
            var c = _track.Centerline;
            int best = 0;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < c.Count; i++)
            {
                float sqr = (c[i] - pos).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = i; }
            }
            return best;
        }
    }
}
