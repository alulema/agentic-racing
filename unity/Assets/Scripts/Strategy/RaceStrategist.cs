using System;
using System.Collections;
using System.Collections.Generic;
using AgenticRacing.Interop;
using AgenticRacing.Vehicle;
using UnityEngine;
using UnityEngine.Networking;

namespace AgenticRacing.Strategy
{
    /// <summary>
    /// One car's team boss (CLAUDE.md §6). Independent per car — no central brain
    /// (Fase 4 checklist). It:
    ///
    ///  * receives race events from the race director (<see cref="Notify"/>),
    ///  * enforces a per-car cooldown and coalesces events during it (§6.6),
    ///  * builds the §6.3 telemetry and POSTs it to <c>/api/strategy</c> on a
    ///    coroutine — the race NEVER waits on the LLM (§6.6),
    ///  * validates the reply and, only if valid, updates
    ///    <see cref="CurrentDirective"/> (§6.8: bad reply -> keep current),
    ///  * pushes a "team radio" line to the DOM overlay via <see cref="JsBridge"/>,
    ///  * falls back to <see cref="HeuristicFallback"/> whenever the LLM is
    ///    unavailable, and runs on it permanently when <see cref="UseLlm"/> is
    ///    false (the Fase 6.3 control car).
    ///
    /// The strategist's only write surface is <see cref="CurrentDirective"/>,
    /// which feeds the pilot's directive channels (§6.1/§6.5). It never touches
    /// the controls.
    /// </summary>
    public sealed class RaceStrategist : MonoBehaviour
    {
        [Header("Identity (set by the race scene)")]
        public string DisplayName = "car";
        public string ColorHex = "#6ee7b7";

        [Header("Behaviour")]
        [Tooltip("False = this car ignores the LLM and runs the fixed heuristic strategy for the whole race (Fase 6.3 control group).")]
        public bool UseLlm = true;
        [Tooltip("Minimum seconds between calls for this car, however many events fire (§6.6).")]
        public float CooldownSeconds = 12f;
        [Tooltip("Seconds before the request is abandoned; the current directive stays (§6.8).")]
        public float RequestTimeoutSeconds = 25f;
        [Tooltip("Last-N lap/corner notes carried in the payload (§6.3).")]
        public int NoteMemory = 8;

        /// <summary>The one value the strategist writes. The pilot reads it.</summary>
        public RaceDirective CurrentDirective { get; private set; } = RaceDirective.Neutral;

        /// <summary>Fired after every call attempt (applied or not) with the full
        /// record, so Fase 6.1 can persist it. Not required for the demo to run.</summary>
        public event Action<StrategyRecord> DecisionMade;

        // Wiring supplied by the race scene.
        public StrategyContext Context { get; set; }
        public StrategyDirectiveMap Map { get; set; }

        // Client-side backstop for §7.5 (the server also gates to 1). Keeps the
        // whole grid from firing at Ollama in the same frame at race start.
        private static int _globalInFlight;
        private const int GlobalInFlightCap = 2;

        private readonly List<string> _notes = new();
        private float _cooldownUntil;
        private bool _callInProgress;
        private bool _pending;
        private StrategyEvent _pendingEvent;
        private TelemetrySnapshot _pendingSnapshot;

        /// <summary>Set the directive the car starts the race on (its population
        /// preset). Called once by the race scene before lights out.</summary>
        public void SetInitialDirective(RaceDirective directive) => CurrentDirective = directive;

        /// <summary>Record something for the lap-over-lap memory (§6.3). The race
        /// director calls this ("L3 T4: lost 0.3s, entry slow" / "L3: overtake on
        /// car_01 at T7 failed, lost position" — §6.2 keeps the failures too).</summary>
        public void AddNote(string note)
        {
            if (string.IsNullOrEmpty(note)) return;
            _notes.Add(note);
            int overflow = _notes.Count - Mathf.Max(1, NoteMemory);
            if (overflow > 0) _notes.RemoveRange(0, overflow);
        }

