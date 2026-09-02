using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Minimal in-browser view of the procedural circuit for Fase 1: builds the
    /// track for the URL seed and shows it top-down with the centerline, the
    /// reference racing line and numbered corner markers. No car yet — that is
    /// Fase 1 iteration 3.
    ///
    /// Everything is created at runtime so the scene asset stays a one-liner and
    /// there is nothing to wire up by hand.
    /// </summary>
    [RequireComponent(typeof(TrackConfig))]
    public sealed class TrackDemoBootstrap : MonoBehaviour
    {
        [SerializeField] private int fallbackSeed = 12345;

        private void Start()
        {
            var config = GetComponent<TrackConfig>();
            config.Resolve();
            int seed = config.Seed != 0 ? config.Seed : fallbackSeed;

            TrackData track = TrackGenerator.Generate(seed);

            BuildSurface(track);
            BuildPolyline("Centerline", track.Centerline, 0.6f, new Color(0.75f, 0.75f, 0.78f), 0.05f);
            BuildPolyline("RacingLine", track.RacingLine, 0.9f, new Color(0.30f, 0.80f, 0.88f), 0.12f);
            BuildStartLine(track);
            BuildCornerMarkers(track);
            BuildCamera(track);
            BuildLight();

            Debug.Log($"[TrackDemoBootstrap] seed {track.EffectiveSeed}: {track.Length:F0} m, " +
                      $"{track.Corners.Count} corners, tightest {track.MinCornerRadius:F1} m.");
        }

        private static Material UnlitColor(Color c)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var m = new Material(shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            return m;
        }

        private void BuildSurface(TrackData track)
        {
            var go = new GameObject("TrackSurface");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = TrackMeshBuilder.Build(track);
            go.AddComponent<MeshRenderer>().sharedMaterial = UnlitColor(new Color(0.16f, 0.17f, 0.19f));
        }

        private void BuildPolyline(string label, System.Collections.Generic.IReadOnlyList<Vector3> pts,
            float width, Color color, float y)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.widthMultiplier = width;
            lr.numCornerVertices = 2;
            lr.material = UnlitColor(color);
            lr.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++)
                lr.SetPosition(i, pts[i] + Vector3.up * y);
        }

        private void BuildStartLine(TrackData track)
        {
            var go = new GameObject("StartLine");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = 1.5f;
            lr.material = UnlitColor(new Color(0.35f, 0.85f, 0.45f));
            Vector3 side = Vector3.Cross(Vector3.up, track.StartDirection).normalized * (track.Width * 0.5f);
            lr.positionCount = 2;
            lr.SetPosition(0, track.StartPosition - side + Vector3.up * 0.15f);
            lr.SetPosition(1, track.StartPosition + side + Vector3.up * 0.15f);
        }

        private void BuildCornerMarkers(TrackData track)
        {
            foreach (var corner in track.Corners)
            {
                Vector3 apex = corner.ApexPosition(track);

                var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dot.transform.SetParent(transform, false);
                dot.transform.position = apex + Vector3.up * 0.3f;
                dot.transform.localScale = Vector3.one * 4f;
                Object.Destroy(dot.GetComponent<Collider>());
                dot.GetComponent<MeshRenderer>().sharedMaterial = UnlitColor(
                    corner.Direction == CornerDirection.Left
                        ? new Color(0.85f, 0.40f, 0.85f)
                        : new Color(0.90f, 0.35f, 0.35f));

                var textGo = new GameObject($"T{corner.Index}");
                textGo.transform.SetParent(transform, false);
                textGo.transform.position = apex + Vector3.up * 1f;
                textGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var tm = textGo.AddComponent<TextMesh>();
                tm.text = $"T{corner.Index}";
                tm.fontSize = 64;
                tm.characterSize = 0.35f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = Color.white;
            }
        }

        private void BuildCamera(TrackData track)
        {
            Bounds b = new Bounds(track.Centerline[0], Vector3.zero);
            foreach (var v in track.Centerline) b.Encapsulate(v);
            b.Expand(track.Width * 2f);

            var go = new GameObject("DemoCamera");
            go.transform.SetParent(transform, false);
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = Mathf.Max(b.extents.x, b.extents.z) * 1.1f;
            cam.transform.position = new Vector3(b.center.x, 400f, b.center.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            go.tag = "MainCamera";
        }

        private void BuildLight()
        {
            var go = new GameObject("DemoLight");
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            l.intensity = 1f;
        }
    }
}
