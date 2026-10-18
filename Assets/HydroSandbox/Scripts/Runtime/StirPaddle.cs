using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// A stirring paddle. Held, it hangs over the tank under the cursor; holding the use button
    /// dips it in, down to where the cursor points, and moving the cursor stirs. It is a
    /// Rigidbody driven by velocity, so the water sees how fast it moves and it shoves floating
    /// objects aside, but the water does not push it back (a hand holds it).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class StirPaddle : BenchItem
    {
        [Tooltip("Distance from the pivot down to the tip of the blade, in meters.")]
        [SerializeField, Min(0f)] float tipDepth = 0.31f;
        [SerializeField, Min(0f)] float bladeHalfWidth = 0.04f;
        [Tooltip("Faster than this and the paddle cuts through the water instead of pushing it.")]
        [SerializeField, Min(0.1f)] float maxSpeed = 1.6f;
        [Tooltip("Height of the tip above the rim while lifted out.")]
        [SerializeField, Min(0f)] float liftClearance = 0.04f;

        Rigidbody body;

        protected override void Awake()
        {
            base.Awake();
            body = GetComponent<Rigidbody>();
            Park();
        }

        void Park()
        {
            body.isKinematic = true;
            body.useGravity = false;
        }

        internal override void PickUp(in HandInput input)
        {
            Vector3 target = TargetPosition(input);
            body.isKinematic = true;
            body.position = target;
            body.rotation = TargetRotation(input);
            transform.SetPositionAndRotation(target, body.rotation);
            body.isKinematic = false;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.linearVelocity = Vector3.zero;
        }

        internal override bool Hold(in HandInput input)
        {
            Vector3 velocity = (TargetPosition(input) - body.position) / Mathf.Max(input.physicsStep, 1e-4f);
            body.linearVelocity = Vector3.ClampMagnitude(velocity, maxSpeed);
            body.angularVelocity = Vector3.zero;
            body.MoveRotation(TargetRotation(input));
            return true;
        }

        internal override void PutBack()
        {
            body.linearVelocity = Vector3.zero;
            body.constraints = RigidbodyConstraints.None;
            Park();
            body.position = HomePosition;
            body.rotation = HomeRotation;
            base.PutBack();
        }

        Vector3 TargetPosition(in HandInput input)
        {
            Vector3 tip = input.planePoint;
            if (input.tank != null)
            {
                Bounds interior = input.tank.Interior;
                float margin = bladeHalfWidth + 0.01f;
                tip.x = Mathf.Clamp(tip.x, interior.min.x + margin, interior.max.x - margin);
                tip.z = Mathf.Clamp(tip.z, interior.min.z + margin, interior.max.z - margin);
                float lifted = interior.max.y + liftClearance;
                tip.y = input.useHeld ? Mathf.Clamp(tip.y, interior.min.y + 0.015f, lifted) : lifted;
            }
            return tip + Vector3.up * tipDepth;
        }

        // The blade faces across the view, so sideways strokes on screen push the most water.
        static Quaternion TargetRotation(in HandInput input)
        {
            Vector3 forward = Vector3.ProjectOnPlane(input.ray.direction, Vector3.up);
            return forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, Vector3.up) : Quaternion.identity;
        }
    }
}
