using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Builds an invisible wall along each edge of the track, tagged
    /// <see cref="EdgeTag"/>, from a chain of short <see cref="BoxCollider"/>
    /// segments. The RL agent's ray sensor detects these by tag to "see" the
    /// borders (CLAUDE.md §5, §2.3), and the car bounces off them.
    ///
    /// History (docs/Devlog.md 2026-09-07/08): a zero-thickness double-sided
    /// triangle ribbon gave PhysX no contact normal (cars got caught and
    /// stalled). A closed extruded MeshCollider had inconsistent winding, so
    /// PhysX could not tell inside from outside the "solid" and cars hit
    /// something mid-track at speed. Convex boxes have none of those problems —
    /// they just need to be short enough not to bulge into the ribbon on a curve.
    /// </summary>
    public static class TrackEdgeColliders
    {
        /// <summary>Tag on the wall colliders; must exist in TagManager.</summary>
        public const string EdgeTag = "TrackEdge";

        private const int SegmentStep = 4;      // edge samples per box (~8 m chord)
        private const float WallThickness = 1.0f;
        private const float OutwardGap = 0.3f;  // inner face this far OUTSIDE the edge line,
                                                // covers the chord sagitta on a curve

        public static void Build(TrackData track, Transform parent, float wallHeight = 2f)
        {
            var center = track.Centerline;
            int n = center.Count;
            float half = track.Width * 0.5f;

            var left = new Vector3[n];
            var right = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 fwd = center[(i + 1) % n] - center[(i - 1 + n) % n];
                fwd.y = 0f;
                fwd.Normalize();
                Vector3 side = new Vector3(-fwd.z, 0f, fwd.x);
                left[i] = center[i] + side * half;
                right[i] = center[i] - side * half;
            }

            CreateWall("EdgeLeft", left, center, wallHeight, parent);
            CreateWall("EdgeRight", right, center, wallHeight, parent);
        }

        private static void CreateWall(string name, IReadOnlyList<Vector3> edge,
            IReadOnlyList<Vector3> center, float height, Transform parent)
        {
            int n = edge.Count;
            var wall = new GameObject(name);
            wall.transform.SetParent(parent, false);
            SetTagIfPresent(wall, EdgeTag);

            for (int i = 0; i < n; i += SegmentStep)
            {
                int j = (i + SegmentStep) % n;
                Vector3 a = edge[i];
                Vector3 b = edge[j];
                Vector3 along = b - a;
                along.y = 0f;
                float len = along.magnitude;
                if (len < 1e-3f) continue;
                along /= len;

                // Outward normal from the mean of the endpoints' (edge - center)
                // directions (edge[k] = center[k] +/- side*half).
                Vector3 outward = (a - center[i]).normalized + (b - center[j]).normalized;
                outward.y = 0f;
                outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : (a - center[i]).normalized;

                var seg = new GameObject($"seg{i}");
                seg.transform.SetParent(wall.transform, false);
                seg.transform.position = 0.5f * (a + b)
                                         + Vector3.up * (height * 0.5f)
                                         + outward * (OutwardGap + WallThickness * 0.5f);
                seg.transform.rotation = Quaternion.LookRotation(along, Vector3.up);
                SetTagIfPresent(seg, EdgeTag);

                // Only ~1 m longer than the chord: 0.5 m overlap each end closes
                // gaps without the box ends swinging into the ribbon on a curve.
                seg.AddComponent<BoxCollider>().size = new Vector3(WallThickness, height, len + 1f);
            }
        }

        private static void SetTagIfPresent(GameObject go, string tag)
        {
            try { go.tag = tag; }
            catch (UnityException)
            {
                Debug.LogWarning($"[TrackEdgeColliders] tag '{tag}' is not defined in TagManager; " +
                                 "ray sensor detection by tag will not work.");
            }
        }
    }
}
