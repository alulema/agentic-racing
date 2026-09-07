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
        private bool _diagCounted;   // did EndDiag already tally the current episode?
        private float _wallJamTimer, _escapeUntil;   // heuristic reverse-off-the-wall state

        private int _episodeSteps;   // steps taken in the current lap/episode

        // Fase 2 bring-up diagnostics. Remove once training is stable.
        private static bool _loggedInit, _loggedObs;
        private static int _episodeCount, _actionCount;

        public override void Initialize()
        {
            _car = GetComponent<CarController>();
            _rb = GetComponent<Rigidbody>();
            _car.ReadKeyboard = false;
            // Unique per agent so parallel arenas don't run identical episodes
            // (System.Random's default seed is the shared tick count).
            _rng = new System.Random(GetInstanceID());

            _arena = GetComponentInParent<TrainingArena>();
            if (_arena == null || _arena.Track == null)
            {
                Debug.LogError("[RaceAgent] no TrainingArena with a built track in the parents.");
                return;
            }
            _track = _arena.Track;
            _halfWidth = _track.Width * 0.5f;
            _progress = new TrackProgress(_track);

            if (!_loggedInit)
            {
                _loggedInit = true;
                Debug.Log($"[RaceAgent] Initialize ok: car={_car != null} rb={_rb != null} " +
                          $"centerline={_track.Centerline?.Count ?? -1} racingLine={_track.RacingLine?.Count ?? -1} " +
                          $"width={_track.Width} maxStep={MaxStep}");
            }
        }

        public override void OnEpisodeBegin()
        {
            int ep = ++_episodeCount;
            if (ep <= 15 || ep % 200 == 0)
                Debug.Log($"[RaceAgent] OnEpisodeBegin #{ep}: track={_track != null} " +
                          $"stepCount={StepCount} prevEpisodeSteps={_episodeSteps}");
            // If the previous episode ended without one of our explicit
            // EndEpisode() paths AND ran ~the full budget, it timed out on
            // MaxStep — count it. (Guard on step count so a mid-episode policy
            // swap in the eval harness isn't miscounted as a timeout.)
            if (ep > 1 && !_diagCounted && _track != null && _episodeSteps >= MaxStep - 5)
                EndDiag("maxStep", _episodeSteps);
            _diagCounted = false;
            _episodeSteps = 0;

            if (_track == null) return;

            _directive = RaceDirective.RandomEpisode(_rng);

            var center = _track.Centerline;
            int n = center.Count;
            int i = _rng.Next(n);
            Vector3 trackDir = (center[(i + 1) % n] - center[i]);
            trackDir.y = 0f;
            trackDir.Normalize();
            Vector3 fwd = Quaternion.Euler(0f, (float)(_rng.NextDouble() * 2 - 1) * spawnHeadingNoiseDeg, 0f) * trackDir;

            Vector3 side = new Vector3(-fwd.z, 0f, fwd.x);
            Vector3 spawn = center[i] + Vector3.up * 0.4f
                            + side * (float)((_rng.NextDouble() * 2 - 1) * spawnLateralNoise);

            _car.PlaceAt(spawn, fwd);
            _car.Throttle = _car.Brake = _car.Steer = 0f;
            // Rolling start along the track. race01/race02 spawned at a dead stop
            // every episode and most episodes ended in ~3 s via the stuck check
            // before the car ever launched, so lap reward never got any credit.
            _rb.linearVelocity = trackDir * launchSpeed;

            _progress.Reset(_rb.position);
            _lapArc = 0f;
            _stuckTimer = 0f;
            _wrongWayTimer = 0f;
            _stuckArmed = false;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!_loggedObs)
            {
                _loggedObs = true;
                var bp = GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                var dr = GetComponent<Unity.MLAgents.DecisionRequester>();
                Debug.Log($"[RaceAgent] CollectObservations (first): track={_track != null} " +
                          $"car={_car != null} progress={_progress != null} " +
                          $"behaviorType={bp?.BehaviorType} actionSpec=C{bp?.BrainParameters.ActionSpec.NumContinuousActions}" +
                          $"/D{bp?.BrainParameters.ActionSpec.NumDiscreteActions} " +
                          $"decisionRequester={(dr != null ? "present" : "MISSING")} drAgent={(dr != null && dr.Agent != null)} " +
                          $"sensors=[{DumpSensors()}]");
            }

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

        // Dumps the base Agent's private sensor list (name + flattened shape) so
        // we can see duplicates / ordering. Reflection because `sensors` is
        // internal to the ML-Agents assembly. Diagnostics only.
        private string DumpSensors()
        {
            try
            {
                var f = typeof(Agent).GetField("sensors",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (!(f?.GetValue(this) is System.Collections.IEnumerable list)) return "reflect-fail";
                var parts = new System.Collections.Generic.List<string>();
                foreach (var o in list)
                {
                    if (o is ISensor si)
                    {
                        var shape = si.GetObservationSpec().Shape;
                        var dims = new int[shape.Length];
                        for (int d = 0; d < shape.Length; d++) dims[d] = shape[d];
                        parts.Add($"{si.GetName()}({string.Join("x", dims)})");
                    }
                }
                return string.Join(", ", parts);
            }
            catch (System.Exception e) { return "err:" + e.Message; }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            int ac = ++_actionCount;
            _episodeSteps++;
            if (ac == 1 || ac % 20000 == 0)
                Debug.Log($"[RaceAgent] OnActionReceived #{ac}: track={_track != null} " +
                          $"stepCount={StepCount} fwdSpeed={(_car != null ? _car.ForwardSpeed : 0f):F2}");

            if (_track == null) return;

            var a = actions.ContinuousActions;
            _car.Steer = Mathf.Clamp(a[0], -1f, 1f);
            _car.Throttle = Mathf.Clamp(a[1], -1f, 1f);
            _car.Brake = Mathf.Clamp01(a[2]);

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
                EndDiag("offTrack", absLat);
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
                EndDiag("stuck", _stuckTimer);
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
                EndDiag("wrongWay", _wrongWayTimer);
                EndEpisode();
                return;
            }

            _lapArc += fwdMetres;
            if (_lapArc >= _track.Length * 0.99f)
            {
                // Flat bonus for finishing the lap, plus a bonus for how much of
                // the MaxStep time budget is left — i.e. for finishing it fast.
                float budgetLeft = MaxStep > 0 ? Mathf.Clamp01(1f - _episodeSteps / (float)MaxStep) : 0f;
                AddReward(lapBonus + fastLapBonus * budgetLeft);
                EndDiag("lap", _lapArc);
                EndEpisode();
            }
        }

        // Running tally of how episodes end, across all agents in the process
        // (stuck / offTrack / wrongWay / lap / maxStep). Logged as a rolling
        // window of the last N so the split is visible as training progresses,
        // not just a lifetime average dominated by the bad early episodes.
        private const int EndWindow = 400;
        private static readonly System.Collections.Generic.Queue<string> _endRecent = new();
        private static int _endTotal;
        private static float _lapArcSum;

        /// <summary>Fired on every episode end: (reason, steps, lapArc metres).
        /// Used by the offline eval harness (<see cref="EvalRunner"/>).</summary>
        internal static event System.Action<string, int, float> AnyEpisodeEnded;

        private void EndDiag(string reason, float value)
        {
            _diagCounted = true;
            AnyEpisodeEnded?.Invoke(reason, _episodeSteps, _lapArc);
            _endTotal++;
            _lapArcSum += _lapArc;

            // First ~24 endings: where and how did it die? Pins down whether the
            // policy/heuristic dies at a repeatable spot on the lap (a specific
            // corner) and in what state.
            if (_endTotal <= 24)
                Debug.Log($"[RaceAgent] end #{_endTotal} '{reason}': lap%={_progress.Distance01 * 100f:F0} " +
                          $"lapArc={_lapArc:F0}m steps={_episodeSteps} speed={_car.ForwardSpeed:F1} " +
                          $"lateral={_progress.LateralOffset:F1}/{_halfWidth:F1}m");
            _endRecent.Enqueue(reason);
            while (_endRecent.Count > EndWindow) _endRecent.Dequeue();
            if (_endTotal % EndWindow == 0)
            {
                var counts = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var r in _endRecent) { counts.TryGetValue(r, out int c); counts[r] = c + 1; }
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in counts) parts.Add($"{kv.Key}={100f * kv.Value / _endRecent.Count:F0}%");
                Debug.Log($"[RaceAgent] end reasons @ {_endTotal} (last {_endRecent.Count}): " +
                          $"{string.Join(", ", parts)} | lifetime mean lapArc={_lapArcSum / _endTotal:F0}m");
            }
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

            // Reverse-off-the-wall, tightly gated so it can't become a low-speed
            // limit cycle (the first version reversed on any slow moment near the
            // edge and never re-accelerated). Only fires when genuinely pinned —
            // almost stopped AND hard against an edge for a sustained moment —
            // then reverses for one short time-boxed burst.
            bool pinned = speed < 1f && Mathf.Abs(carOffLeft) > 0.8f * _halfWidth;
            _wallJamTimer = pinned ? _wallJamTimer + Time.fixedDeltaTime : 0f;
            if (Time.time < _escapeUntil || _wallJamTimer > 0.5f)
            {
                if (_wallJamTimer > 0.5f) { _escapeUntil = Time.time + 0.8f; _wallJamTimer = 0f; }
                a[0] = Mathf.Sign(carOffLeft) * 0.4f;
                a[1] = -1f;
                a[2] = 0f;
                return;
            }

            // Pure-pursuit toward a centre-biased lookahead point.
            float lookaheadM = Mathf.Clamp(10f + speed * 1.2f, 12f, 55f);
            int laSteps = Mathf.Max(1, Mathf.RoundToInt(lookaheadM / spacing));
            Vector3 aim = Vector3.Lerp(line[(s + laSteps) % n], center[(s + laSteps) % n], 0.5f);
            Vector3 toTarget = aim - _rb.position;
            toTarget.y = 0f;
            float headingErrDeg = Vector3.SignedAngle(transform.forward, toTarget, Vector3.up);

            float crossCorrDeg = Mathf.Clamp(carOffLeft * 3f, -25f, 25f);  // left of centre -> steer right (+)

            a[0] = Mathf.Clamp((headingErrDeg + crossCorrDeg) / 14f, -1f, 1f);

            // Sharpest heading change of the line anywhere in the next ~55 m =>
            // the corner we are about to reach => target entry speed.
            int scan = Mathf.Max(2, Mathf.RoundToInt(55f / spacing));
            int seg = Mathf.Max(1, Mathf.RoundToInt(8f / spacing));
            float turnAheadDeg = 0f;
            for (int k = 0; k < scan; k += seg)
            {
                Vector3 e0 = line[(s + k + 1) % n] - line[(s + k) % n];
                Vector3 e1 = line[(s + k + seg + 1) % n] - line[(s + k + seg) % n];
                e0.y = e1.y = 0f;
                turnAheadDeg = Mathf.Max(turnAheadDeg, Mathf.Abs(Vector3.SignedAngle(e0, e1, Vector3.up)));
            }
            float targetSpeed = Mathf.Lerp(maxSpeed * 0.42f, maxSpeed * 0.11f,
                                           Mathf.Clamp01(turnAheadDeg / 55f));

            if (speed < targetSpeed - 1f) { a[1] = 1f; a[2] = 0f; }
            else if (speed > targetSpeed + 1f) { a[1] = 0f; a[2] = Mathf.Clamp01((speed - targetSpeed) / 5f); }
            else { a[1] = 0.4f; a[2] = 0f; }
        }
    }
}
