using System.Collections.Generic;
using AgenticRacing.Track;
using UnityEngine;

namespace AgenticRacing.Agents
{
    /// <summary>
    /// Fase 4 race scene (CLAUDE.md §6): the whole demo view built at runtime, so
    /// the scene asset is a single object (<see cref="TrackConfig"/> +
    /// this). It generates the circuit for the URL seed, draws the surface, the
    /// numbered corners and the start line, spawns a <see cref="RaceDirector"/>
    /// on that shared track, and frames the pack with a top-down camera. The HUD,
    /// the team-radio feed and the LLM-status chip live in the DOM overlay
    /// (<c>web/</c>), fed by the <c>race:*</c> / <c>radio:msg</c> messages the
    /// director and each <see cref="Strategy.RaceStrategist"/> push through the
    /// JS bridge — nothing on-screen here except track and cars (§2.2).
    ///
    /// URL params: <c>?seed=</c> and <c>?laps=</c> via <see cref="TrackConfig"/>
    /// (the fixed oval ignores the seed geometrically; it still labels the race),
    /// plus <c>?race=N</c> for <see cref="RaceDirector"/>'s grid / LLM rotation.
    /// </summary>
    [RequireComponent(typeof(TrackConfig))]
    public sealed class RaceSceneBootstrap : MonoBehaviour
    {
        [SerializeField] private int fallbackLaps = 6;
        [SerializeField] private int raceIndex;
        [SerializeField] private float cameraHeight = 90f;
        [SerializeField] private float cameraMargin = 22f;
        [SerializeField] private Vector2 cameraSizeClamp = new Vector2(38f, 240f);

        private RaceDirector _director;
        private Camera _cam;

        private void Start()
        {
            var config = GetComponent<TrackConfig>();
            config.Resolve();
            int seed = config.Seed != 0 ? config.Seed : 12345;
            int laps = config.Laps > 0 ? config.Laps : fallbackLaps;
            int race = ResolveRaceIndex();

            var track = TrackGenerator.Generate(seed);

            BuildSurface(track);
            BuildPolyline("RacingLine", track.RacingLine, 0.8f, new Color(0.30f, 0.80f, 0.88f), 0.12f);
            BuildStartLine(track);
            BuildCornerMarkers(track);
            BuildCamera(track);
            BuildLight();

            var go = new GameObject("RaceDirector");
            go.transform.SetParent(transform, false);
            go.SetActive(false);
            _director = go.AddComponent<RaceDirector>();
            _director.Configure(track, seed, laps, race);
            go.SetActive(true);   // Awake -> StartRace() with the configured track

            Debug.Log($"[RaceSceneBootstrap] seed {track.EffectiveSeed} ({track.Length:F0} m, " +
                      $"{track.Corners.Count} corners) — {laps} laps, race #{race}, " +
                      $"{_director.CarCount} cars.");
        }

        private void LateUpdate()
        {
            if (_director == null || _cam == null || _director.CarCount == 0) return;

            // Frame the pack: centre over its centroid, size to fit its spread.
            Vector3 min = new Vector3(float.MaxValue, 0f, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, 0f, float.MinValue);
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int s = 0; s < _director.CarCount; s++)
            {
                var t = _director.CarTransform(s);
                if (t == null) continue;
                Vector3 p = t.position;
                sum += p;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                count++;
            }
            if (count == 0) return;

            Vector3 mid = sum / count;
            _cam.transform.position = new Vector3(mid.x, cameraHeight, mid.z);

            float halfZ = (max.z - min.z) * 0.5f + cameraMargin;
            float halfX = ((max.x - min.x) * 0.5f + cameraMargin) / Mathf.Max(0.1f, _cam.aspect);
            float size = Mathf.Clamp(Mathf.Max(halfZ, halfX), cameraSizeClamp.x, cameraSizeClamp.y);
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, size, 0.05f);
        }

        private int ResolveRaceIndex()
        {
            if (TryGetQueryInt(Application.absoluteURL, "race", out int r) && r >= 0) return r;
            return Mathf.Max(0, raceIndex);
        }

        // --- builders (kept minimal; Fase 5 consolidates demo visuals) -------

        private static Shader _colorShader;

        private static Material UnlitColor(Color c)
        {
            if (_colorShader == null)
                _colorShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (_colorShader == null)
            {
                var probe = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _colorShader = probe.GetComponent<MeshRenderer>().sharedMaterial.shader;
                Destroy(probe);
            }

            var m = new Material(_colorShader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            m.color = c;
            return m;
        }

        private void BuildSurface(TrackData track)
        {
            var go = new GameObject("TrackSurface");
            go.transform.SetParent(transform, false);
            var mesh = TrackMeshBuilder.Build(track);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = UnlitColor(new Color(0.18f, 0.19f, 0.22f));
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void BuildPolyline(string label, IReadOnlyList<Vector3> pts, float width, Color color, float y)
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

                var textGo = new GameObject($"T{corner.Index}");
                textGo.transform.SetParent(transform, false);
                textGo.transform.position = apex + Vector3.up * 1f;
                textGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var tm = textGo.AddComponent<TextMesh>();
                tm.text = $"T{corner.Index}";
                tm.fontSize = 64;
                tm.characterSize = 0.5f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = corner.Direction == CornerDirection.Left
                    ? new Color(0.85f, 0.55f, 0.90f)
                    : new Color(0.95f, 0.55f, 0.55f);
            }
        }

        private void BuildCamera(TrackData track)
        {
            var go = new GameObject("RaceCamera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = cameraSizeClamp.x;
            _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
            _cam.farClipPlane = cameraHeight + 100f;
            _cam.transform.position = new Vector3(track.StartPosition.x, cameraHeight, track.StartPosition.z);
            go.tag = "MainCamera";
        }

        private void BuildLight()
        {
            var go = new GameObject("RaceLight");
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            l.intensity = 1f;
        }

        /// <summary>Tiny query-int reader (TrackConfig's is internal to its
        /// assembly). Same casing/semantics: first <c>key=int</c> pair wins.</summary>
        private static bool TryGetQueryInt(string url, string key, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(url)) return false;
            int q = url.IndexOf('?');
            if (q < 0 || q == url.Length - 1) return false;
            string query = url.Substring(q + 1);
            int hash = query.IndexOf('#');
            if (hash >= 0) query = query.Substring(0, hash);
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string name = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (!string.Equals(name, key, System.StringComparison.OrdinalIgnoreCase)) continue;
                string raw = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                return int.TryParse(raw.Trim(), out value);
            }
            return false;
        }
    }
}
