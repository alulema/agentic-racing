using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Builds an invisible wall along each edge of the track, tagged
    /// <see cref="EdgeTag"/>, out of a chain of overlapping <see cref="BoxCollider"/>
    /// segments. The RL agent's ray sensor detects these by tag to "see" the
    /// borders (CLAUDE.md §5, §2.3), and the car bounces off them instead of
    /// driving into the void.
    ///
    /// Boxes, not a MeshCollider: the old version used a zero-thickness,
    /// double-sided triangle ribbon, and PhysX cannot resolve contact against
    /// that — a car that touched an edge got caught in the geometry and stalled
    /// there instead of bouncing (diagnosed 2026-09-07, docs/Devlog.md). Convex
    /// boxes give robust, cheap contact.
    /// </summary>
    public static class TrackEdgeColliders
    {
        /// <summary>Tag on the wall colliders; must exist in TagManager.</summary>
        public const string EdgeTag = "TrackEdge";

        private const int SegmentStep = 6;      // edge samples per box (~12 m)
        private const float WallThickness = 0.8f;

        /// <summary>
        /// Creates two child GameObjects ("EdgeLeft" / "EdgeRight") under
        /// <paramref name="parent"/>, each holding the box-segment wall for one
        /// side.
        /// </summary>
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

                // Push the box centre slightly outside the drivable ribbon so the
                // wall doesn't eat into the racing surface. edge[i] was built as
                // center[i] +/- side*half, so (edge - center) is the outward normal.
                Vector3 mid = 0.5f * (a + b);
                Vector3 outward = edge[i] - center[i];
                outward.y = 0f;
                if (outward.sqrMagnitude > 1e-4f) outward.Normalize();

                var seg = new GameObject($"seg{i}");
                seg.transform.SetParent(wall.transform, false);
                seg.transform.position = mid + Vector3.up * (height * 0.5f)
                                             + outward * (WallThickness * 0.5f);
                seg.transform.rotation = Quaternion.LookRotation(along, Vector3.up);
                SetTagIfPresent(seg, EdgeTag);

                var box = seg.AddComponent<BoxCollider>();
                // Overlap neighbours a touch so there are no gaps on the outside
                // of a curve.
                box.size = new Vector3(WallThickness, height, len + SegmentStep * 0.5f);
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
