using UnityEngine;
using UnityEngine.InputSystem;

namespace AgenticRacing.Vehicle
{
    /// <summary>
    /// Arcade car physics on a plain <see cref="Rigidbody"/> with manual forces
    /// and torque — no WheelCollider (CLAUDE.md §3). Everything runs in
    /// <see cref="FixedUpdate"/>. Keyboard control (arrows or WASD) is polled
    /// directly from the Input System device so no action asset is needed.
    ///
    /// Inputs are exposed as <see cref="Throttle"/> / <see cref="Brake"/> /
    /// <see cref="Steer"/> so that Fase 2's RL agent and Fase 4's strategist can
    /// drive the same body by writing these instead of reading the keyboard.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class CarController : MonoBehaviour
    {
        [SerializeField] private VehicleConfig config;
        [SerializeField] private bool readKeyboard = true;

        private Rigidbody _rb;

        /// <summary>
        /// When true the keyboard drives the car. The RL agent (Fase 2) and the
        /// strategist (Fase 4) turn this off and write <see cref="Throttle"/> /
        /// <see cref="Brake"/> / <see cref="Steer"/> directly.
        /// </summary>
        public bool ReadKeyboard { get => readKeyboard; set => readKeyboard = value; }

        /// <summary>-1..1 forward request (keyboard or external controller).</summary>
        public float Throttle { get; set; }
        /// <summary>0..1 brake request.</summary>
        public float Brake { get; set; }
        /// <summary>-1..1 steering request (negative = left).</summary>
        public float Steer { get; set; }

        /// <summary>Signed forward speed in m/s (negative = reversing).</summary>
        public float ForwardSpeed => _rb != null ? Vector3.Dot(_rb.linearVelocity, transform.forward) : 0f;

        /// <summary>The parameters in force. Never null after Awake.</summary>
        public VehicleConfig Config => config;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (config == null) config = VehicleConfig.CreateDefault();

#if UNITY_WEBGL && !UNITY_EDITOR
            // Route browser keyboard to the canvas even without an explicit click.
            WebGLInput.captureAllKeyboardInput = true;
#endif

            _rb.mass = config.Mass;
            _rb.linearDamping = config.LinearDrag;
            _rb.angularDamping = config.AngularDrag;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.useGravity = false;
            // Fase 1 track is a flat ribbon at Y = 0 with no ground plane, so pin
            // the car to that plane (no falling through the mesh collider) and
            // keep it upright. Fase 3 can relax this if elevation is added.
            _rb.constraints = RigidbodyConstraints.FreezeRotationX
                              | RigidbodyConstraints.FreezeRotationZ
                              | RigidbodyConstraints.FreezePositionY;
        }

        private void FixedUpdate()
        {
            if (readKeyboard) PollKeyboard();

            float dt = Time.fixedDeltaTime;
            Vector3 fwd = transform.forward;
            float vFwd = Vector3.Dot(_rb.linearVelocity, fwd);

            ApplyDrive(fwd, vFwd);
            ApplySteering(vFwd, dt);
            ApplyLateralGrip(fwd);
        }

        private void PollKeyboard()
        {
            float t = 0f, s = 0f;
            bool brakeKey;

#if ENABLE_LEGACY_INPUT_MANAGER
            // The legacy Input Manager's keyboard is reliable in WebGL, unlike the
            // Input System's keyboard which in Unity 6 WebGL builds often never
            // receives events (project uses activeInputHandler=Both for this).
            if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) t += 1f;
            if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) t -= 1f;
            if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) s -= 1f;
            if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) s += 1f;
            brakeKey = Input.GetKey(KeyCode.Space);
#else
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.upArrowKey.isPressed || kb.wKey.isPressed) t += 1f;
                if (kb.downArrowKey.isPressed || kb.sKey.isPressed) t -= 1f;
                if (kb.leftArrowKey.isPressed || kb.aKey.isPressed) s -= 1f;
                if (kb.rightArrowKey.isPressed || kb.dKey.isPressed) s += 1f;
            }
            brakeKey = kb != null && kb.spaceKey.isPressed;
#endif

            Steer = s;
            // Down/S brakes while moving forward, otherwise it reverses.
            if (t < 0f && ForwardSpeed > 0.5f) { Brake = 1f; Throttle = 0f; }
            else { Brake = brakeKey ? 1f : 0f; Throttle = t; }
        }

        private void ApplyDrive(Vector3 fwd, float vFwd)
        {
            if (Brake > 0.01f && Mathf.Abs(vFwd) > 0.1f)
            {
                _rb.AddForce(-Mathf.Sign(vFwd) * fwd * (config.BrakeForce * Mathf.Clamp01(Brake)));
                return;
            }

            if (Mathf.Abs(Throttle) > 0.01f)
            {
                bool overLimit = (Throttle > 0f && vFwd > config.MaxSpeed) ||
                                 (Throttle < 0f && -vFwd > config.MaxReverseSpeed);
                if (!overLimit)
                    _rb.AddForce(fwd * (config.EngineForce * Throttle));
            }
            else if (Mathf.Abs(vFwd) > 0.1f)
            {
                _rb.AddForce(-Mathf.Sign(vFwd) * fwd * config.CoastForce);
            }
        }

        private void ApplySteering(float vFwd, float dt)
        {
            if (Mathf.Abs(Steer) < 0.01f) return;

            float speed = Mathf.Abs(vFwd);
            // No steering authority when nearly stopped; full at low speed;
            // tapering to HighSpeedTurnFactor by MaxSpeed.
            float speedT = Mathf.Clamp01(speed / config.SteerFadeInSpeed);
            float highT = Mathf.Clamp01(speed / config.MaxSpeed);
            float authority = speedT * Mathf.Lerp(1f, config.HighSpeedTurnFactor, highT);

            float yawDeg = Steer * config.TurnRateDegPerSec * authority * dt;
            // Reversing inverts the steering sense, like a real car.
            if (vFwd < -0.1f) yawDeg = -yawDeg;

            Quaternion delta = Quaternion.Euler(0f, yawDeg, 0f);
            _rb.MoveRotation(_rb.rotation * delta);
        }

        private void ApplyLateralGrip(Vector3 fwd)
        {
            Vector3 right = transform.right;
            float vRight = Vector3.Dot(_rb.linearVelocity, right);
            // Cancel most of the sideways velocity each step; what leaks through
            // is the slide/drift.
            _rb.AddForce(-right * (vRight * config.LateralGrip), ForceMode.Acceleration);
        }

        /// <summary>Places the car at a pose and clears its motion (grid reset).</summary>
        public void PlaceAt(Vector3 position, Vector3 forward)
        {
            _rb.position = position;
            _rb.rotation = Quaternion.LookRotation(new Vector3(forward.x, 0f, forward.z).normalized, Vector3.up);
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(_rb.position, _rb.rotation);
        }
    }
}