        /// <summary>
        /// A race event happened. If the cooldown is clear and no call is
        /// running, fire now; otherwise remember the most relevant event and
        /// fire when the cooldown expires (§6.6 coalescing).
        /// </summary>
        public void Notify(StrategyEvent evt, TelemetrySnapshot snapshot)
        {
            if (snapshot == null) return;

            if (_callInProgress || Time.time < _cooldownUntil || _globalInFlight >= GlobalInFlightCap)
            {
                if (!_pending || Rank(evt) > Rank(_pendingEvent)) _pendingEvent = evt;
                _pendingSnapshot = snapshot; // keep the freshest data
                _pending = true;
                return;
            }

            StartCoroutine(Run(evt, snapshot));
        }

        private void Update()
        {
            if (_pending && !_callInProgress && Time.time >= _cooldownUntil
                && _globalInFlight < GlobalInFlightCap)
            {
                _pending = false;
                StartCoroutine(Run(_pendingEvent, _pendingSnapshot));
            }
        }

        private static int Rank(StrategyEvent e) => e switch
        {
            StrategyEvent.Incident => 5,
            StrategyEvent.FinalLap => 4,
            StrategyEvent.PositionChange => 3,
            StrategyEvent.RivalInRange => 2,
            _ => 1, // LapCompleted
        };

        // A holder so a coroutine can hand a value back to its caller.
        private sealed class Box { public StrategyDecision Value = StrategyDecision.Keep("pending"); }

        private IEnumerator Run(StrategyEvent evt, TelemetrySnapshot snapshot)
        {
            _callInProgress = true;
            _cooldownUntil = Time.time + CooldownSeconds;

            var box = new Box();

            if (!UseLlm || Context == null)
            {
                // Control car (or no context wired): pure heuristic, no network.
                var d = HeuristicFallback.Decide(snapshot, ResolveMap());
                box.Value = new StrategyDecision(true, d, null, Array.Empty<int>(),
                    HeuristicRadio(d), "fixed heuristic strategy", "heuristic", 0);
            }
            else
            {
                yield return PostLlm(snapshot, box);
            }

            Apply(box.Value, evt, snapshot);
            _callInProgress = false;
        }

        private IEnumerator PostLlm(TelemetrySnapshot snapshot, Box box)
        {
            string body = BuildRequestBody(Context, snapshot, _notes);

            using var req = new UnityWebRequest(StrategyApi.Url(), "POST")
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.CeilToInt(RequestTimeoutSeconds),
            };
            req.SetRequestHeader("Content-Type", "application/json");

            _globalInFlight++;
            yield return req.SendWebRequest();
            _globalInFlight = Mathf.Max(0, _globalInFlight - 1);

            box.Value = req.result != UnityWebRequest.Result.Success
                ? StrategyDecision.Keep("transport: " + req.error)
                : ParseEnvelope(req.downloadHandler.text, snapshot);
        }

        /// <summary>Turn a server envelope into a decision (§6.8). Any problem =>
        /// keep the current directive.</summary>
        private StrategyDecision ParseEnvelope(string json, TelemetrySnapshot snapshot)
        {
            StrategyEnvelopeDto env;
            try { env = JsonUtility.FromJson<StrategyEnvelopeDto>(json); }
            catch (Exception e) { return StrategyDecision.Keep("unparseable: " + e.Message); }

            if (env == null || string.IsNullOrEmpty(env.status))
                return StrategyDecision.Keep("empty envelope");

            if (env.status != "ok" || env.strategy == null)
                return StrategyDecision.Keep(env.reason ?? "fallback", env.latency_ms);

            var s = env.strategy;
            if (!TryKind(s.directive, out var kind)
                || !TryLevel(s.aggression, out var agg)
                || !TryLevel(s.risk_tolerance, out var risk))
                return StrategyDecision.Keep("rejected: bad enum", env.latency_ms);

            string target = string.IsNullOrEmpty(s.target_rival) ? null : s.target_rival;
            if (target != null && !KnownCar(target, snapshot))
                return StrategyDecision.Keep("rejected: unknown target_rival", env.latency_ms);

            var directive = ResolveMap().ToDirective(kind, agg, risk);
            string radio = ClampWords(s.radio, 15);
            return new StrategyDecision(true, directive, target, s.focus_corners,
                radio, s.rationale, "ok", env.latency_ms);
        }

