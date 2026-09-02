using AgenticRacing.Track;
using AgenticRacing.Vehicle;
using UnityEngine;

namespace AgenticRacing.Demo
{
    /// <summary>
    /// Fase 1 in-browser demo: builds the circuit for the URL seed, drops in one
    /// keyboard-driven car at the start line, follows it top-down, and shows a
    /// lap counter. Everything is created at runtime so the scene asset is a
    /// one-liner. The proper DOM HUD arrives in Fase 4; the on-screen text here
    /// is a temporary Fase 1 readout.
    /// </summary>
    [RequireComponent(typeof(TrackConfig))]
    public sealed class TrackDemoBootstrap : MonoBehaviour
    {
        [SerializeField] private int fallbackSeed = 12345;
        [SerializeField] private int fallbackLaps = 5;
        [SerializeField] private float cameraHeight = 65f;
        [SerializeField] private float cameraSize = 42f;

        private Transform _car;
        private LapTracker _lapTracker;
        private Camera _cam;
        private TrackData _track;

        private void Start()
        {
            var config = GetComponent<TrackConfig>();
            config.Resolve();
            int seed = config.Seed != 0 ? config.Seed : fallbackSeed;
            int laps = config.Laps > 0 ? config.Laps : fallbackLaps;

            _track = TrackGenerator.Generate(seed);

            BuildSurface(_track);
            BuildPolyline("Centerline", _track.Centerline, 0.6f, new Color(0.65f, 0.65f, 0.7f), 0.05f, true);
            BuildPolyline("RacingLine", _track.RacingLine, 0.9f, new Color(0.30f, 0.80f, 0.88f), 0.12f, true);
            BuildStartLine(_track);
            BuildCornerMarkers(_track);
            BuildCar(_track);
            _lapTracker = BuildLapTracker(_track, laps);
            BuildCamera(_track);
            BuildLight();

            Debug.Log($"[TrackDemoBootstrap] seed {_track.EffectiveSeed}: {_track.Length:F0} m, " +
                      $"{_track.Corners.Count} corners, {laps} laps.");
        }

        private void LateUpdate()
        {
            if (_car == null || _cam == null) return;
            Vector3 p = _car.position;
            _cam.transform.position = new Vector3(p.x, cameraHeight, p.z);
        }

        private void OnGUI()
        {
            if (_lapTracker == null) return;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };
            string lap = _lapTracker.Finished
                ? "FINISHED"
                : $"LAP {Mathf.Min(_lapTracker.CurrentLap, _lapTracker.TotalLaps)} / {_lapTracker.TotalLaps}";
            GUI.Label(new Rect(16, 12, 400, 30), lap, style);
            GUI.Label(new Rect(16, 40, 400, 24),
                $"seed {_track.EffectiveSeed}   {_track.Corners.Count} corners   arrows / WASD",
                new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } });
        }

        // --- builders --------------------------------------------------------

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
            var mesh = TrackMeshBuilder.Build(track);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = UnlitColor(new Color(0.16f, 0.17f, 0.19f));
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void BuildPolyline(string label, System.Collections.Generic.IReadOnlyList<Vector3> pts,
            float width, Color color, float y, bool loop)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.widthMultiplier = width;
            lr.numCornerVertices = 2;
            lr.material = UnlitColor(color);
            lr.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) lr.SetPosition(i, pts[i] + Vector3.up * y);
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
                dot.transform.localScale = Vector3.one * 3.5f;
                var col = dot.GetComponent<Collider>();
                if (col != null) Destroy(col);
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
                tm.characterSize = 0.4f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = Color.white;
            }
        }

        private void BuildCar(TrackData track)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Car";
            body.transform.SetParent(transform, false);
            body.transform.localScale = new Vector3(2.0f, 0.8f, 4.2f);
            body.GetComponent<MeshRenderer>().sharedMaterial = UnlitColor(new Color(0.90f, 0.75f, 0.20f));

            var rb = body.AddComponent<Rigidbody>();
            rb.centerOfMass = new Vector3(0f, -0.4f, 0f);

            var controller = body.AddComponent<CarController>();
            _car = body.transform;

            // Spawn just ahead of the line so the first crossing is a real lap.
            Vector3 spawn = track.StartPosition + Vector3.up * 0.6f + track.StartDirection * 2f;
            controller.PlaceAt(spawn, track.StartDirection);
        }

        private LapTracker BuildLapTracker(TrackData track, int laps)
        {
            var go = new GameObject("LapTracker");
            go.transform.SetParent(transform, false);
            var lt = go.AddComponent<LapTracker>();
            lt.Initialise(track, _car, laps);
            return lt;
        }

        private void BuildCamera(TrackData track)
        {
            var go = new GameObject("DemoCamera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = cameraSize;
            _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.farClipPlane = cameraHeight + 50f;
            Vector3 p = _car != null ? _car.position : track.StartPosition;
            _cam.transform.position = new Vector3(p.x, cameraHeight, p.z);
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
