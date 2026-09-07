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
        private string _demoDir;

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
            _heuristic = _record || System.Array.IndexOf(args, "-heuristic") >= 0;
            if (_record && evalSeconds < 300f) evalSeconds = 300f;

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

            _demoDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "demos"));
            if (_record) Directory.CreateDirectory(_demoDir);

            var arenas = FindObjectsByType<TrainingArena>(FindObjectsSortMode.None);
            _trackLen = arenas.Length > 0 && arenas[0].Track != null ? arenas[0].Track.Length : 0f;

            int n = 0;
            foreach (var agent in FindObjectsByType<RaceAgent>(FindObjectsSortMode.None))
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
                var car = agent.GetComponent<CarController>();
                if (car != null) _cars.Add(car);
                n++;
            }

            RaceAgent.AnyEpisodeEnded += OnEpisodeEnded;
            _startTime = Time.time;
            string mode = _record ? $"RECORD -> {_demoDir}" : _heuristic ? "policy=HEURISTIC" : "model=Resources/" + modelResource;
            Debug.Log($"[Eval] {mode} agents={n} cars={_cars.Count} trackLen={_trackLen:F0}m window={evalSeconds:F0}s");
            if (n == 0)
            {
                Debug.LogError("[Eval] no RaceAgent found in the scene");
                Application.Quit(1);
            }
        }

        private void OnDestroy() => RaceAgent.AnyEpisodeEnded -= OnEpisodeEnded;

        private void OnEpisodeEnded(string reason, int steps, float lapArc)
        {
            if (_reported) return;
            _endCounts.TryGetValue(reason, out int c);
            _endCounts[reason] = c + 1;
            _epCount++;
            _epStepsSum += steps;
            _lapArcSum += lapArc;
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

            Application.Quit(0);
        }
    }
}