        private void Apply(StrategyDecision d, StrategyEvent evt, TelemetrySnapshot snapshot)
        {
            if (d.Applied) CurrentDirective = d.Directive;

            string status = d.Applied && d.Reason == "ok" ? "ok" : "fallback";
            string radio = !string.IsNullOrEmpty(d.Radio)
                ? d.Radio
                : "Staying on plan — no new call from the pit wall.";

            EmitRadio(status, d, radio);
            AddNote(NoteFor(evt, d, snapshot));

            DecisionMade?.Invoke(new StrategyRecord
            {
                CarId = Context?.CarId ?? DisplayName,
                Event = evt,
                Applied = d.Applied,
                Reason = d.Reason,
                Directive = CurrentDirective,
                TargetRival = d.TargetRival,
                Radio = radio,
                Rationale = d.Rationale,
                LatencyMs = d.LatencyMs,
                Snapshot = snapshot,
            });
        }

        private void EmitRadio(string status, StrategyDecision d, string radio)
        {
            var j = new JsonBuilder();
            j.Obj()
                .Field("type", "radio:msg")
                .Field("carId", Context?.CarId ?? DisplayName)
                .Field("name", DisplayName)
                .Field("color", ColorHex)
                .Field("directive", CurrentDirective.Kind.ToString().ToLowerInvariant())
                .Field("aggression", LevelLabel(CurrentDirective.Aggression))
                .Field("risk", LevelLabel(CurrentDirective.RiskTolerance));
            if (!string.IsNullOrEmpty(d.TargetRival)) j.Field("targetRival", d.TargetRival);
            j.Arr("focusCorners");
            if (d.FocusCorners != null)
                foreach (int c in d.FocusCorners) j.Val(c);
            j.EndArr();
            j.Field("radio", radio).Field("status", status);
            if (!string.IsNullOrEmpty(d.Reason) && d.Reason != "ok") j.Field("reason", d.Reason);
            if (d.LatencyMs > 0) j.Field("latencyMs", d.LatencyMs);
            j.EndObj();

            JsBridge.Send(j.ToString());
        }

        // -- request body (§6.3) ------------------------------------------------

        private static string BuildRequestBody(StrategyContext ctx, TelemetrySnapshot t, List<string> notes)
        {
            var j = new JsonBuilder();
            j.Obj();
            WriteContext(j, ctx);
            WriteTelemetry(j, t, notes);
            j.EndObj();
            return j.ToString();
        }

        private static void WriteContext(JsonBuilder j, StrategyContext c)
        {
            j.Key("context").Obj()
                .Field("car_id", c.CarId)
                .Field("pilot_profile", c.PilotProfile ?? "")
                .Field("track_name", c.TrackName ?? "circuit")
                .Field("track_length_m", c.TrackLengthM)
                .Field("total_laps", c.TotalLaps);
            j.Arr("corners");
            foreach (var corner in c.Corners)
                j.Obj()
                    .Field("index", corner.Index)
                    .Field("direction", corner.Direction)
                    .Field("severity", corner.Severity)
                    .EndObj();
            j.EndArr();
            j.EndObj();
        }

