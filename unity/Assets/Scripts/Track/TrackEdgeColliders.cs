using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Builds a thin invisible wall along each edge of the track as a
    /// <see cref="MeshCollider"/>, tagged <see cref="EdgeTag"/>. The RL agent's
    /// ray sensor detects these by tag to "see" the track borders (CLAUDE.md §5,
    /// §2.3), and the car bounces off them instead of driving into the void.
    /// </summary>
    public static class TrackEdgeColliders
    {
        /// <summary>Tag on the wall colliders; must exist in TagManager.</summary>
        public const string EdgeTag = "TrackEdge";

        /// <summary>
        /// Creates two child GameObjects ("EdgeLeft" / "EdgeRight") under
        /// <paramref name="parent"/>, each with a wall mesh collider.
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

            CreateWall("EdgeLeft", left, wallHeight, parent);
            CreateWall("EdgeRight", right, wallHeight, parent);
        }

        private static void CreateWall(string name, IReadOnlyList<Vector3> edge, float height, Transform parent)
        {
            int n = edge.Count;
            var verts = new Vector3[n * 2];
            var tris = new int[n * 12]; // both faces so a ray from inside also hits

            for (int i = 0; i < n; i++)
            {
                verts[i * 2] = edge[i];
                verts[i * 2 + 1] = edge[i] + Vector3.up * height;

                int b0 = i * 2;
                int t0 = i * 2 + 1;
                int b1 = ((i + 1) % n) * 2;
                int t1 = b1 + 1;

                int k = i * 12;
                // front
                tris[k + 0] = b0; tris[k + 1] = t0; tris[k + 2] = b1;
                tris[k + 3] = b1; tris[k + 4] = t0; tris[k + 5] = t1;
                // back
                tris[k + 6] = b1; tris[k + 7] = t0; tris[k + 8] = b0;
                tris[k + 9] = t1; tris[k + 10] = t0; tris[k + 11] = b1;
            }

            var mesh = new Mesh { name = name };
            mesh.indexFormat = verts.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            SetTagIfPresent(go, EdgeTag);
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
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
