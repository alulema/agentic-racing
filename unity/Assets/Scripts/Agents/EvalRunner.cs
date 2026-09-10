using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgenticRacing.Vehicle;
using Unity.InferenceEngine;
using Unity.MLAgents.Demonstrations;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace AgenticRacing.Agents
{
    /// <summary>
    /// Offline evaluation harness. Runs the same arena grid as training but with
    /// every <see cref="RaceAgent"/> in <see cref="BehaviorType.InferenceOnly"/>
    /// driving from a baked <c>.onnx</c> (<c>Resources/Eval/RaceAgent</c>), for a
    /// fixed wall-clock window, then logs an aggregate report and quits. Built by
    /// <c>Fase2EvalBuild</c>. This is how we see what a trained policy actually
    /// does without opening the Editor.
    ///
    /// Command-line flags (see <see cref="Start"/>):
    ///   -heuristic          drive RaceAgent.Heuristic instead of a model
    ///   -record             imply -heuristic and dump .demo files
    ///   -directive &lt;kind&gt;   force one strategist stance on every agent
    ///   -aggression &lt;lvl&gt;   lo|mid|hi for the forced directive
    ///   -population         Fase 3 baseline: assign RaceDirective.Population
    ///                       members round-robin across arenas and report mean
    ///                       lap time per member (implies -heuristic)
    ///   -seconds &lt;n&gt;        override the eval window
    /// </summary>
    public sealed class EvalRunner : MonoBehaviour
    {
        [SerializeField] private float evalSeconds = 120f;
        [SerializeField] private string modelResource = "Eval/RaceAgent";

        private readonly List<CarController> _cars = new();
        private readonly Dictionary<string, int> _endCounts = new();
        private int _epCount;
        private long _epStepsSum;
        private double _lapArcSum, _speedSum, _throttleSum, _absSteerSum, _brakeSum;
        private long _samples;
        private float _trackLen;
        private float _startTime;
        private bool _reported;
        private bool _heuristic;
        private bool _record;
        private bool _population;
        private string _demoDir;

        // Fase 3 -population: which population member each agent drives, and the
        // per-member lap tally (lap count + lap time in FixedUpdate steps).
        private readonly Dictionary<RaceAgent, int> _memberOf = new();
        private int[] _memberArenas;
        private int[] _memberLaps;
        private long[] _memberLapStepsSum;
        private int[] _memberLapStepsMin;
        private int[] _memberLapStepsMax;

        private void Start()
        {
            // `eval.exe -heuristic` runs RaceAgent.Heuristic (the scripted
            // racing-line follower) instead of a trained model — the "can this
            // track be driven at all?" reference.
            // `eval.exe -record` implies -heuristic and attaches a
            // DemonstrationRecorder to every agent, writing .demo files next to
            // the exe for imitation learning (BC/GAIL).
            var args = System.Environment.GetCommandLineArgs();
            _record = System.Array.IndexOf(args, "-record") >= 0;
            _population = System.Array.IndexOf(args, "-population") >= 0;
            _heuristic = _record || _population || System.Array.IndexOf(args, "-heuristic") >= 0;
            if (_record && evalSeconds < 300f) evalSeconds = 300f;
            // A population run needs each member to complete several laps
            // (~80-115 s each) for the mean to mean anything.
            if (_population && evalSeconds < 360f) evalSeconds = 360f;

            string secondsArg = ArgValue(args, "-seconds");
            if (secondsArg != null && float.TryParse(secondsArg, out float sec) && sec > 0f)
                evalSeconds = sec;

            // `-directive <attack|defend|conserve|push>` and `-aggression <lo|mid|hi>`
            // force every agent to one strategist stance, to inspect its effect.
            // -population assigns a different stance per arena instead, so the two
            // are mutually exclusive; -population wins.
            if (!_population)
                RaceAgent.ForcedDirective = ParseForcedDirective(args);

            ModelAsset model = null;
            if (!_heuristic)
            {
                model = Resources.Load<ModelAsset>(modelResource);
                if (model == null)
                {
                    Debug.LogError($"[Eval] no ModelAsset at Resources/{modelResource}");
                    Application.Quit(1);
                    return;
                }
            }

            // Clean, aligned, at-speed spawns for the heuristic — the noisy
            // training spawn stalls it before it can converge to the line.
            RaceAgent.CleanSpawn = _heuristic;

            _demoDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "demos"));
            if (_record) Directory.CreateDirectory(_demoDir);

            var arenas = FindObjectsByType<TrainingArena>(FindObjectsSortMode.None);
            _trackLen = arenas.Length > 0 && arenas[0].Track != null ? arenas[0].Track.Length : 0f;

            if (_population)
            {
                int pn = RaceDirective.Population.Length;
                _memberArenas = new int[pn];
                _memberLaps = new int[pn];
                _memberLapStepsSum = new long[pn];
                _memberLapStepsMin = new int[pn];
                _memberLapStepsMax = new int[pn];
                for (int k = 0; k < pn; k++) _memberLapStepsMin[k] = int.MaxValue;
            }

            // Stable agent order so -population spreads members evenly and the
            // same way each run (FindObjectsByType order is not guaranteed).
            var agentList = FindObjectsByType<RaceAgent>(FindObjectsSortMode.None)
                .OrderBy(ag => ag.GetInstanceID())
                .ToList();

            int n = 0;
            foreach (var agent in agentList)
            {
                if (_heuristic)
                {
                    agent.GetComponent<BehaviorParameters>().BehaviorType = BehaviorType.HeuristicOnly;
                    if (_record)
                    {
                        var rec = agent.gameObject.AddComponent<DemonstrationRecorder>();
                        rec.DemonstrationName = "RaceHeuristic";
                        rec.DemonstrationDirectory = _demoDir;
                        rec.Record = true;
                    }
                }
                else
                {
                    // Set the model BEFORE switching Behavior Type: flipping to
                    // InferenceOnly while Model is still null throws
                    // "Can't use Behavior Type InferenceOnly without a model".
                    // ML-Agents' InferenceDevice: Burst == CPU inference.
                    agent.SetModel("RaceAgent", model, InferenceDevice.Burst);
                    agent.GetComponent<BehaviorParameters>().BehaviorType = BehaviorType.InferenceOnly;
                }

                if (_population)
                {
                    int mi = n % RaceDirective.Population.Length;
                    agent.InstanceDirective = RaceDirective.Population[mi].Directive;
                    _memberOf[agent] = mi;
                    _memberArenas[mi]++;
                    // The first episode already began (in Awake) with a random
                    // directive; restart it so every counted lap runs the member.
                    agent.EndEpisode();
                }

                var car = agent.GetComponent<CarController>();
                if (car != null) _cars.Add(car);
                n++;
            }

            RaceAgent.AnyEpisodeEnded += OnEpisodeEnded;
            _startTime = Time.time;
            string mode = _record ? $"RECORD -> {_demoDir}"
                : _population ? $"policy=HEURISTIC population={RaceDirective.Population.Length} members"
                : _heuristic ? "policy=HEURISTIC"
                : "model=Resources/" + modelResource;
            Debug.Log($"[Eval] {mode} agents={n} cars={_cars.Count} trackLen={_trackLen:F0}m window={evalSeconds:F0}s");
            if (_population)
            {
                for (int k = 0; k < RaceDirective.Population.Length; k++)
                {
                    var pm = RaceDirective.Population[k];
                    Debug.Log($"[Eval]   {pm.Name}: Kind={pm.Directive.Kind} agg={pm.Directive.Aggression:F2} " +
                              $"risk={pm.Directive.RiskTolerance:F2} arenas={_memberArenas[k]}");
                }
            }
            if (n == 0)
            {
                Debug.LogError("[Eval] no RaceAgent found in the scene");
                Application.Quit(1);
            }
        }

        private void OnDestroy() => RaceAgent.AnyEpisodeEnded -= OnEpisodeEnded;

        private static RaceDirective? ParseForcedDirective(string[] args)
        {
            string kindArg = ArgValue(args, "-directive");
            string aggArg = ArgValue(args, "-aggression");
            if (kindArg == null && aggArg == null) return null;

            var d = RaceDirective.Neutral;
            if (kindArg != null && System.Enum.TryParse(kindArg, true, out DirectiveKind k))
                d.Kind = k;
            d.Aggression = aggArg switch { "lo" => 0.15f, "hi" => 0.85f, _ => 0.5f };
            d.RiskTolerance = d.Aggression;
            Debug.Log($"[Eval] forced directive: Kind={d.Kind} Aggression={d.Aggression:F2}");
            return d;
        }

        private static string ArgValue(string[] args, string flag)
        {
            int i = System.Array.IndexOf(args, flag);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }

        private void OnEpisodeEnded(RaceAgent agent, string reason, int steps, float lapArc)
        {
            if (_reported) return;
            _endCounts.TryGetValue(reason, out int c);
            _endCounts[reason] = c + 1;
            _epCount++;
            _epStepsSum += steps;
            _lapArcSum += lapArc;

            if (_population && reason == "lap" && _memberOf.TryGetValue(agent, out int mi))
            {
                _memberLaps[mi]++;
                _memberLapStepsSum[mi] += steps;
                if (steps < _memberLapStepsMin[mi]) _memberLapStepsMin[mi] = steps;
                if (steps > _memberLapStepsMax[mi]) _memberLapStepsMax[mi] = steps;
            }
        }

        private void FixedUpdate()
        {
            if (_reported) return;

            foreach (var car in _cars)
            {
                if (car == null) continue;
                _speedSum += car.ForwardSpeed;
                _throttleSum += car.Throttle;
                _absSteerSum += Mathf.Abs(car.Steer);
                _brakeSum += car.Brake;
                _samples++;
            }

            if (Time.time - _startTime >= evalSeconds)
                Report();
        }

        private void Report()
        {
            _reported = true;

            if (_record)
            {
                // Flush every recorder so the .demo files close cleanly.
                foreach (var rec in FindObjectsByType<DemonstrationRecorder>(FindObjectsSortMode.None))
                    rec.Close();
                var files = Directory.Exists(_demoDir) ? Directory.GetFiles(_demoDir, "*.demo") : new string[0];
                Debug.Log($"[Eval] RECORD done: {files.Length} .demo files in {_demoDir}");
            }

            long s = System.Math.Max(1, _samples);
            int e = System.Math.Max(1, _epCount);
            float meanSteps = (float)_epStepsSum / e;
            float meanLapFrac = _trackLen > 0 ? (float)(_lapArcSum / e) / _trackLen : 0f;
            string split = string.Join(", ", _endCounts.OrderByDescending(k => k.Value)
                .Select(k => $"{k.Key}={100f * k.Value / e:F0}%"));

            Debug.Log(
                $"[Eval] REPORT {(_heuristic ? "policy=HEURISTIC" : "model=Resources/" + modelResource)}\n" +
                $"  episodes={_epCount}  meanEpisodeSteps={meanSteps:F0} (~{meanSteps * Time.fixedDeltaTime:F1}s)  " +
                $"meanLapProgress={meanLapFrac * 100f:F0}% of a lap\n" +
                $"  end reasons: {split}\n" +
                $"  meanForwardSpeed={_speedSum / s:F1} m/s  meanThrottle={_throttleSum / s:F2}  " +
                $"meanBrake={_brakeSum / s:F2}  meanAbsSteer={_absSteerSum / s:F2}");

            if (_population)
                ReportPopulation();

            Application.Quit(0);
        }

        /// <summary>
        /// The Fase 3 baseline table (CLAUDE.md §5): mean lap time per population
        /// member, head to head on the same fixed oval. If one member is
        /// consistently seconds faster than the rest, the population is not
        /// pace-matched and its preset needs pulling back toward the pack before
        /// it can seed the Fase 6.3 comparison.
        /// </summary>
        private void ReportPopulation()
        {
            float dt = Time.fixedDeltaTime;
            var rows = new List<string> { "[Eval] POPULATION baseline (mean lap time, head to head)" };
            var means = new List<float>();

            for (int k = 0; k < RaceDirective.Population.Length; k++)
            {
                var pm = RaceDirective.Population[k];
                int laps = _memberLaps[k];
                if (laps == 0)
                {
                    rows.Add($"  {pm.Name,-12} arenas={_memberArenas[k]} laps=0  (no completed lap in the window)");
                    continue;
                }
                float mean = (float)_memberLapStepsSum[k] / laps * dt;
                float min = _memberLapStepsMin[k] * dt;
                float max = _memberLapStepsMax[k] * dt;
                means.Add(mean);
                rows.Add($"  {pm.Name,-12} arenas={_memberArenas[k]} laps={laps,2}  " +
                         $"mean={mean,6:F1}s  min={min,6:F1}s  max={max,6:F1}s  " +
                         $"[{pm.Directive.Kind} agg={pm.Directive.Aggression:F2} risk={pm.Directive.RiskTolerance:F2}]");
            }

            if (means.Count >= 2)
            {
                float lo = means.Min();
                float hi = means.Max();
                rows.Add($"  spread: fastest {lo:F1}s .. slowest {hi:F1}s  " +
                         $"(+{(hi - lo):F1}s, {100f * (hi - lo) / lo:F0}% of the fastest) " +
                         $"-- pace-matched if this is small");
            }

            Debug.Log(string.Join("\n", rows));
        }
    }
}