        private static void WriteTelemetry(JsonBuilder j, TelemetrySnapshot t, List<string> notes)
        {
            j.Key("telemetry").Obj()
                .Field("event", EventName(t.Event))
                .Field("lap", t.Lap)
                .Field("laps_remaining", t.LapsRemaining);

            var me = t.Me;
            j.Key("me").Obj()
                .Field("car_id", me.CarId)
                .Field("position", me.Position);
            OptTime(j, "last_lap_time", me.LastLapTime);
            OptTime(j, "best_lap_time", me.BestLapTime);
            if (me.GapAhead >= 0f) j.Field("gap_ahead", me.GapAhead); else j.Null("gap_ahead");
            if (me.GapBehind >= 0f) j.Field("gap_behind", me.GapBehind); else j.Null("gap_behind");
            j.Field("current_directive", me.CurrentDirective.ToString().ToLowerInvariant())
                .Field("incidents", me.Incidents)
                .EndObj();

            j.Arr("rivals");
            foreach (var r in t.Rivals)
            {
                j.Obj()
                    .Field("car_id", r.CarId)
                    .Field("position", r.Position)
                    .Field("gap", r.GapSeconds);
                OptTime(j, "last_lap_time", r.LastLapTime);
                j.Field("trend", r.Trend.ToString().ToLowerInvariant()).EndObj();
            }
            j.EndArr();

            j.Arr("notes");
            foreach (string note in notes) j.Val(note);
            j.EndArr();

            j.EndObj();
        }

        private static void OptTime(JsonBuilder j, string key, float t)
        {
            if (t > 0f) j.Field(key, t); else j.Null(key);
        }

        // -- helpers ----------------------------------------------------------

        private StrategyDirectiveMap ResolveMap() => Map != null ? Map : StrategyDirectiveMap.Default;

        private static bool KnownCar(string id, TelemetrySnapshot t)
        {
            if (id == t.Me.CarId) return true;
            foreach (var r in t.Rivals) if (r.CarId == id) return true;
            return false;
        }

        private static bool TryKind(string s, out DirectiveKind kind)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "attack": kind = DirectiveKind.Attack; return true;
                case "defend": kind = DirectiveKind.Defend; return true;
                case "conserve": kind = DirectiveKind.Conserve; return true;
                case "push": kind = DirectiveKind.Push; return true;
                default: kind = DirectiveKind.Push; return false;
            }
        }

        private static bool TryLevel(string s, out DirectiveLevel level)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "low": level = DirectiveLevel.Low; return true;
                case "medium": level = DirectiveLevel.Medium; return true;
                case "high": level = DirectiveLevel.High; return true;
                default: level = DirectiveLevel.Medium; return false;
            }
        }

        private static string LevelLabel(float v) => v < 0.33f ? "low" : v < 0.66f ? "medium" : "high";

        private static string EventName(StrategyEvent e) => e switch
        {
            StrategyEvent.LapCompleted => "lap_completed",
            StrategyEvent.RivalInRange => "rival_in_range",
            StrategyEvent.PositionChange => "position_change",
            StrategyEvent.Incident => "incident",
            StrategyEvent.FinalLap => "final_lap",
            _ => "lap_completed",
        };

        private static string ClampWords(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var parts = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= max) return s.Trim();
            return string.Join(" ", parts, 0, max).TrimEnd(',', '.', ';', ':') + "…";
        }

        private static string HeuristicRadio(RaceDirective d) => d.Kind switch
        {
            DirectiveKind.Attack => "Push on — close the gap and make the move when it's clean.",
            DirectiveKind.Defend => "Cover the inside, hold your line, make them work for it.",
            DirectiveKind.Conserve => "Settle in, smooth inputs, look after the car.",
            _ => "Clear track — build a rhythm and keep it tidy.",
        };

        private static string NoteFor(StrategyEvent evt, StrategyDecision d, TelemetrySnapshot t)
        {
            string what = d.Applied ? $"directive -> {d.Directive.Kind}" : $"kept directive ({d.Reason})";
            return $"L{t.Lap} {EventName(evt)}: {what}";
        }
    }

    /// <summary>Full record of one strategy call for Fase 6.1 persistence.</summary>
    public sealed class StrategyRecord
    {
        public string CarId;
        public StrategyEvent Event;
        public bool Applied;
        public string Reason;
        public RaceDirective Directive;
        public string TargetRival;
        public string Radio;
        public string Rationale;
        public int LatencyMs;
        public TelemetrySnapshot Snapshot;
    }
}
