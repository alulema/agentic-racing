using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Scene entry point for the procedural circuit: on <see cref="Awake"/> it
    /// reads the seed (from <see cref="TrackConfig"/> if present, else the
    /// serialized field), generates the track and pushes the ribbon mesh into
    /// the sibling <see cref="MeshFilter"/> / <see cref="MeshCollider"/>.
    ///
    /// Later Fase 1 iterations consume <see cref="Data"/> for corner numbering,
    /// the racing line and lap detection; this iteration just produces the track.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public sealed class TrackBuilder : MonoBehaviour
    {
        [SerializeField] private int seed = 12345;
        [SerializeField] private TrackConfig config;
        [SerializeField] private bool addMeshCollider = true;

        [Header("Gizmos")]
        [SerializeField] private bool drawCenterline = true;
        [SerializeField] private bool drawStartLine = true;
        [SerializeField] private bool drawRacingLine = true;
        [SerializeField] private bool drawCorners = true;

        private Mesh _mesh;

        /// <summary>The generated track, or null before <see cref="Awake"/>.</summary>
        public TrackData Data { get; private set; }

        private void Awake()
        {
            int resolvedSeed = seed;
            if (config != null)
            {
                config.Resolve();
                resolvedSeed = config.Seed;
            }

            Build(resolvedSeed);
        }

        /// <summary>Generates the track for <paramref name="withSeed"/> and rebuilds the mesh.</summary>
        public void Build(int withSeed)
        {
            Data = TrackGenerator.Generate(withSeed);

            if (Data.EffectiveSeed != Data.RequestedSeed)
            {
                Debug.Log($"[TrackBuilder] seed {Data.RequestedSeed} was not navigable; " +
                          $"using derived seed {Data.EffectiveSeed} after {Data.Attempts} attempts.");
            }

            Debug.Log($"[TrackBuilder] seed {Data.EffectiveSeed}: {Data.Length:F0} m, " +
                      $"{Data.Centerline.Count} centerline points, tightest corner {Data.MinCornerRadius:F1} m.");

            if (_mesh != null) DestroyImmediate(_mesh);
            _mesh = TrackMeshBuilder.Build(Data);

            GetComponent<MeshFilter>().sharedMesh = _mesh;

            if (addMeshCollider)
            {
                var col = GetComponent<MeshCollider>();
                if (col == null) col = gameObject.AddComponent<MeshCollider>();
                col.sharedMesh = _mesh;
            }
        }

        private void OnDrawGizmos()
        {
            if (Data == null) return;
            var center = Data.Centerline;

            if (drawCenterline)
            {
                Gizmos.color = Color.yellow;
                for (int i = 0; i < center.Count; i++)
                    Gizmos.DrawLine(center[i], center[(i + 1) % center.Count]);
            }

            if (drawRacingLine && Data.RacingLine != null)
            {
                Gizmos.color = Color.cyan;
                var rl = Data.RacingLine;
                for (int i = 0; i < rl.Count; i++)
                    Gizmos.DrawLine(rl[i], rl[(i + 1) % rl.Count]);
            }

            if (drawStartLine)
            {
                Vector3 side = Vector3.Cross(Vector3.up, Data.StartDirection).normalized * (Data.Width * 0.5f);
                Gizmos.color = Color.green;
                Gizmos.DrawLine(Data.StartPosition - side, Data.StartPosition + side);
                Gizmos.DrawLine(Data.StartPosition, Data.StartPosition + Data.StartDirection * 8f);
            }

            if (drawCorners && Data.Corners != null)
            {
                foreach (var corner in Data.Corners)
                {
                    Vector3 apex = corner.ApexPosition(Data);
                    Gizmos.color = corner.Direction == CornerDirection.Left ? Color.magenta : Color.red;
                    Gizmos.DrawSphere(apex, 2f);
#if UNITY_EDITOR
                    UnityEditor.Handles.color = Color.white;
                    UnityEditor.Handles.Label(apex + Vector3.up * 3f, $"T{corner.Index}");
#endif
                }
            }
        }
    }
}
