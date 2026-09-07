using System.Collections.Generic;
using System.Linq;
using AgenticRacing.Vehicle;
using Unity.InferenceEngine;
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

        private void Start()
        {
            var model = Resources.Load<ModelAsset>(modelResource);
            if (model == null)
            {
                Debug.LogError($"[Eval] no ModelAsset at Resources/{modelResource}");
                Application.Quit(1);
                return;
            }

            var arenas = FindObjectsByType<TrainingArena>(FindObjectsSortMode.None);
            _trackLen = arenas.Length > 0 && arenas[0].Track != null ? arenas[0].Track.Length : 0f;

            int n = 0;
            foreach (var agent in FindObjectsByType<RaceAgent>(FindObjectsSortMode.None))
            {
                var bp = agent.GetComponent<BehaviorParameters>();
                bp.BehaviorType = BehaviorType.InferenceOnly;
                agent.SetModel("RaceAgent", model, InferenceDevice.CPU);
                var car = agent.GetComponent<CarController>();
                if (car != null) _cars.Add(car);
                n++;
            }

            RaceAgent.AnyEpisodeEnded += OnEpisodeEnded;
            _startTime = Time.time;
            Debug.Log($"[Eval] model=Resources/{modelResource} agents={n} trackLen={_trackLen:F0}m window={evalSeconds:F0}s");
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

            long s = System.Math.Max(1, _samples);
            int e = System.Math.Max(1, _epCount);
            float meanSteps = (float)_epStepsSum / e;
            float meanLapFrac = _trackLen > 0 ? (float)(_lapArcSum / e) / _trackLen : 0f;
            string split = string.Join(", ", _endCounts.OrderByDescending(k => k.Value)
                .Select(k => $"{k.Key}={100f * k.Value / e:F0}%"));

            Debug.Log(
                $"[Eval] REPORT model=Resources/{modelResource}\n" +
                $"  episodes={_epCount}  meanEpisodeSteps={meanSteps:F0} (~{meanSteps * Time.fixedDeltaTime:F1}s)  " +
                $"meanLapProgress={meanLapFrac * 100f:F0}% of a lap\n" +
                $"  end reasons: {split}\n" +
                $"  meanForwardSpeed={_speedSum / s:F1} m/s  meanThrottle={_throttleSum / s:F2}  " +
                $"meanBrake={_brakeSum / s:F2}  meanAbsSteer={_absSteerSum / s:F2}");

            Application.Quit(0);
        }
    }
}
