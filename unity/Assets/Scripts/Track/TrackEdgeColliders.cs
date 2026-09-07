using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Builds an invisible wall along each edge of the track, tagged
    /// <see cref="EdgeTag"/>. The RL agent's ray sensor detects these by tag to
    /// "see" the borders (CLAUDE.md §5, §2.3), and the car bounces off them
    /// instead of driving into the void.
    ///
    /// Each wall is a single <see cref="MeshCollider"/> built as a closed,
    /// curve-following extruded SOLID (inner + outer + top + bottom faces,
    /// <see cref="WallThickness"/> wide). The first version was a zero-thickness
    /// double-sided triangle ribbon: PhysX cannot define a contact normal against
    /// that, so a car that touched an edge got caught in the geometry and
    /// stalled there instead of bouncing (diagnosed 2026-09-07, docs/Devlog.md).
    /// A box-segment chain was worse — straight boxes bulge into the ribbon on a
    /// curve. A thick extruded solid follows the curve exactly and gives a
    /// well-defined contact normal.
    /// </summary>
    public static class TrackEdgeColliders
    {
        /// <summary>Tag on the wall colliders; must exist in TagManager.</summary>
        public const string EdgeTag = "TrackEdge";

        private const float WallThickness = 1.0f;   // > per-step travel at race speed, so no tunnelling
        private const float WallHeightDefault = 2.0f;

        /// <summary>
        /// Creates two child GameObjects ("EdgeLeft" / "EdgeRight") under
        /// <paramref name="parent"/>, each with the extruded-solid wall for one
        /// side.
        /// </summary>
        public static void Build(TrackData track, Transform parent, float wallHeight = WallHeightDefault)
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
            var verts = new Vector3[n * 4];
            var tris = new List<int>(n * 24);

            for (int i = 0; i < n; i++)
            {
                Vector3 outward = edge[i] - center[i];
                outward.y = 0f;
                outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.right;

                int v = i * 4;
                verts[v + 0] = edge[i];                                        // inner bottom
                verts[v + 1] = edge[i] + Vector3.up * height;                  // inner top
                verts[v + 2] = edge[i] + outward * WallThickness;             // outer bottom
                verts[v + 3] = edge[i] + outward * WallThickness + Vector3.up * height; // outer top
            }

            for (int i = 0; i < n; i++)
            {
                int a = i * 4;
                int b = ((i + 1) % n) * 4;
                // a+0 iB, a+1 iT, a+2 oB, a+3 oT ; same for b
                AddQuad(tris, a + 0, a + 1, b + 1, b + 0); // inner face
                AddQuad(tris, b + 2, b + 3, a + 3, a + 2); // outer face (reverse winding)
                AddQuad(tris, a + 1, a + 3, b + 3, b + 1); // top face
                AddQuad(tris, b + 0, b + 2, a + 2, a + 0); // bottom face
            }

            var mesh = new Mesh { name = name };
            mesh.indexFormat = verts.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            SetTagIfPresent(go, EdgeTag);
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;          // non-convex: a closed solid, so contact is well-defined
        }

        private static void AddQuad(List<int> tris, int v0, int v1, int v2, int v3)
        {
            tris.Add(v0); tris.Add(v1); tris.Add(v2);
            tris.Add(v0); tris.Add(v2); tris.Add(v3);
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
