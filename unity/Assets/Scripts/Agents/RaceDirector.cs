using System.Collections.Generic;
using AgenticRacing.Interop;
using AgenticRacing.Strategy;
using AgenticRacing.Track;
using AgenticRacing.Vehicle;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace AgenticRacing.Agents
{
    /// <summary>
    /// Fase 4 race-scene director (CLAUDE.md §6). Builds the six-car grid from
    /// <see cref="RaceDirective.Population"/> on one shared fixed circuit, runs
    /// the live race state (classification, gaps in seconds, lap times,
    /// incidents), assembles the §6.3 telemetry, and fires each car's
    /// <see cref="RaceStrategist"/> by event with a per-car cooldown handled
    /// inside the strategist (§6.6). The strategist owns the <c>/api/strategy</c>
    /// call, the radio line and the directive it writes back; the director only
    /// decides <b>when</b> to ask and with <b>what</b> context, and mirrors each
    /// strategist's current directive into its pilot every tick so the car
    /// actually drives to it (§6.1).
    ///
    /// Mixed field for the Fase 6.3 comparison: the first <see cref="llmCars"/>
    /// grid slots run the LLM strategist, the rest run the fixed heuristic
    /// strategy. Rotating grid order and which slots carry the LLM across races
    /// is §6.3's job; here they are the serialized defaults.
    ///
    /// Pilots are <see cref="RaceAgent"/> in <see cref="RaceAgent.RaceMode"/>
    /// (the Camino A heuristic pilot — no <c>.onnx</c>, no episode resets). The
    /// soft-respawn / off-track policy is Fase 4 paso 3; until then a car that
    /// leaves the track keeps going on the heuristic's own wall recovery.
    ///
    /// Paso 2 supplies the scene, the car-vs-car collisions (which call
    /// <see cref="ReportIncident"/>), and the grid/LLM-slot rotation.
    /// </summary>
    public sealed class RaceDirector : MonoBehaviour
    {
        [Header("Race")]
        [Tooltip("Fixed rounded-rect oval ignores this; kept for parity with the procedural path.")]
        [SerializeField] private int trackSeed = 1;
        [SerializeField, Min(1)] private int totalLaps = 8;
        [Tooltip("How many of the six cars carry the LLM strategist this race; the rest run the fixed heuristic (Fase 6.3 mixed field).")]
        [SerializeField, Min(0)] private int llmCars = 3;
        [Tooltip("Rotates BOTH the grid order and which population members carry the LLM, on different index axes, so a grid-slot advantage can't be mistaken for the strategist's effect (§6.3). The demo shell bumps this per race.")]
        [SerializeField, Min(0)] private int raceIndex;
        [SerializeField] private bool autoStartOnAwake = true;

        [Header("Grid")]
        [SerializeField] private float firstRowBack = 6f;   // metres behind the s/f line
        [SerializeField] private float rowGap = 9f;         // metres between rows
        [SerializeField] private float colGap = 3.5f;       // metres either side of the centreline

        [Header("Strategy wiring")]
        [Tooltip("Shared calibrated directive map (§6.5). Null => StrategyDirectiveMap.Default.")]
        [SerializeField] private StrategyDirectiveMap directiveMap;
        [Tooltip("Gap (s) to the car ahead that counts as 'in range' for a RivalInRange call (§6.6).")]
        [SerializeField] private float rivalEngageGapSeconds = 1.5f;
        [Tooltip("How long that gap must hold before the call fires — not a momentary blip (§6.6).")]
        [SerializeField] private float rivalEngageHoldSeconds = 2f;

        [Header("Soft recovery (§6.3: never a hard reset mid-race)")]
        [Tooltip("Metres past the wall before a car counts as off the track.")]
        [SerializeField] private float offTrackMargin = 2f;
        [SerializeField] private float offTrackRecoverSeconds = 1.5f;
        [Tooltip("Speed (m/s) under which a car that has started racing counts as dead-stopped.")]
        [SerializeField] private float stuckSpeed = 0.6f;
        [SerializeField] private float stuckRecoverSeconds = 4f;
        [Tooltip("Heading vs track tangent below this dot product, at speed, = driving backwards.")]
        [SerializeField] private float wrongWayRecoverSeconds = 3f;
        [Tooltip("Time penalty added (as lost track position) each time a car is recovered.")]
        [SerializeField] private float softRecoverPenaltySeconds = 4f;

        [Header("Overlay")]
        [SerializeField] private bool emitOverlay = true;
        [Tooltip("Grid slot the HUD highlights as 'me'; -1 for none.")]
        [SerializeField] private int focusCarSlot = 0;
        [SerializeField] private float tickInterval = 0.2f;

        // One HUD/radio colour per population member.
        private static readonly string[] Palette =
        { "#6ee7b7", "#fca5a5", "#93c5fd", "#fcd34d", "#c4b5fd", "#86efac" };

        private TrackData _track;
        private float _trackLen;
        private float _halfWidth;
        private CornerInfo[] _corners;
        private readonly List<CarState> _cars = new();
        private readonly List<CarState> _sorted = new();   // reused each tick
        private bool _started;
        private bool _finished;      // every car has finished; the director stops
        private bool _chequered;     // the leader has finished; HUD reads FINISHED
        private int _finishSeq;      // running finish counter -> CarState.FinishOrder
        private float _winnerFinishTime;
        private float _nextTickAt;

        /// <summary>True once the leader has completed <see cref="totalLaps"/>.</summary>
        public bool Finished => _finished;

        /// <summary>Number of cars on the grid (== population size once started).</summary>
        public int CarCount => _cars.Count;

        /// <summary>Stable <c>car_NN</c> id for a grid slot, or null if out of range.</summary>
        public string CarIdOf(int slot) => (slot >= 0 && slot < _cars.Count) ? _cars[slot].CarId : null;

        /// <summary>The shared circuit (available after <see cref="StartRace"/>, or
        /// right after <see cref="Configure"/> if one was supplied).</summary>
        public TrackData Track => _track;

        /// <summary>Grid slot the HUD/camera treat as the viewer's car; -1 for none.</summary>
        public int FocusCarSlot => focusCarSlot;

        /// <summary>Transform of the car in a grid slot, or null if out of range.</summary>
        public Transform CarTransform(int slot) =>
            (slot >= 0 && slot < _cars.Count) ? _cars[slot].Go.transform : null;

        private void Awake()
        {
            if (autoStartOnAwake) StartRace();
        }

        /// <summary>Scene-bootstrap hook: hand the director a pre-generated track
        /// (so the visuals and the sim share one <see cref="TrackData"/>) plus the
        /// race parameters. Call on an inactive object before it activates, or
        /// before <see cref="StartRace"/>.</summary>
        public void Configure(TrackData track, int seed, int laps, int race)
        {
            _track = track;
            _trackLen = track != null ? track.Length : 0f;
            trackSeed = seed;
            totalLaps = Mathf.Max(1, laps);
            raceIndex = Mathf.Max(0, race);
        }

        /// <summary>Build the track (if not already supplied via
        /// <see cref="Configure"/>) and the grid, and start the race. Idempotent.</summary>
        public void StartRace()
        {
            if (_started) return;
            _started = true;

            if (_track == null)
            {
                _track = TrackGenerator.Generate(trackSeed);
                _trackLen = _track.Length;
            }
            _halfWidth = _track.Width * 0.5f;
            _corners = BuildCornerMap(_track);
            TrackEdgeColliders.Build(_track, transform);

            // Rotate grid order (over slots) and LLM assignment (over population
            // members) on separate axes: in race k, slot s holds member (s+k) and
            // a member m carries the LLM iff (m+k) mod N < llmCars. Every member
            // then visits every slot and carries the LLM in exactly llmCars of
            // the N races, decoupled from where it starts (§6.3 methodology).
            var pop = RaceDirective.Population;
            int n = pop.Length;
            for (int slot = 0; slot < n; slot++)
            {
                int member = (slot + raceIndex) % n;
                bool useLlm = (member + raceIndex) % n < Mathf.Clamp(llmCars, 0, n);
                _cars.Add(BuildCar(slot, member, pop[member], useLlm));
            }

            // Seed classification with grid order so frame 1 fires no spurious
            // PositionChange events.
            for (int i = 0; i < _cars.Count; i++)
            {
                _cars[i].Position = i + 1;
                _cars[i].PrevPosition = i + 1;
            }
            _sorted.AddRange(_cars);

            if (emitOverlay) EmitRaceStart();
        }

        /// <summary>Car-vs-car contact, called by <see cref="RaceCarContact"/>:
        /// bumps both cars' incident counts and fires an Incident call for each,
        /// so the strategist can react and the bitácora keeps the failures
        /// too (§6.2).</summary>
        public void ReportIncident(int slotA, int slotB)
        {
            if (!_started || _finished) return;
            if (slotA < 0 || slotA >= _cars.Count || slotB < 0 || slotB >= _cars.Count || slotA == slotB) return;

            RaiseIncident(_cars[slotA], _cars[slotB]);
            RaiseIncident(_cars[slotB], _cars[slotA]);
        }

        private void RaiseIncident(CarState st, CarState other)
        {
            st.Incidents++;
            st.Strategist.AddNote($"L{Mathf.Max(1, st.Crossings)}: contact with {other.CarId}");
            st.Strategist.Notify(StrategyEvent.Incident, BuildSnapshot(st, StrategyEvent.Incident));
        }

        // -- grid construction ------------------------------------------------

        private CarState BuildCar(int slot, int memberIndex, RaceDirective.PopulationMember member, bool useLlm)
        {
            // Identity (id, name, colour) follows the population MEMBER, not the
            // grid slot, so a pilot is the same "car_0N" with the same colour and
            // profile across races even as its start slot and LLM flag rotate —
            // the bitácora (§6.1) and the mixed-field comparison (§6.3) track a
            // stable pilot, not a seat.
            var st = new CarState
            {
                Slot = slot,
                MemberIndex = memberIndex,
                CarId = $"car_{memberIndex + 1:00}",
                Name = member.Name,
                Color = Palette[memberIndex % Palette.Length],
            };

            // Cold build: assemble every ML-Agents component while the object is
            // INACTIVE, then activate once, so Agent.OnEnable runs
            // InitializeSensors() exactly once over the final component set.
            // Building it live, one component at a time, desyncs the observation
            // spec — see TrainingArena.BuildAgentCar for the full rationale.
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"Car_{slot + 1}_{member.Name}";
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            go.transform.localScale = new Vector3(2.0f, 0.8f, 4.2f);
            go.layer = 2; // Ignore Raycast — no ray sensor here, but keep parity
            TintCar(go, st.Color);   // body colour == HUD swatch == team-radio colour

            go.AddComponent<Rigidbody>();
            var car = go.AddComponent<CarController>();
            car.ReadKeyboard = false;

            var bp = go.AddComponent<BehaviorParameters>();
            bp.BehaviorName = "RaceAgent";
            bp.BrainParameters.VectorObservationSize = RaceAgent.ObsSize;
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(3);
            bp.BehaviorType = BehaviorType.HeuristicOnly;   // Camino A: pure C# pilot, no model

            var agent = go.AddComponent<RaceAgent>();
            agent.RaceMode = true;
            agent.ExternalTrack = _track;
            agent.InstanceDirective = member.Directive;     // start-of-race stance
            agent.DirectiveMap = directiveMap;
            agent.MaxStep = 0;                              // one long race, no auto-end

            // DecisionRequester last: it [RequireComponent(typeof(Agent))] and
            // runs at execution order -10; added before a concrete Agent it races
            // the agent's own init.
            var dr = go.AddComponent<DecisionRequester>();
            dr.DecisionPeriod = 5;
            dr.TakeActionsBetweenDecisions = true;

            var contact = go.AddComponent<RaceCarContact>();
            contact.Slot = slot;
            contact.Director = this;

            var strat = go.AddComponent<RaceStrategist>();
            strat.DisplayName = member.Name;
            strat.ColorHex = st.Color;
            strat.UseLlm = useLlm;
            strat.Context = BuildContext(st, member);
            strat.Map = directiveMap;

            // Grid pose BEFORE activation, so RaceAgent.OnEpisodeBegin (which in
            // RaceMode re-seeds its progress tracker from the current pose the
            // instant the object activates) sees the real slot, not the origin.
            Vector3 dir = _track.StartDirection;
            Vector3 left = new Vector3(-dir.z, 0f, dir.x);
            int row = slot / 2;
            int col = slot % 2;
            Vector3 pos = _track.StartPosition
                          - dir * (firstRowBack + row * rowGap)
                          + left * (col == 0 ? colGap : -colGap)
                          + Vector3.up * 0.4f;
            go.transform.SetPositionAndRotation(
                pos, Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z).normalized, Vector3.up));

            go.SetActive(true);                             // single clean InitializeSensors()

            strat.SetInitialDirective(member.Directive);
            car.PlaceAt(pos, dir);                          // canonical placement + zero motion

            st.Go = go;
            st.Car = car;
            st.Agent = agent;
            st.Strategist = strat;
            st.Progress = new TrackProgress(_track);
            st.Progress.Reset(pos);
            st.PrevDistance01 = st.Progress.Distance01;
            st.TotalArc = st.Progress.ArcMetres;
            return st;
        }

        private StrategyContext BuildContext(CarState st, RaceDirective.PopulationMember member) => new()
        {
            CarId = st.CarId,
            PilotProfile = PilotProfiles.For(member.Name),
            TrackName = "Fixed oval",
            TrackLengthM = _trackLen,
            TotalLaps = totalLaps,
            Corners = _corners,
        };

        private static Shader _carShader;

        /// <summary>Give the car body its palette colour so it reads the same in
        /// the 3D view, the HUD standings swatch and the team-radio panel. One
        /// material per car (they share the cube mesh).</summary>
        private static void TintCar(GameObject go, string hex)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;
            if (_carShader == null)
                _carShader = Shader.Find("Universal Render Pipeline/Unlit")
                             ?? Shader.Find("Unlit/Color")
                             ?? mr.sharedMaterial.shader;

            if (!ColorUtility.TryParseHtmlString(hex, out var c)) c = Color.gray;
            var m = new Material(_carShader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            m.color = c;
            mr.sharedMaterial = m;
        }

        private static CornerInfo[] BuildCornerMap(TrackData track)
        {
            var list = new List<CornerInfo>(track.Corners.Count);
            foreach (var c in track.Corners)
            {
                string dir = c.Direction == CornerDirection.Left ? "left" : "right";
                string sev = c.MinRadius >= 150f ? "fast"
                    : c.MinRadius >= 80f ? "medium"
                    : c.MinRadius >= 40f ? "slow" : "hairpin";
                list.Add(new CornerInfo(c.Index, dir, sev));
            }
            return list.ToArray();
        }

        // -- per-step race state --------------------------------------------

        private void FixedUpdate()
        {
            if (!_started || _finished) return;
            float now = Time.time;
            float dt = Time.fixedDeltaTime;

            // 1. advance each car's progress, speed EMA and s/f-line crossings.
            foreach (var st in _cars)
            {
                st.Progress.Update(st.Car.transform.position);
                st.AvgSpeed += (st.Car.ForwardSpeed - st.AvgSpeed) * Mathf.Clamp01(dt / 2f);

                float d = st.Progress.Distance01;
                if (st.CrossArmed && st.PrevDistance01 > 0.7f && d < 0.3f)
                {
                    HandleCrossing(st, now);
                    st.CrossArmed = false;
                }
                if (d > 0.5f) st.CrossArmed = true;
                st.PrevDistance01 = d;

                st.TotalArc = st.Crossings * _trackLen + st.Progress.ArcMetres - st.ArcPenalty;

                // Mirror the strategist's live directive into the pilot (§6.1).
                st.Agent.SetRaceDirective(st.Strategist.CurrentDirective);

                MaybeSoftRecover(st, dt);
            }

            // 2. classification + position-change events.
            UpdateClassification();

            // No more strategy calls once the chequered flag is out — the running
            // order just settles as backmarkers finish.
            if (!_chequered)
            {
                foreach (var st in _cars)
                {
                    if (st.Position == st.PrevPosition) continue;
                    st.Strategist.Notify(StrategyEvent.PositionChange, BuildSnapshot(st, StrategyEvent.PositionChange));
                    st.PrevPosition = st.Position;
                }

                // 3. sustained rival-in-range (§6.6: not a momentary crossing).
                for (int i = 0; i < _sorted.Count; i++)
                {
                    var st = _sorted[i];
                    if (i == 0)
                    {
                        st.RivalInRangeSince = -1f;
                        st.RivalRangeArmed = true;
                        continue;
                    }

                    var ahead = _sorted[i - 1];
                    float gapSec = (ahead.TotalArc - st.TotalArc) / Mathf.Max(8f, st.AvgSpeed);
                    if (gapSec < rivalEngageGapSeconds)
                    {
                        if (st.RivalInRangeSince < 0f) st.RivalInRangeSince = now;
                        if (st.RivalRangeArmed && now - st.RivalInRangeSince >= rivalEngageHoldSeconds)
                        {
                            st.RivalRangeArmed = false;
                            st.Strategist.Notify(StrategyEvent.RivalInRange, BuildSnapshot(st, StrategyEvent.RivalInRange));
                        }
                    }
                    else if (gapSec > rivalEngageGapSeconds * 1.6f)
                    {
                        st.RivalInRangeSince = -1f;
                        st.RivalRangeArmed = true;
                    }
                }
            }

            // 4. overlay tick (keeps flowing after the flag so the HUD shows the
            // backmarkers cross the line and stop).
            if (emitOverlay && now >= _nextTickAt)
            {
                _nextTickAt = now + Mathf.Max(0.05f, tickInterval);
                EmitTick();
            }
        }

        private void HandleCrossing(CarState st, float now)
        {
            if (st.Finished) return;

            st.Crossings++;
            st.TotalArc = st.Crossings * _trackLen + st.Progress.ArcMetres - st.ArcPenalty;

            if (st.Crossings == 1)
            {
                // First pass of the line from the grid = the start of lap 1.
                st.LapStartTime = now;
                return;
            }

            st.LastLapTime = now - st.LapStartTime;
            if (st.BestLapTime <= 0f || st.LastLapTime < st.BestLapTime) st.BestLapTime = st.LastLapTime;
            st.LapStartTime = now;
            st.LapsCompleted = st.Crossings - 1;

            st.Strategist.AddNote($"L{st.LapsCompleted}: {st.LastLapTime:F1}s, P{st.Position}");

            // Finish: completed every lap, or the chequered flag is already out and
            // this car has just crossed the line. Either way it stops here.
            if (st.LapsCompleted >= totalLaps || _chequered)
            {
                FinishCar(st, now);
                return;
            }

            bool startingFinalLap = st.LapsCompleted == totalLaps - 1 && !st.FinalLapFired;
            var evt = startingFinalLap ? StrategyEvent.FinalLap : StrategyEvent.LapCompleted;
            if (startingFinalLap) st.FinalLapFired = true;
            st.Strategist.Notify(evt, BuildSnapshot(st, evt));
        }

        /// <summary>The car has taken the chequered flag: park it, lock its
        /// classification place, and — if it is the winner — end the race for the
        /// HUD. The director keeps ticking until every car has finished so the
        /// backmarkers are seen crossing the line and stopping.</summary>
        private void FinishCar(CarState st, float now)
        {
            st.Finished = true;
            st.FinishOrder = ++_finishSeq;
            st.Agent.RaceStop();
            st.Strategist.AddNote($"Chequered flag — P{st.FinishOrder}, {st.LapsCompleted} laps");

            if (_finishSeq == 1)
            {
                _chequered = true;
                _winnerFinishTime = now;
                if (emitOverlay) EmitEnd();
            }
            st.FinishGap = now - _winnerFinishTime;

            UpdateClassification();
            if (emitOverlay) EmitTick();

            if (_cars.TrueForAll(c => c.Finished))
                _finished = true;
        }

        /// <summary>Watch for a car that is off the track, dead-stopped, or
        /// driving backwards for too long and, instead of a hard reset, put it
        /// back on the racing surface at its current arc with a time penalty
        /// (§6.3 note: the race is one long episode).</summary>
        private void MaybeSoftRecover(CarState st, float dt)
        {
            if (_finished || st.Finished) return;

            bool offTrack = Mathf.Abs(st.Progress.LateralOffset) > _halfWidth + offTrackMargin;
            st.OffTrackTime = offTrack ? st.OffTrackTime + dt : 0f;

            bool racing = st.Crossings >= 1 || st.AvgSpeed > 3f;
            bool stopped = racing && Mathf.Abs(st.Car.ForwardSpeed) < stuckSpeed;
            st.StuckTime = stopped ? st.StuckTime + dt : 0f;

            float align = Vector3.Dot(st.Car.transform.forward, st.Progress.Tangent);
            bool backwards = st.Crossings >= 1 && align < -0.3f && st.AvgSpeed > 4f;
            st.WrongWayTime = backwards ? st.WrongWayTime + dt : 0f;

            string reason =
                st.OffTrackTime > offTrackRecoverSeconds ? "off track" :
                st.WrongWayTime > wrongWayRecoverSeconds ? "wrong way" :
                st.StuckTime > stuckRecoverSeconds ? "stuck" : null;
            if (reason != null) SoftRecover(st, reason);
        }

        private void SoftRecover(CarState st, string reason)
        {
            st.Agent.RaceSoftRespawn();

            Vector3 p = st.Car.transform.position;
            st.Progress.Reset(p);
            st.PrevDistance01 = st.Progress.Distance01;
            st.CrossArmed = st.PrevDistance01 > 0.5f;   // don't score a phantom crossing

            // The reset saved the car the seconds it would have spent driving
            // back; dock that as lost track position so it rejoins where it
            // belongs, not ahead of it.
            st.ArcPenalty += softRecoverPenaltySeconds * Mathf.Max(8f, st.AvgSpeed);
            st.TotalArc = st.Crossings * _trackLen + st.Progress.ArcMetres - st.ArcPenalty;

            st.OffTrackTime = st.StuckTime = st.WrongWayTime = 0f;
            st.Incidents++;
            st.Strategist.AddNote(
                $"L{Mathf.Max(1, st.Crossings)}: {reason}, recovered (+{softRecoverPenaltySeconds:F0}s)");
            st.Strategist.Notify(StrategyEvent.Incident, BuildSnapshot(st, StrategyEvent.Incident));
        }

        private void UpdateClassification()
        {
            _sorted.Clear();
            _sorted.AddRange(_cars);
            // Finished cars hold their finish order at the front; everyone still
            // running is ranked behind them by track position.
            _sorted.Sort((a, b) =>
            {
                if (a.Finished && b.Finished) return a.FinishOrder.CompareTo(b.FinishOrder);
                if (a.Finished != b.Finished) return a.Finished ? -1 : 1;
                return b.TotalArc.CompareTo(a.TotalArc);
            });
            for (int i = 0; i < _sorted.Count; i++) _sorted[i].Position = i + 1;
        }

        // -- §6.3 telemetry -------------------------------------------------

        private TelemetrySnapshot BuildSnapshot(CarState st, StrategyEvent evt)
        {
            int idx = st.Position - 1;
            CarState ahead = idx > 0 && idx - 1 < _sorted.Count ? _sorted[idx - 1] : null;
            CarState behind = idx >= 0 && idx + 1 < _sorted.Count ? _sorted[idx + 1] : null;
            float spd = Mathf.Max(8f, st.AvgSpeed);

            float gapAhead = ahead != null ? (ahead.TotalArc - st.TotalArc) / spd : -1f;
            float gapBehind = behind != null ? (st.TotalArc - behind.TotalArc) / spd : -1f;

            var me = new SelfSnapshot(
                st.CarId, st.Position, st.LastLapTime, st.BestLapTime,
                gapAhead, gapBehind, st.Strategist.CurrentDirective.Kind, st.Incidents);

            var rivals = new List<RivalSnapshot>(Mathf.Max(0, _sorted.Count - 1));
            foreach (var o in _sorted)
            {
                if (o == st) continue;
                float gapSec = -(o.TotalArc - st.TotalArc) / spd;   // <0 = ahead of me
                float mag = Mathf.Abs(gapSec);
                bool seen = st.PrevRivalGap.TryGetValue(o.Slot, out float prev);
                GapTrend trend = !seen ? GapTrend.Stable
                    : mag < prev - 0.1f ? GapTrend.Closing
                    : mag > prev + 0.1f ? GapTrend.Dropping
                    : GapTrend.Stable;
                st.PrevRivalGap[o.Slot] = mag;
                rivals.Add(new RivalSnapshot(o.CarId, o.Position, gapSec, o.LastLapTime, trend));
            }

            return new TelemetrySnapshot
            {
                Event = evt,
                Lap = Mathf.Max(1, st.Crossings),
                LapsRemaining = Mathf.Max(0, totalLaps - st.LapsCompleted),
                Me = me,
                Rivals = rivals,
            };
        }

        // -- overlay (Unity -> DOM, §2.2) ---------------------------------

        private void EmitRaceStart()
        {
            var j = new JsonBuilder();
            j.Obj().Field("type", "race:start").Field("seed", _track.EffectiveSeed).Field("laps", totalLaps);
            j.Arr("cars");
            foreach (var st in _cars)
                j.Obj()
                    .Field("id", st.CarId)
                    .Field("name", st.Name)
                    .Field("color", st.Color)
                    .Field("pilot", st.Strategist.UseLlm ? "llm" : "heuristic")
                    .EndObj();
            j.EndArr().EndObj();
            JsBridge.Send(j.ToString());
        }

        private void EmitTick()
        {
            int lap = _sorted.Count > 0 ? Mathf.Clamp(_sorted[0].LapsCompleted + 1, 1, totalLaps) : 1;
            var j = new JsonBuilder();
            j.Obj().Field("type", "race:tick").Field("lap", lap).Field("laps", totalLaps);
            WriteMeId(j);
            j.Arr("classification");
            foreach (var st in _sorted) WriteClassRow(j, st, withDirective: true);
            j.EndArr().EndObj();
            JsBridge.Send(j.ToString());
        }

        private void EmitEnd()
        {
            var j = new JsonBuilder();
            j.Obj().Field("type", "race:end");
            WriteMeId(j);
            j.Arr("classification");
            foreach (var st in _sorted) WriteClassRow(j, st, withDirective: false);
            j.EndArr().EndObj();
            JsBridge.Send(j.ToString());
        }

        private void WriteMeId(JsonBuilder j)
        {
            string meId = CarIdOf(focusCarSlot);
            if (meId != null) j.Field("meId", meId); else j.Null("meId");
        }

        private void WriteClassRow(JsonBuilder j, CarState st, bool withDirective)
        {
            j.Obj().Field("pos", st.Position).Field("id", st.CarId).Field("name", st.Name);
            if (st.Position == 1 || _sorted.Count == 0) j.Null("gap");
            else if (st.Finished) j.Field("gap", st.FinishGap);
            else j.Field("gap", (_sorted[0].TotalArc - st.TotalArc) / Mathf.Max(8f, st.AvgSpeed));
            if (st.LastLapTime > 0f) j.Field("lastLap", st.LastLapTime); else j.Null("lastLap");
            if (st.Finished) j.Field("done", true);
            if (withDirective)
                j.Field("directive", st.Strategist.CurrentDirective.Kind.ToString().ToLowerInvariant());
            j.EndObj();
        }

        // -- per-car runtime state ----------------------------------------

        private sealed class CarState
        {
            public int Slot;          // grid slot this race (rotates)
            public int MemberIndex;   // RaceDirective.Population index (the stable pilot)
            public string CarId;      // "car_01" — follows MemberIndex, not Slot
            public string Name;       // population member name
            public string Color;

            public GameObject Go;
            public CarController Car;
            public RaceAgent Agent;
            public RaceStrategist Strategist;
            public TrackProgress Progress;

            public int Crossings;           // forward passes of the s/f line
            public int LapsCompleted;       // == max(0, Crossings - 1)
            public float TotalArc;          // Crossings * trackLen + arc into the current lap
            public float LapStartTime;
            public float LastLapTime;       // <=0 = unknown
            public float BestLapTime;       // <=0 = unknown
            public float AvgSpeed = 12f;    // EMA, for gap-in-seconds
            public bool FinalLapFired;

            public int Position;
            public int PrevPosition;
            public int Incidents;
            public bool Finished;
            public int FinishOrder;   // 1..N — order the chequered flag was taken
            public float FinishGap;   // seconds behind the winner at the flag

            public float ArcPenalty;      // metres of track position docked by soft recoveries
            public float OffTrackTime;
            public float StuckTime;
            public float WrongWayTime;

            public float PrevDistance01;
            public bool CrossArmed = true;
            public float RivalInRangeSince = -1f;
            public bool RivalRangeArmed = true;
            public readonly Dictionary<int, float> PrevRivalGap = new();
        }
    }

    /// <summary>
    /// One per race car (added by <see cref="RaceDirector.BuildCar"/>). Turns a
    /// car-to-car physics contact into a single <see cref="RaceDirector.ReportIncident"/>
    /// call, debounced so the multiple contact points of one bump don't each
    /// count. Wall contacts are ignored here — <see cref="RaceAgent"/> already
    /// handles those.
    /// </summary>
    public sealed class RaceCarContact : MonoBehaviour
    {
        internal int Slot;
        internal RaceDirector Director;

        private const float ContactCooldownSeconds = 1.5f;
        private float _muteUntil;

        private void OnCollisionEnter(Collision collision)
        {
            if (Director == null || Time.time < _muteUntil) return;

            var rb = collision.collider.attachedRigidbody;
            var other = rb != null ? rb.GetComponent<RaceCarContact>()
                                   : collision.collider.GetComponent<RaceCarContact>();
            if (other == null || other.Slot == Slot) return;

            _muteUntil = other._muteUntil = Time.time + ContactCooldownSeconds;
            Director.ReportIncident(Slot, other.Slot);
        }
    }
}
