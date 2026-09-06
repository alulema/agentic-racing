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
        // NOTE: TrainingArena builds the agent in code with no field overrides, so
        // these run with the values below, not with anything serialized. Tuning =
        // edit here + rebuild the training player (docs/Devlog.md 2026-09-06).
        [Header("Reward shaping")]
        [SerializeField] private float progressRewardPerMetre = 0.02f;
        [SerializeField] private float speedRewardPerSec = 0.15f;     // scaled by forward-speed fraction
        [SerializeField] private float lineFollowRewardPerSec = 0.10f; // when moving, aligned, and near the racing line
        [SerializeField] private float timePenaltyPerStep = 0.0005f;
        [SerializeField] private float edgeCreepPenaltyPerSec = 0.5f;
        [SerializeField] private float offTrackPenalty = 1.0f;
        [SerializeField] private float wallHitPenalty = 0.1f;
        [SerializeField] private float stuckPenalty = 1.0f;
        [SerializeField] private float lapBonus = 12.0f;
        [SerializeField] private float fastLapBonus = 8.0f;           // extra, scaled by the MaxStep budget left at lap completion

        [Header("Episode limits")]
        [SerializeField] private float offTrackMargin = 2.0f;   // metres past the edge = fully off
        [SerializeField] private float stuckSpeed = 0.5f;       // m/s
        [SerializeField] private float stuckSeconds = 3.0f;
        [SerializeField] private float wrongWaySeconds = 2.5f;
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
            // If the previous episode ended without hitting one of our explicit
            // EndEpisode() paths, it timed out on MaxStep — count it so the tally
            // reflects the real split.
            if (ep > 1 && !_diagCounted && _track != null) EndDiag("maxStep", _episodeSteps);
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

            if (_track == null) { for (int k = 0; k < 12; k++) sensor.AddObservation(0f); return; }

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

            // Directive channels (§6.1) — 6 floats.
            sensor.AddObservation(_directive.Aggression);
            sensor.AddObservation(_directive.RiskTolerance);
            for (int k = 0; k < 4; k++)
                sensor.AddObservation(_directive.Kind == (DirectiveKind)k ? 1f : 0f);
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
            AddReward(-timePenaltyPerStep);

            // Speed and racing-line shaping. Both scale with the forward-speed
            // fraction so they cannot be farmed at a standstill (that was the
            // race01 failure mode: the policy settled for "creep forward safely").
            float maxSpeed = Mathf.Max(1f, _car.Config.MaxSpeed);
            float fwdFrac = Mathf.Clamp01(_car.ForwardSpeed / maxSpeed);
            AddReward(speedRewardPerSec * fwdFrac * Time.fixedDeltaTime);

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

            float absLat = Mathf.Abs(_progress.LateralOffset);
            if (absLat > _halfWidth)
                AddReward(-edgeCreepPenaltyPerSec * Time.fixedDeltaTime);
            if (absLat > _halfWidth + offTrackMargin)
            {
                AddReward(-offTrackPenalty);
                EndDiag("offTrack", absLat);
                EndEpisode();
                return;
            }

            // Only let the stuck check bite after the car has genuinely got going
            // at least once — a fumbled launch shouldn't end the episode, a
            // mid-track stall should.
            if (_car.ForwardSpeed > stuckSpeed) _stuckArmed = true;
            if (_stuckArmed && Mathf.Abs(_car.ForwardSpeed) < stuckSpeed) _stuckTimer += Time.fixedDeltaTime;
            else _stuckTimer = 0f;
            if (_stuckTimer > stuckSeconds)
            {
                AddReward(-stuckPenalty);
                EndDiag("stuck", _stuckTimer);
                EndEpisode();
                return;
            }

            if (fwdMetres < -0.15f) _wrongWayTimer += Time.fixedDeltaTime;
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
        private void EndDiag(string reason, float value)
        {
            _diagCounted = true;
            _endTotal++;
            _lapArcSum += _lapArc;
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

#if ENABLE_LEGACY_INPUT_MANAGER
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var a = actionsOut.ContinuousActions;
            a[0] = (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D) ? 1f : 0f)
                   - (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A) ? 1f : 0f);
            a[1] = (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W) ? 1f : 0f)
                   - (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S) ? 1f : 0f);
            a[2] = Input.GetKey(KeyCode.Space) ? 1f : 0f;
        }
#endif
    }
}
