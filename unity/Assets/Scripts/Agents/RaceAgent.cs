using AgenticRacing.Track;
using AgenticRacing.Vehicle;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace AgenticRacing.Agents
{
    /// <summary>
    /// ML-Agents driver policy for one car (CLAUDE.md Fase 2). It maps
    /// observations to the three continuous controls of a <see cref="CarController"/>
    /// — it does not reason. An episode is <b>one lap</b> (§2.1, §5): the agent
    /// spawns at a random point on the track and the episode ends when it has
    /// advanced a full lap length, leaves the track, gets stuck, or times out.
    ///
    /// The directive channels are part of the observation from day one and are
    /// randomised every episode (<see cref="RaceDirective.RandomEpisode"/>), so
    /// the policy learns to drive differently for different directives. In Fase 4
    /// the LLM strategist writes them instead. Training without them = retrain
    /// from scratch (§6.1, §11).
    /// </summary>
    [RequireComponent(typeof(CarController))]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class RaceAgent : Agent
    {
        /// <summary>Length of the CollectObservations vector. Keep
        /// BehaviorParameters.VectorObservationSize (set in TrainingArena) in sync.
        /// 2 speed + 4 track-relative + 3 curvature lookahead + 6 directive.</summary>
        public const int ObsSize = 15;

        // NOTE: TrainingArena builds the agent in code with no field overrides, so
        // these run with the values below, not with anything serialized. Tuning =
        // edit here + rebuild the training player (docs/Devlog.md 2026-09-06).
        //
        // race01-06 all plateaued at ~10% of a lap while the scripted controller
        // (same physics) drove 82%. It's an RL learning gap, not the environment:
        // the agent had no way to see corners coming and the reward pushed raw
        // speed. race07: 3 curvature-lookahead observations, and speed reward now
        // peaks at a curvature-appropriate TARGET speed (brake into corners pays).
        [Header("Reward shaping")]
        [SerializeField] private float progressRewardPerMetre = 0.02f;
        [SerializeField] private float speedRewardPerSec = 0.25f;      // peaks at the curvature-appropriate target speed
        [SerializeField] private float lineFollowRewardPerSec = 0.05f; // when moving, aligned, and near the racing line
        [SerializeField] private float slowPenaltyPerSec = 0.15f;      // extra, only when well under target speed
        [SerializeField] private float straightSpeedFrac = 0.42f;      // target speed as a fraction of MaxSpeed on a straight
        [SerializeField] private float cornerSpeedFrac = 0.12f;        // ...into the sharpest corners
        [SerializeField] private float edgeCreepPenaltyPerSec = 0.6f;
        [SerializeField] private float edgeSafeFrac = 0.55f;          // edge penalty ramps in past this fraction of the half-width
        [SerializeField] private float offTrackPenalty = 1.0f;
        [SerializeField] private float wallHitPenalty = 0.1f;
        [SerializeField] private float stuckPenaltyPerSec = 0.3f;     // while stopped (does NOT end the episode)
        [SerializeField] private float lapBonus = 12.0f;
        [SerializeField] private float fastLapBonus = 8.0f;           // extra, scaled by the MaxStep budget left at lap completion

        [Header("Episode limits")]
        [SerializeField] private float offTrackMargin = 2.0f;   // metres past the edge = fully off
        [SerializeField] private float stuckSpeed = 0.5f;       // m/s
        [SerializeField] private float stuckSeconds = 8.0f;     // hard cutoff only — a genuinely dead car, not a tactic
        [SerializeField] private float stallSeconds = 5.0f;     // window for the progress-stall check
        [SerializeField] private float stallMinMetres = 8.0f;   // must cover at least this much track per window
        [SerializeField] private float wrongWaySeconds = 4.5f;
        [SerializeField] private float spawnHeadingNoiseDeg = 10f;
        [SerializeField] private float spawnLateralNoise = 2.0f;
        [SerializeField] private float launchSpeed = 8.0f;      // m/s along the track at spawn — no dead-stop starts

        private CarController _car;
        private Rigidbody _rb;
        private TrainingArena _arena;
        private TrackData _track;
        private TrackProgress _progress;
        private System.Random _rng;

        private RaceDirective _directive;
        private float _halfWidth;
        private float _lapArc;
        private float _stuckTimer;
        private float _wrongWayTimer;
        private bool _stuckArmed;    // stuck check only bites once the car has actually got moving
        private float _stallArc, _stallTimer;   // progress-stall check state
        private bool _endReported;   // did this episode already fire AnyEpisodeEnded?
        private float _wallJamTimer, _escapeUntil, _steerSmooth;   // heuristic control state
        private int _episodeSteps;   // steps taken in the current lap/episode

        public override void Initialize()
        {
            _car = GetComponent<CarController>();
            _rb = GetComponent<Rigidbody>();
            _car.ReadKeyboard = false;
            // Unique per agent so parallel arenas don't run identical episodes
            // (System.Random's default seed is the shared tick count).
            _rng = new System.Random(GetInstanceID());

            // Fase 4 race scene: RaceDirector injects one shared circuit for the
            // whole grid (ExternalTrack) instead of a per-agent TrainingArena.
            _track = ExternalTrack;
            if (_track == null)
            {
                _arena = GetComponentInParent<TrainingArena>();
                if (_arena == null || _arena.Track == null)
                {
                    Debug.LogError("[RaceAgent] no track: needs a TrainingArena parent " +
                                   "(training/eval) or a RaceDirector-supplied ExternalTrack (race).");
                    return;
                }
                _track = _arena.Track;
            }
            _halfWidth = _track.Width * 0.5f;
            _progress = new TrackProgress(_track);
        }

        public override void OnEpisodeBegin()
        {
            if (RaceMode)
            {
                // One long episode = the whole race. No random respawn (the car
                // stays on the grid slot RaceDirector placed it on), no reward or
                // MaxStep-timeout bookkeeping. The soft-respawn / time-penalty
                // policy for a car that goes off or tangles is RaceDirector's job
                // (Fase 4 paso 3). Fires once, on activation.
                if (_track == null) return;
                _directive = InstanceDirective ?? RaceDirective.Neutral;
                _progress.Reset(_rb.position);
                _lapArc = 0f;
                _stuckTimer = 0f;
                _wrongWayTimer = 0f;
                _stuckArmed = false;
                _stallArc = 0f;
                _stallTimer = 0f;
                _wallJamTimer = 0f;
                _escapeUntil = 0f;
                _steerSmooth = 0f;
                _episodeSteps = 0;
                _endReported = false;
                return;
            }

            // If the previous episode ended without one of our explicit
            // EndEpisode() paths and ran ~the full budget, it timed out on
            // MaxStep — report it. (Guard on step count so a mid-episode policy
            // swap in the eval harness isn't miscounted as a timeout.)
            if (!_endReported && _track != null && _episodeSteps >= MaxStep - 5)
                ReportEpisodeEnd("maxStep");
            _endReported = false;
            _episodeSteps = 0;

            if (_track == null) return;

            _directive = InstanceDirective ?? ForcedDirective ?? RaceDirective.RandomEpisode(_rng);

            var center = _track.Centerline;
            int n = center.Count;
            int i = _rng.Next(n);
            Vector3 trackDir = (center[(i + 1) % n] - center[i]);
            trackDir.y = 0f;
            trackDir.Normalize();

            // CleanSpawn (set by the eval harness): on the CENTRELINE, aligned, at
            // speed — no heading/lateral noise. Must match what the heuristic
            // steers toward (the centre): spawning on the racing line, which the
            // heuristic's cross-track term then yanks toward the centre, sent the
            // car into an instant oscillation -> spin (Devlog 2026-09-07). The
            // noisy centre spawn is training-time episode diversity.
            float headNoise = CleanSpawn ? 0f : spawnHeadingNoiseDeg;
            float latNoise = CleanSpawn ? 0f : spawnLateralNoise;
            Vector3 fwd = Quaternion.Euler(0f, (float)(_rng.NextDouble() * 2 - 1) * headNoise, 0f) * trackDir;

            Vector3 side = new Vector3(-fwd.z, 0f, fwd.x);
            Vector3 spawn = center[i] + Vector3.up * 0.4f
                            + side * (float)((_rng.NextDouble() * 2 - 1) * latNoise);

            _car.PlaceAt(spawn, fwd);
            _car.Throttle = _car.Brake = _car.Steer = 0f;
            // Rolling start along the track. race01/race02 spawned at a dead stop
            // every episode and most episodes ended in ~3 s via the stuck check
            // before the car ever launched, so lap reward never got any credit.
            _rb.linearVelocity = trackDir * (CleanSpawn ? 14f : launchSpeed);

            _progress.Reset(_rb.position);
            _lapArc = 0f;
            _stuckTimer = 0f;
            _wrongWayTimer = 0f;
            _stuckArmed = false;
            _stallArc = 0f;
            _stallTimer = 0f;
            _wallJamTimer = 0f;
            _escapeUntil = 0f;
            _steerSmooth = 0f;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (_track == null) { for (int k = 0; k < ObsSize; k++) sensor.AddObservation(0f); return; }

            float maxSpeed = Mathf.Max(1f, _car.Config.MaxSpeed);
            Vector3 v = _rb.linearVelocity;
            Vector3 fwd = transform.forward;

            sensor.AddObservation(Vector3.Dot(v, fwd) / maxSpeed);          // forward speed
            sensor.AddObservation(Vector3.Dot(v, transform.right) / maxSpeed); // lateral speed

            // Heading error vs the racing line's local direction, and how far the
            // car and the racing line each sit off the centerline.
            var center = _track.Centerline;
            var line = _track.RacingLine;
            int n = center.Count;
            int s = _progress.NearestSample;
            Vector3 rlTan = line[(s + 1) % n] - line[(s - 1 + n) % n];
            rlTan.y = 0f;
            if (rlTan.sqrMagnitude > 1e-6f) rlTan.Normalize(); else rlTan = fwd;

            sensor.AddObservation(Vector3.SignedAngle(fwd, rlTan, Vector3.up) / 180f);
            sensor.AddObservation(Mathf.Clamp(_progress.LateralOffset / _halfWidth, -2f, 2f));

            Vector3 rlRel = line[s] - center[s];
            Vector3 left = new Vector3(-_progress.Tangent.z, 0f, _progress.Tangent.x);
            sensor.AddObservation(Mathf.Clamp(Vector3.Dot(rlRel, left) / _halfWidth, -2f, 2f));

            sensor.AddObservation(_progress.Distance01);

            // Curvature lookahead — the agent needs to see corners coming to brake
            // for them (race01-06: no anticipation -> off-track at the first bend;
            // the scripted controller, which scans ahead, drove 82% of a lap).
            sensor.AddObservation(Mathf.Clamp(CenterlineTurnDeg(0f, 22f) / 90f, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(CenterlineTurnDeg(18f, 45f) / 90f, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(CenterlineTurnDeg(40f, 75f) / 90f, -1f, 1f));

            // Directive channels (§6.1) — 6 floats.
            sensor.AddObservation(_directive.Aggression);
            sensor.AddObservation(_directive.RiskTolerance);
            for (int k = 0; k < 4; k++)
                sensor.AddObservation(_directive.Kind == (DirectiveKind)k ? 1f : 0f);
        }

        /// <summary>
        /// Signed heading change (deg) of the centreline between <paramref name="fromM"/>
        /// and <paramref name="toM"/> metres ahead of the car's nearest sample.
        /// + = the track turns left. Shared by the observations, the target-speed
        /// reward, and the heuristic.
        /// </summary>
        private float CenterlineTurnDeg(float fromM, float toM)
        {
            var c = _track.Centerline;
            int n = c.Count;
            float spacing = Mathf.Max(0.1f, _track.Length / n);
            int s = _progress.NearestSample;
            int i0 = s + Mathf.RoundToInt(fromM / spacing);
            int i1 = s + Mathf.RoundToInt(toM / spacing);
            Vector3 d0 = c[(i0 + 1) % n] - c[i0 % n];
            Vector3 d1 = c[(i1 + 1) % n] - c[i1 % n];
            d0.y = d1.y = 0f;
            return Vector3.SignedAngle(d0, d1, Vector3.up);
        }

        /// <summary>Curvature-appropriate speed for what's just ahead: fast on a
        /// straight, low into the sharpest nearby corner (mirrors the heuristic).</summary>
        private float TargetSpeed()
        {
            float turn = Mathf.Max(Mathf.Abs(CenterlineTurnDeg(0f, 30f)),
                                   Mathf.Abs(CenterlineTurnDeg(20f, 55f)));
            return _car.Config.MaxSpeed *
                   Mathf.Lerp(straightSpeedFrac, cornerSpeedFrac, Mathf.Clamp01(turn / 55f));
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            _episodeSteps++;
            if (_track == null) return;

            var a = actions.ContinuousActions;
            _car.Steer = Mathf.Clamp(a[0], -1f, 1f);
            _car.Throttle = Mathf.Clamp(a[1], -1f, 1f);
            _car.Brake = Mathf.Clamp01(a[2]);

            if (RaceMode)
            {
                // Pilot only: feed the controls and keep _progress current for
                // Heuristic() and CollectObservations. Lap counting, gaps and any
                // respawn policy are RaceDirector's; nothing here ends the race.
                _progress.Update(_rb.position);
                return;
            }

            _progress.Update(_rb.position);
            float fwdMetres = _progress.ConsumeForwardDelta();

            AddReward(fwdMetres * progressRewardPerMetre);

            // Speed shaping around a curvature-appropriate TARGET speed: reward
            // peaks at target and falls off both ways, so braking into a corner
            // pays and overcooking it doesn't. race01-06 rewarded raw speed and
            // the policy never learned to brake (eval: meanBrake ~0.02).
            float maxSpeed = Mathf.Max(1f, _car.Config.MaxSpeed);
            float fwdFrac = Mathf.Clamp01(_car.ForwardSpeed / maxSpeed);
            float target = TargetSpeed();
            float speedErr = (_car.ForwardSpeed - target) / Mathf.Max(1f, target); // signed, <0 = too slow
            AddReward(speedRewardPerSec * Mathf.Clamp01(1f - Mathf.Abs(speedErr) * 1.3f) * Time.fixedDeltaTime);
            if (speedErr < -0.3f)
                AddReward(-slowPenaltyPerSec * (-speedErr - 0.3f) * Time.fixedDeltaTime);

            if (fwdFrac > 0.05f)
            {
                var line = _track.RacingLine;
                var centerline = _track.Centerline;
                int m = centerline.Count;
                int s = _progress.NearestSample;
                Vector3 rlTan = line[(s + 1) % m] - line[(s - 1 + m) % m];
                rlTan.y = 0f;
                float headingErr01 = rlTan.sqrMagnitude > 1e-6f
                    ? Mathf.Abs(Vector3.SignedAngle(transform.forward, rlTan, Vector3.up)) / 180f
                    : 0f;
                Vector3 left = new Vector3(-_progress.Tangent.z, 0f, _progress.Tangent.x);
                float rlOffset = Vector3.Dot(line[s] - centerline[s], left);
                float lineDist01 = Mathf.Clamp01(Mathf.Abs(_progress.LateralOffset - rlOffset) / _halfWidth);
                float follow = (1f - Mathf.Clamp01(headingErr01 * 3f)) * (1f - lineDist01);
                AddReward(lineFollowRewardPerSec * follow * fwdFrac * Time.fixedDeltaTime);
            }

            // Edge avoidance: smooth penalty that ramps in from edgeSafeFrac of the
            // half-width out to the edge, then the old hard penalty past it. The
            // racing line hugs the walls, so rewarding line-following alone pushed
            // the car into them (Devlog 2026-09-07) — this pulls it back toward a
            // safe corridor.
            float absLat = Mathf.Abs(_progress.LateralOffset);
            float edgeT = Mathf.InverseLerp(edgeSafeFrac * _halfWidth, _halfWidth, absLat);
            if (edgeT > 0f)
                AddReward(-edgeCreepPenaltyPerSec * edgeT * Time.fixedDeltaTime);
            if (absLat > _halfWidth + offTrackMargin)
            {
                AddReward(-offTrackPenalty);
                ReportEpisodeEnd("offTrack");
                EndEpisode();
                return;
            }

            // Stuck is a per-second penalty, NOT an episode end — ending on stuck
            // was an escape hatch (race01-04: creep a bit, then stop to cash the
            // one-off penalty and reset). It only arms once the car has genuinely
            // moved, and only hard-terminates after a long stall (a dead car, not
            // a tactic).
            if (_car.ForwardSpeed > stuckSpeed) _stuckArmed = true;
            if (_stuckArmed && Mathf.Abs(_car.ForwardSpeed) < stuckSpeed) _stuckTimer += Time.fixedDeltaTime;
            else _stuckTimer = 0f;
            if (_stuckArmed && _stuckTimer > 0f)
                AddReward(-stuckPenaltyPerSec * Time.fixedDeltaTime);
            if (_stuckTimer > stuckSeconds)
            {
                AddReward(-offTrackPenalty);
                ReportEpisodeEnd("stuck");
                EndEpisode();
                return;
            }

            // Wrong-way only arms after the car has actually made progress — a
            // short reverse to unstick from a wall must not end the episode.
            if (_lapArc > 15f && fwdMetres < -0.15f) _wrongWayTimer += Time.fixedDeltaTime;
            else _wrongWayTimer = 0f;
            if (_wrongWayTimer > wrongWaySeconds)
            {
                AddReward(-offTrackPenalty);
                ReportEpisodeEnd("wrongWay");
                EndEpisode();
                return;
            }

            // Progress stall: covered < stallMinMetres of track in the last
            // stallSeconds. Catches the sawtooth-near-spawn and slow circling that
            // the speed-based 'stuck' check misses (brief reverse bursts keep
            // resetting it). Independent of instantaneous speed.
            _stallTimer += Time.fixedDeltaTime;
            if (_stallTimer >= stallSeconds)
            {
                if (_episodeSteps > 60 && _lapArc - _stallArc < stallMinMetres)
                {
                    AddReward(-offTrackPenalty);
                    ReportEpisodeEnd("stall");
                    EndEpisode();
                    return;
                }
                _stallArc = _lapArc;
                _stallTimer = 0f;
            }

            _lapArc += fwdMetres;
            if (_lapArc >= _track.Length * 0.99f)
            {
                // Flat bonus for finishing the lap, plus a bonus for how much of
                // the MaxStep time budget is left — i.e. for finishing it fast.
                float budgetLeft = MaxStep > 0 ? Mathf.Clamp01(1f - _episodeSteps / (float)MaxStep) : 0f;
                AddReward(lapBonus + fastLapBonus * budgetLeft);
                ReportEpisodeEnd("lap");
                EndEpisode();
            }
        }

        /// <summary>Fired on every episode end with (agent, reason, steps, lapArc
        /// metres). Reasons: lap / offTrack / stuck / wrongWay / stall / maxStep.
        /// The offline eval harness (<see cref="EvalRunner"/>) aggregates these;
        /// the agent ref lets its <c>-population</c> mode attribute lap times to a
        /// specific pilot.</summary>
        internal static event System.Action<RaceAgent, string, int, float> AnyEpisodeEnded;

        /// <summary>Eval harness: spawn on the centreline, aligned, at speed — no
        /// training-time heading/lateral noise.</summary>
        internal static bool CleanSpawn;

        /// <summary>Eval harness override applied to <b>every</b> agent: when set,
        /// each episode uses this directive instead of a random one
        /// (`eval.exe -directive attack`).</summary>
        internal static RaceDirective? ForcedDirective;

        /// <summary>Eval harness per-agent override (Fase 3 <c>-population</c>):
        /// this one agent runs a fixed population member. Takes precedence over
        /// <see cref="ForcedDirective"/>.</summary>
        internal RaceDirective? InstanceDirective;

        /// <summary>Directive→controller mapping (§6.5). Left null in the
        /// training/eval arenas (they use <see cref="StrategyDirectiveMap.Default"/>);
        /// the Fase 4 race scene assigns a shared calibrated asset.</summary>
        internal StrategyDirectiveMap DirectiveMap;

        /// <summary>Fase 4 race scene: the one shared circuit for the whole grid,
        /// injected by RaceDirector before the agent activates (a race has a
        /// single track, not a per-agent TrainingArena). Null in training/eval.</summary>
        internal TrackData ExternalTrack;

        /// <summary>Fase 4 race scene: run as a pure pilot for one long race —
        /// no random per-episode respawn, no reward shaping, no stall/stuck/
        /// off-track/lap <see cref="Agent.EndEpisode"/>. Set together with
        /// <see cref="ExternalTrack"/> and <see cref="InstanceDirective"/>.</summary>
        internal bool RaceMode;

        /// <summary>Fase 4 race scene: the strategist pushes its live directive
        /// here every tick so the pilot's driving — and the directive channels in
        /// its observation vector (§6.1) — track the pit wall. No-op outside race
        /// mode; training/eval set the directive once per episode instead.</summary>
        internal void SetRaceDirective(RaceDirective directive)
        {
            if (RaceMode) _directive = directive;
        }

        private void ReportEpisodeEnd(string reason)
        {
            _endReported = true;
            AnyEpisodeEnded?.Invoke(this, reason, _episodeSteps, _lapArc);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.gameObject.CompareTag(TrackEdgeColliders.EdgeTag))
                AddReward(-wallHitPenalty);
        }

        /// <summary>
        /// Autonomous path follower: pure-pursuit steering toward a speed-scaled
        /// lookahead point on a centre-biased blend of centreline and racing line
        /// (the pure racing line hugs the walls — too little error margin), plus
        /// brake-into / accelerate-out speed control from the sharpest heading
        /// change of the line ahead, and a reverse-off-the-wall recovery. Not used
        /// in training; it is the "can this track be driven?" reference (eval
        /// harness <c>-heuristic</c>) and the seed of the Fase 6.3 fixed strategy.
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var a = actionsOut.ContinuousActions;
            if (_track == null) { a[0] = a[1] = a[2] = 0f; return; }

            var line = _track.RacingLine;
            var center = _track.Centerline;
            int n = line.Count;
            int s = _progress.NearestSample;
            float spacing = Mathf.Max(0.1f, _track.Length / n);
            float speed = Mathf.Max(0f, _car.ForwardSpeed);
            float maxSpeed = Mathf.Max(1f, _car.Config.MaxSpeed);

            Vector3 left = new Vector3(-_progress.Tangent.z, 0f, _progress.Tangent.x);
            float carOffLeft = Vector3.Dot(_rb.position - center[s], left); // + = car left of centre
            Vector3 trackDir = _progress.Tangent;

            // Recovery. Two cases:
            //  - Slow but NOT against a wall -> just re-align with the local track
            //    direction and floor it FORWARD. Reversing here was the death
            //    spiral in the -heuristic eval: reverse -> no lap progress ->
            //    stall check kills it (end# speeds were negative).
            //  - Slow AND jammed against an edge -> a short reverse burst is the
            //    only way off; time-boxed so it can't loop.
            bool slow = speed < 1.5f;
            bool againstWall = Mathf.Abs(carOffLeft) > 0.75f * _halfWidth;
            float noseErr = Vector3.SignedAngle(transform.forward, trackDir, Vector3.up);

            _wallJamTimer = (slow && againstWall) ? _wallJamTimer + Time.fixedDeltaTime : 0f;
            if (Time.time < _escapeUntil || _wallJamTimer > 1.0f)
            {
                if (_wallJamTimer > 1.0f) { _escapeUntil = Time.time + 0.6f; _wallJamTimer = 0f; }
                a[0] = Mathf.Clamp(-noseErr / 20f, -1f, 1f); // reversing inverts steer sense
                a[1] = -1f;
                a[2] = 0f;
                return;
            }
            if (slow)
            {
                _steerSmooth = a[0] = Mathf.Clamp((noseErr - carOffLeft * 2f) / 18f, -1f, 1f);
                a[1] = 1f;
                a[2] = 0f;
                return;
            }

            // Sharpest heading change of the CENTRELINE anywhere in the next ~60 m
            // => the corner we're about to reach => target entry speed. (Scan the
            // centreline, not the racing line, since we drive down the middle now;
            // the racing line's apex-cut reads gentler than the corner really is.)
            int scan = Mathf.Max(2, Mathf.RoundToInt(60f / spacing));
            int seg = Mathf.Max(1, Mathf.RoundToInt(8f / spacing));
            float turnAheadDeg = 0f;
            for (int k = 0; k < scan; k += seg)
            {
                Vector3 e0 = center[(s + k + 1) % n] - center[(s + k) % n];
                Vector3 e1 = center[(s + k + seg + 1) % n] - center[(s + k + seg) % n];
                e0.y = e1.y = 0f;
                turnAheadDeg = Mathf.Max(turnAheadDeg, Mathf.Abs(Vector3.SignedAngle(e0, e1, Vector3.up)));
            }
            // --- Directive modulation (§6.5) -----------------------------------
            // The strategist's only write surface. The mapping directive ->
            // (speed scale, line bias, braking margin) lives in a single
            // StrategyDirectiveMap so the RL/heuristic pilot and the Fase 4 LLM
            // strategist agree on exactly what a directive does. These are the
            // same channels the policy sees in its observations (§6.1).
            float agg = Mathf.Clamp01(_directive.Aggression);
            var mod = (DirectiveMap != null ? DirectiveMap : StrategyDirectiveMap.Default)
                      .Resolve(_directive);
            float aggSpeed = mod.SpeedScale;
            float centreBlend = mod.CentreBlend;
            // -----------------------------------------------------------------

            float targetSpeed = Mathf.Lerp(maxSpeed * 0.40f, maxSpeed * 0.10f,
                                           Mathf.Clamp01(turnAheadDeg / 45f)) * aggSpeed;
            float brakeMargin = mod.BrakeMargin;   // brake later when aggressive

            if (speed < targetSpeed - 0.5f) { a[1] = 1f; a[2] = 0f; }
            else if (speed > targetSpeed + brakeMargin) { a[1] = 0f; a[2] = Mathf.Clamp01((speed - targetSpeed) / 3f); }
            else { a[1] = Mathf.Lerp(0.45f, 0.9f, agg); a[2] = 0f; }   // more throttle out of the corner

            // Pure-pursuit steering, low-pass filtered. Earlier: gain /11, no
            // filter, short lookahead -> the steer command slammed +-1 and the
            // car oscillated itself off the track. Now: longer lookahead, softer
            // gain, and a first-order filter on the command.
            float lookaheadM = Mathf.Clamp(12f + speed * 0.9f, 12f, 36f);
            int laSteps = Mathf.Max(1, Mathf.RoundToInt(lookaheadM / spacing));
            Vector3 aim = Vector3.Lerp(line[(s + laSteps) % n], center[(s + laSteps) % n], centreBlend);
            Vector3 toTarget = aim - _rb.position;
            toTarget.y = 0f;
            float headingErrDeg = Vector3.SignedAngle(transform.forward, toTarget, Vector3.up);
            float crossCorrDeg = Mathf.Clamp(carOffLeft * 1.4f, -13f, 13f);  // left of centre -> steer right (+)
            float rawSteer = Mathf.Clamp((headingErrDeg + crossCorrDeg) / 16f, -1f, 1f);
            _steerSmooth = Mathf.Lerp(_steerSmooth, rawSteer, 0.35f);
            a[0] = _steerSmooth;
        }
    }
}
