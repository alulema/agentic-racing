using UnityEngine;

namespace AgenticRacing.Vehicle
{
    /// <summary>
    /// Plain (non-MonoBehaviour) detector for start/finish line crossings, so it
    /// can be unit-tested by feeding it a path of positions.
    ///
    /// The finish line is the plane through <c>startPos</c> with normal
    /// <c>startDir</c> (the lap direction). A lap counts only when the car
    /// crosses that plane going forward AND is within <c>triggerRadius</c> of
    /// <c>startPos</c> laterally — otherwise crossing the infinite plane
    /// elsewhere on a twisty circuit would false-trigger. After a count the
    /// detector disarms until the car is back a little way behind the line.
    /// </summary>
    public sealed class LapDetector
    {
        private readonly Vector3 _startPos;
        private readonly Vector3 _startDir;   // unit, lap direction
        private readonly float _triggerRadius;
        private readonly float _rearmDistance;

        private bool _hasPrev;
        private float _prevAhead;
        private bool _armed = true;

        public int LapsCompleted { get; private set; }

        /// <summary>1 while on the first lap, 2 on the second, ...</summary>
        public int CurrentLap => LapsCompleted + 1;

        public LapDetector(Vector3 startPos, Vector3 startDir, float triggerRadius, float rearmDistance = 8f)
        {
            _startPos = startPos;
            _startDir = new Vector3(startDir.x, 0f, startDir.z).normalized;
            _triggerRadius = Mathf.Max(0.1f, triggerRadius);
            _rearmDistance = Mathf.Max(0.5f, rearmDistance);
        }

        /// <summary>
        /// Feed the current car position. Returns true on the frame a lap is
        /// completed.
        /// </summary>
        public bool Tick(Vector3 carPos)
        {
            Vector3 rel = carPos - _startPos;
            float ahead = Vector3.Dot(rel, _startDir);
            Vector3 perp = rel - ahead * _startDir;
            float lateral = new Vector2(perp.x, perp.z).magnitude;

            bool lap = false;

            if (_hasPrev)
            {
                if (_armed && _prevAhead < 0f && ahead >= 0f && lateral <= _triggerRadius)
                {
                    LapsCompleted++;
                    _armed = false;
                    lap = true;
                }
                else if (!_armed && ahead < -_rearmDistance)
                {
                    _armed = true;
                }
            }

            _prevAhead = ahead;
            _hasPrev = true;
            return lap;
        }
    }
}
