using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Tracks where a moving point is relative to a <see cref="TrackData"/>
    /// centerline: normalised lap progress, signed lateral offset, the local
    /// tangent, and a wrap-aware forward delta. Plain class (no MonoBehaviour) so
    /// the RL agent and any HUD can share it.
    ///
    /// <see cref="Update"/> keeps the nearest sample within a moving window, so
    /// it stays O(window) instead of scanning the whole ~1000-point centerline
    /// every physics step. Call <see cref="Reset"/> whenever the point teleports.
    /// </summary>
    public sealed class TrackProgress
    {
        private readonly IReadOnlyList<Vector3> _center;
        private readonly int _n;
        private readonly float _length;
        private readonly float _spacing;   // metres of arc per sample
        private readonly int _window;

        private int _nearest;
        private float _prevDistance01;

        public TrackProgress(TrackData track, int window = 60)
        {
            _center = track.Centerline;
            _n = _center.Count;
            _length = track.Length;
            _spacing = _length / _n;
            _window = Mathf.Max(4, window);
        }

        /// <summary>Nearest centerline sample index.</summary>
        public int NearestSample => _nearest;

        /// <summary>0..1 position around the loop from the start/finish line.</summary>
        public float Distance01 { get; private set; }

        /// <summary>Distance around the loop in metres.</summary>
        public float ArcMetres => Distance01 * _length;

        /// <summary>Unit forward tangent of the centerline at the nearest sample (XZ).</summary>
        public Vector3 Tangent { get; private set; } = Vector3.forward;

        /// <summary>Signed distance from the centerline in metres (+ = left of travel).</summary>
        public float LateralOffset { get; private set; }

        /// <summary>Re-seeds the nearest sample with a full scan (use after a teleport).</summary>
        public void Reset(Vector3 pos)
        {
            int best = 0;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < _n; i++)
            {
                float sqr = (_center[i] - pos).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = i; }
            }
            _nearest = best;
            Recompute(pos);
            _prevDistance01 = Distance01;
        }

        /// <summary>Advances the tracker to <paramref name="pos"/> for this step.</summary>
        public void Update(Vector3 pos)
        {
            int best = _nearest;
            float bestSqr = (_center[best] - pos).sqrMagnitude;
            for (int d = -_window; d <= _window; d++)
            {
                int i = ((_nearest + d) % _n + _n) % _n;
                float sqr = (_center[i] - pos).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = i; }
            }
            _nearest = best;
            Recompute(pos);
        }

        /// <summary>
        /// Forward progress in metres since the last call, wrap-aware: a jump
        /// from 0.99 to 0.01 reads as a small positive advance, not a big
        /// negative one. Negative when going backwards.
        /// </summary>
        public float ConsumeForwardDelta()
        {
            float d = Distance01 - _prevDistance01;
            if (d > 0.5f) d -= 1f;
            else if (d < -0.5f) d += 1f;
            _prevDistance01 = Distance01;
            return d * _length;
        }

        private void Recompute(Vector3 pos)
        {
            Vector3 a = _center[(_nearest - 1 + _n) % _n];
            Vector3 b = _center[_nearest];
            Vector3 c = _center[(_nearest + 1) % _n];

            Vector3 t = c - a;
            t.y = 0f;
            Tangent = t.sqrMagnitude > 1e-6f ? t.normalized : Tangent;

            // Project onto the segment [b, c] for a smoother progress fraction.
            Vector3 seg = c - b;
            float segLen = seg.magnitude;
            float frac = segLen > 1e-4f ? Mathf.Clamp01(Vector3.Dot(pos - b, seg) / (segLen * segLen)) : 0f;
            Distance01 = ((_nearest + frac) % _n) / _n;

            Vector3 left = new Vector3(-Tangent.z, 0f, Tangent.x);
            Vector3 rel = pos - b;
            rel.y = 0f;
            LateralOffset = Vector3.Dot(rel, left);
        }
    }
}
