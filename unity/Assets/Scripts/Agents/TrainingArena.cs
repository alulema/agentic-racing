using System.Collections.Generic;
using AgenticRacing.Track;
using AgenticRacing.Vehicle;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace AgenticRacing.Agents
{
    /// <summary>
    /// One self-contained training arena: a procedurally generated circuit (with
    /// its own seed), the invisible edge walls, and one car carrying a
    /// <see cref="RaceAgent"/>. A training scene places several of these far
    /// apart so many agents feed one policy in parallel (CLAUDE.md §5: "múltiples
    /// arenas en paralelo, cada una con una seed de circuito distinta").
    ///
    /// Everything is created in <see cref="Awake"/>: no scene wiring, and the
    /// headless Linux build (Fase 2 training) is just this component in an empty
    /// scene, or a grid of them via <see cref="TrainingSceneBootstrap"/>.
    /// </summary>
    public sealed class TrainingArena : MonoBehaviour
    {
        [SerializeField] private int seed = 1;

        public TrackData Track { get; private set; }
        public CarController Car { get; private set; }

        public void SetSeed(int value) => seed = value;

        private void Awake()
        {
            // See TrainingSceneBootstrap: keep stepping while the player window is
            // unfocused, or mlagents-learn times the environment out. Harmless to
            // set from every arena.
            Application.runInBackground = true;

            Track = TrackGenerator.Generate(seed);
            TrackEdgeColliders.Build(Track, transform);
            Car = BuildAgentCar(Track);
        }

        private CarController BuildAgentCar(TrackData track)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "AgentCar";

            // Assemble the whole agent while the GameObject is INACTIVE, then
            // switch it on once. ML-Agents' Agent.OnEnable runs InitializeSensors()
            // the instant the component lands on an *active* object; adding
            // Rigidbody / CarController / BehaviorParameters / DecisionRequester /
            // RayPerceptionSensorComponent3D / RaceAgent one at a time on a live
            // object makes that fire against a half-built component set. The
            // negotiated observation spec then ends up out of step with the
            // runtime stream (spec sees the 12-float vector sensor only; runtime
            // also sends the 27-float ray sensor), which crashes mlagents-learn on
            // the first step with "Expected shape (12,) but got (27,)" and floods
            // Player-0.log with "Fewer observations (0) ... size (12)". Building
            // cold and activating once makes InitializeSensors() run exactly once
            // over the final component set.
            body.SetActive(false);
            body.transform.SetParent(transform, false);
            body.transform.localScale = new Vector3(2.0f, 0.8f, 4.2f);
            // Keep the BoxCollider (the car bounces off the edge walls), but put
            // the car on "Ignore Raycast" so the ray sensor, whose origin sits
            // inside this box, doesn't hit the car's own collider. Physical
            // collision with the walls still works — that's the collision matrix,
            // not raycasts.
            body.layer = 2; // Ignore Raycast

            body.AddComponent<Rigidbody>();
            var car = body.AddComponent<CarController>();
            car.ReadKeyboard = false;

            // Order matters. BehaviorParameters + sensors first, then the Agent,
            // then DecisionRequester LAST: DecisionRequester has
            // [RequireComponent(typeof(Agent))] and [DefaultExecutionOrder(-10)];
            // adding it before a concrete Agent makes Unity try to satisfy the
            // requirement with the abstract Agent type, and its -10 Awake then
            // races the Agent's own init. Added last, the requirement is already
            // met and the request pipeline wires up cleanly.
            AddBehaviour(body);
            var agent = body.AddComponent<RaceAgent>();   // its Initialize() reads the arena + brain
            agent.MaxStep = 6000;                          // ~120 s: a full lap of the fixed circuit is ~95 s at pace
            AddDecisionRequester(body);

            body.SetActive(true);                          // single, clean InitializeSensors()

            // PlaceAt needs CarController.Awake to have cached the Rigidbody, so it
            // runs after activation. The first OnEpisodeBegin re-spawns the car at
            // a random point on the centerline anyway; this is just a placeholder.
            Vector3 spawn = track.StartPosition + Vector3.up * 0.4f + track.StartDirection * 2f;
            car.PlaceAt(spawn, track.StartDirection);
            return car;
        }

        private static void AddBehaviour(GameObject go)
        {
            var bp = go.AddComponent<BehaviorParameters>();
            bp.BehaviorName = "RaceAgent";
            bp.BrainParameters.VectorObservationSize = RaceAgent.ObsSize;
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(3);

            var ray = go.AddComponent<RayPerceptionSensorComponent3D>();
            ray.SensorName = "TrackRays";
            ray.DetectableTags = new List<string> { TrackEdgeColliders.EdgeTag };
            ray.RaysPerDirection = 4;          // 9 rays
            ray.MaxRayDegrees = 75f;
            ray.RayLength = 70f;              // was 40 — needs to see the corner before the car is in it
            ray.SphereCastRadius = 0.4f;
            ray.StartVerticalOffset = 0.3f;
            ray.EndVerticalOffset = 0.3f;
        }

        private static void AddDecisionRequester(GameObject go)
        {
            var dr = go.AddComponent<DecisionRequester>();
            dr.DecisionPeriod = 5;
            dr.TakeActionsBetweenDecisions = true;
        }
    }
}
