using System.Collections.Generic;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Builds a flat ribbon <see cref="Mesh"/> that follows a closed centerline.
    /// The ribbon is closed seamlessly at the start/finish join by wrapping the
    /// final quad back to the first pair of vertices.
    /// </summary>
    public static class TrackMeshBuilder
    {
        /// <summary>
        /// Creates the drivable-surface mesh for <paramref name="track"/>.
        /// UVs run 0..1 across the width and 0..N along the loop, where N is the
        /// loop length divided by <paramref name="uvTileLength"/>, so a tiling
        /// asphalt material keeps a constant real-world scale.
        /// </summary>
        public static Mesh Build(TrackData track, float uvTileLength = 8f)
        {
            IReadOnlyList<Vector3> center = track.Centerline;
            int n = center.Count;
            float half = track.Width * 0.5f;

            var vertices = new Vector3[n * 2];
            var normals = new Vector3[n * 2];
            var uv = new Vector2[n * 2];
            var colors = new Color32[n * 2];
            var triangles = new int[n * 6];
            var white = new Color32(255, 255, 255, 255);

            float arc = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 curr = center[i];
                Vector3 next = center[(i + 1) % n];
                Vector3 prev = center[(i - 1 + n) % n];

                Vector3 forward = (next - prev);
                forward.y = 0f;
                forward.Normalize();
                // Right-hand normal in the XZ plane.
                Vector3 side = new Vector3(forward.z, 0f, -forward.x);

                int l = i * 2;
                int r = i * 2 + 1;
                vertices[l] = curr - side * half;
                vertices[r] = curr + side * half;
                normals[l] = Vector3.up;
                normals[r] = Vector3.up;

                float v = arc / uvTileLength;
                uv[l] = new Vector2(0f, v);
                uv[r] = new Vector2(1f, v);
                colors[l] = white;
                colors[r] = white;

                arc += Vector3.Distance(curr, next);

                int t = i * 6;
                int lNext = ((i + 1) % n) * 2;
                int rNext = lNext + 1;
                // Wind CCW when viewed from +Y so the surface faces up.
                triangles[t + 0] = l;
                triangles[t + 1] = lNext;
                triangles[t + 2] = r;
                triangles[t + 3] = r;
                triangles[t + 4] = lNext;
                triangles[t + 5] = rNext;
            }

            var mesh = new Mesh { name = $"Track_{track.EffectiveSeed}" };
            mesh.indexFormat = vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.colors32 = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
