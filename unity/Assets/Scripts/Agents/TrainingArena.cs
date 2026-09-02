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
            Track = TrackGenerator.Generate(seed);
            TrackEdgeColliders.Build(Track, transform);
            Car = BuildAgentCar(Track);
        }

        private CarController BuildAgentCar(TrackData track)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "AgentCar";
            body.transform.SetParent(transform, false);
            body.transform.localScale = new Vector3(2.0f, 0.8f, 4.2f);
            // Keep the BoxCollider (the car bounces off the edge walls), but put
            // the car on "Ignore Raycast" so the ray sensor, whose origin sits
            // inside this box, doesn't hit the car's own collider. Physical
            // collision with the walls still works — that's the collision matrix,
            // not raycasts.
            body.layer = 2; // Ignore Raycast

            var rb = body.AddComponent<Rigidbody>();
            var car = body.AddComponent<CarController>();
            car.ReadKeyboard = false;

            Vector3 spawn = track.StartPosition + Vector3.up * 0.4f + track.StartDirection * 2f;
            car.PlaceAt(spawn, track.StartDirection);

            AddBehaviour(body);
            var agent = body.AddComponent<RaceAgent>();   // last: its Initialize() reads the arena + brain
            agent.MaxStep = 4000;                          // ~80 s of sim = episode timeout
            return car;
        }

        private static void AddBehaviour(GameObject go)
        {
            var bp = go.AddComponent<BehaviorParameters>();
            bp.BehaviorName = "RaceAgent";
            bp.BrainParameters.VectorObservationSize = 12;
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(3);

            var dr = go.AddComponent<DecisionRequester>();
            dr.DecisionPeriod = 5;
            dr.TakeActionsBetweenDecisions = true;

            var ray = go.AddComponent<RayPerceptionSensorComponent3D>();
            ray.SensorName = "TrackRays";
            ray.DetectableTags = new List<string> { TrackEdgeColliders.EdgeTag };
            ray.RaysPerDirection = 4;          // 9 rays
            ray.MaxRayDegrees = 75f;
            ray.RayLength = 40f;
            ray.SphereCastRadius = 0.4f;
            ray.StartVerticalOffset = 0.3f;
            ray.EndVerticalOffset = 0.3f;
        }
    }
}
