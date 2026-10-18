using UnityEngine;

namespace HydroSandbox
{
    /// <summary>What <see cref="TankTools"/> hands the held item each frame.</summary>
    public struct HandInput
    {
        public Ray ray;
        /// <summary>The cursor on the vertical plane through the tank's center that faces the camera.</summary>
        public Vector3 planePoint;
        /// <summary>Scene hits under the cursor, nearest first, without the held item's own colliders.</summary>
        public RaycastHit[] hits;
        public int hitCount;
        public bool useHeld;
        /// <summary>The use button went down and up without dragging.</summary>
        public bool useClicked;
        public float scroll;
        /// <summary>The time step of the next physics step.</summary>
        public float physicsStep;
        public FluidTank tank;
    }

    /// <summary>
    /// Something on the workbench the player can pick up with <see cref="TankTools"/> and use on the
    /// tank. Picked-up items follow the cursor until they are used up or put back.
    /// </summary>
    public abstract class BenchItem : MonoBehaviour
    {
        [SerializeField] string displayName = "Item";
        [Tooltip("Shown by the cursor while the item is held.")]
        [SerializeField, TextArea] string heldHint = "";

        Vector3 homePosition;
        Quaternion homeRotation;

        public string DisplayName => displayName;
        public virtual string HeldHint => heldHint;
        public virtual bool CapturesScroll => false;

        protected virtual void Awake()
        {
            homePosition = transform.position;
            homeRotation = transform.rotation;
        }

        public void ConfigureItem(string name, string hint)
        {
            displayName = name;
            heldHint = hint;
        }

        internal virtual void PickUp(in HandInput input) { }

        /// <summary>Called every frame while held; returns false once the item has let go of itself.</summary>
        internal abstract bool Hold(in HandInput input);

        internal virtual void PutBack() => transform.SetPositionAndRotation(homePosition, homeRotation);

        protected Vector3 HomePosition => homePosition;
        protected Quaternion HomeRotation => homeRotation;

        /// <summary>The first hit on the tank's glass, if the cursor is over it.</summary>
        protected static bool TryGlassHit(in HandInput input, out RaycastHit glass)
        {
            for (int i = 0; i < input.hitCount; i++)
            {
                RaycastHit hit = input.hits[i];
                if (input.tank != null && hit.collider.transform.IsChildOf(input.tank.transform))
                {
                    glass = hit;
                    return true;
                }
                if (hit.collider.GetComponentInParent<BenchItem>() == null)
                    break;
            }
            glass = default;
            return false;
        }
    }
}
