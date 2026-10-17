using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Returns a floating Rigidbody to a respawn point once it leaves a region (washed out of the
    /// river reach, or fallen off the terrain), so debris keeps flowing through the scene.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class DebrisRecycler : MonoBehaviour
    {
        [Tooltip("World-space region the debris may occupy; leaving it triggers a respawn.")]
        [SerializeField] Bounds region = new Bounds(Vector3.zero, Vector3.one * 20f);
        [SerializeField] Transform respawnPoint;
        [Tooltip("Random offset applied around the respawn point, in the respawn point's local axes, in meters.")]
        [SerializeField] Vector3 respawnJitter = new Vector3(1f, 0.05f, 0.25f);
        [Tooltip("Clearance radius kept from other bodies when choosing a respawn spot.")]
        [SerializeField, Min(0f)] float respawnClearance = 0.3f;
        [Tooltip("Respawn when the body has barely moved for this long, in simulated seconds (stuck on a rock or bank).")]
        [SerializeField, Min(0f)] float stuckSeconds = 12f;

        Rigidbody body;
        float stuckTimer;

        void Awake() => body = GetComponent<Rigidbody>();

        // Physics may be stepped from script (FluidRigidbodyCoupling), where FixedUpdate does not
        // run; its step event keeps the stuck timer in simulated time either way.
        void OnEnable() => FluidRigidbodyCoupling.PhysicsStepped += Check;

        void OnDisable() => FluidRigidbodyCoupling.PhysicsStepped -= Check;

        void FixedUpdate()
        {
            if (Physics.simulationMode != SimulationMode.Script)
                Check(Time.fixedDeltaTime);
        }

        void Check(float dt)
        {
            stuckTimer = body.linearVelocity.sqrMagnitude < 0.01f ? stuckTimer + dt : 0f;
            if (region.Contains(body.position) && (stuckSeconds <= 0f || stuckTimer < stuckSeconds))
                return;
            Respawn();
        }

        public void Respawn()
        {
            if (respawnPoint == null)
                return;
            // Try a few spots and take the first clear of other bodies, so recycled debris does not
            // land on top of debris recycled earlier.
            Vector3 position = respawnPoint.position;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector3 jitter = Vector3.Scale(respawnJitter, new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f)));
                Vector3 candidate = respawnPoint.position + respawnPoint.rotation * jitter;
                position = candidate;
                if (IsClear(candidate))
                    break;
            }
            body.position = position;
            body.rotation = respawnPoint.rotation * Quaternion.Euler(0f, Random.Range(-25f, 25f), 0f);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            stuckTimer = 0f;
        }

        bool IsClear(Vector3 point)
        {
            foreach (Collider c in Physics.OverlapSphere(point, respawnClearance, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c.attachedRigidbody != null && c.attachedRigidbody != body)
                    return false;
            }
            return true;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.6f, 0.4f, 0.2f);
            Gizmos.DrawWireCube(region.center, region.size);
        }
    }
}
