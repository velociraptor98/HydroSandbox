using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// An object to drop into the tank. Its density decides which layer it settles in: the fluid
    /// coupling floats it on any liquid denser than itself. Held, it hangs above the tank under
    /// the cursor until a click lets go. It can be picked up again from wherever it ends up, and
    /// goes back to its tray if it falls off the bench.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class DropObject : BenchItem
    {
        [Tooltip("kg/m^3; with the volume, sets the Rigidbody's mass.")]
        [SerializeField, Min(1f)] float density = 1000f;
        [Tooltip("Collider volume in m^3.")]
        [SerializeField, Min(1e-7f)] float volume = 1e-4f;
        [Tooltip("Half the object's largest extent, to keep it clear of the glass while held.")]
        [SerializeField, Min(0f)] float radius = 0.03f;
        [Tooltip("Below this height it has fallen off the bench and returns to its tray.")]
        [SerializeField] float lostBelowY = -0.2f;

        Rigidbody body;

        public float Density => density;

        public void Configure(float objectDensity, float objectVolume, float objectRadius, float lostHeight)
        {
            density = objectDensity;
            volume = objectVolume;
            radius = objectRadius;
            lostBelowY = lostHeight;
        }

        protected override void Awake()
        {
            base.Awake();
            body = GetComponent<Rigidbody>();
            body.mass = density * volume;
            body.isKinematic = true;
        }

        void OnEnable() => FluidRigidbodyCoupling.PhysicsStepped += CheckLost;

        void OnDisable() => FluidRigidbodyCoupling.PhysicsStepped -= CheckLost;

        void FixedUpdate()
        {
            if (Physics.simulationMode != SimulationMode.Script)
                CheckLost(Time.fixedDeltaTime);
        }

        void CheckLost(float dt)
        {
            if (!body.isKinematic && body.position.y < lostBelowY)
                PutBack();
        }

        internal override void PickUp(in HandInput input)
        {
            body.isKinematic = true;
            Hold(input);
        }

        internal override bool Hold(in HandInput input)
        {
            Vector3 target = input.planePoint;
            float floor = HomePosition.y;
            if (input.tank != null)
            {
                Bounds outer = input.tank.Interior;
                outer.Expand(new Vector3(2f * (input.tank.WallThickness + radius), 0f, 2f * (input.tank.WallThickness + radius)));
                if (target.x > outer.min.x && target.x < outer.max.x && target.z > outer.min.z && target.z < outer.max.z)
                    floor = outer.max.y + radius + 0.03f;
            }
            target.y = Mathf.Max(target.y, floor);
            body.position = target;
            transform.position = target;

            if (!input.useClicked)
                return true;
            body.isKinematic = false;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            return false;
        }

        internal override void PutBack()
        {
            if (!body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            body.isKinematic = true;
            body.position = HomePosition;
            body.rotation = HomeRotation;
            base.PutBack();
        }
    }
}
