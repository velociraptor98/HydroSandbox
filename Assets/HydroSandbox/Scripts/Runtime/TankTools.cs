using UnityEngine;
using UnityEngine.InputSystem;

namespace HydroSandbox
{
    /// <summary>
    /// The player's hand in a <see cref="FluidTank"/> scene. With nothing held, clicking a
    /// <see cref="PourNozzle"/> starts or stops its pour and clicking a <see cref="BenchItem"/> picks
    /// it up; dragging orbits the camera. A held item follows the cursor and gets the left button
    /// (and the scroll wheel, if it wants it); right drag orbits instead, and a right click or
    /// Escape puts the item back. A click is a press and release without dragging.
    /// </summary>
    // Before FluidRigidbodyCoupling, so a held paddle's velocity is set before physics steps.
    [DefaultExecutionOrder(-200)]
    public class TankTools : MonoBehaviour
    {
        const float ClickSlopPixels = 6f;
        const int MaxHits = 16;

        [SerializeField] FluidTank tank;
        [SerializeField] FluidSolver solver;
        [Tooltip("Names the liquids in the nozzle tooltips.")]
        [SerializeField] FluidMixing mixing;
        [Tooltip("Optional: clicks over its panel are ignored.")]
        [SerializeField] MixingPanel panel;

        readonly RaycastHit[] rawHits = new RaycastHit[MaxHits];
        readonly RaycastHit[] hits = new RaycastHit[MaxHits];
        Camera view;
        OrbitCamera orbit;
        BenchItem held;
        PourNozzle hoveredNozzle;
        BenchItem hoveredItem;
        readonly ClickTracker left = new ClickTracker();
        readonly ClickTracker right = new ClickTracker();

        public void Configure(FluidTank targetTank, FluidSolver fluid, FluidMixing liquids, MixingPanel mixingPanel)
        {
            tank = targetTank;
            solver = fluid;
            mixing = liquids;
            panel = mixingPanel;
        }

        void OnDisable()
        {
            SetHovered(null);
            if (held != null)
                held.PutBack();
            held = null;
        }

        void Update()
        {
            Mouse mouse = Mouse.current;
            if (view == null)
            {
                view = Camera.main;
                orbit = view != null ? view.GetComponent<OrbitCamera>() : null;
            }
            if (mouse == null || view == null)
                return;

            Vector2 screen = mouse.position.ReadValue();
            bool overUI = panel != null && panel.ScreenRect.Contains(new Vector2(screen.x, Screen.height - screen.y));
            bool free = !overUI && GUIUtility.hotControl == 0;
            left.Update(mouse.leftButton, screen, free);
            right.Update(mouse.rightButton, screen, free);

            Ray ray = view.ScreenPointToRay(screen);
            int hitCount = overUI ? 0 : CollectHits(ray);
            hoveredItem = null;
            PourNozzle nozzle = null;
            if (hitCount > 0)
            {
                nozzle = hits[0].collider.GetComponentInParent<PourNozzle>();
                if (held == null)
                    hoveredItem = FirstItem(hitCount);
            }
            SetHovered(nozzle);

            Keyboard keyboard = Keyboard.current;
            if (held != null && (right.Clicked || (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)))
            {
                held.PutBack();
                held = null;
            }

            var input = new HandInput
            {
                ray = ray,
                planePoint = PlanePoint(ray),
                hits = hits,
                hitCount = hitCount,
                // A click on a nozzle pours whatever is in hand.
                useHeld = free && nozzle == null && mouse.leftButton.isPressed,
                useClicked = nozzle == null && left.Clicked,
                scroll = free ? mouse.scroll.ReadValue().y : 0f,
                physicsStep = solver != null && solver.GetComponent<FluidRigidbodyCoupling>() != null ? solver.Settings.timeStep : Time.fixedDeltaTime,
                tank = tank,
            };

            if (nozzle != null && left.Clicked)
                nozzle.Toggle();
            else if (held == null && hoveredItem != null && left.Clicked)
            {
                held = hoveredItem;
                hoveredItem = null;
                // The click that picked it up must not also use it.
                input.useClicked = false;
                held.PickUp(input);
            }
            if (held != null && !held.Hold(input))
                held = null;

            if (orbit != null)
            {
                orbit.LeftButtonFree = held == null;
                orbit.ScrollFree = held == null || !held.CapturesScroll;
            }
        }

        int CollectHits(Ray ray)
        {
            int raw = Physics.RaycastNonAlloc(ray, rawHits, 100f, ~0, QueryTriggerInteraction.Collide);
            int count = 0;
            for (int i = 0; i < raw; i++)
            {
                if (held == null || !rawHits[i].collider.transform.IsChildOf(held.transform))
                    hits[count++] = rawHits[i];
            }
            System.Array.Sort(hits, 0, count, HitDistanceComparer.Instance);
            return count;
        }

        // An item may sit behind the glass, so look past the panes for it.
        BenchItem FirstItem(int hitCount)
        {
            for (int i = 0; i < hitCount; i++)
            {
                Transform t = hits[i].collider.transform;
                var item = t.GetComponentInParent<BenchItem>();
                if (item != null)
                    return item;
                if (tank == null || !t.IsChildOf(tank.transform))
                    return null;
            }
            return null;
        }

        Vector3 PlanePoint(Ray ray)
        {
            Vector3 center = tank != null ? tank.Interior.center : Vector3.zero;
            Vector3 normal = Vector3.ProjectOnPlane(view.transform.forward, Vector3.up);
            if (normal.sqrMagnitude < 1e-6f)
                normal = Vector3.forward;
            var plane = new Plane(normal.normalized, center);
            return plane.Raycast(ray, out float enter) && enter > 0f ? ray.GetPoint(enter) : ray.GetPoint(2f);
        }

        void SetHovered(PourNozzle nozzle)
        {
            if (hoveredNozzle == nozzle)
                return;
            if (hoveredNozzle != null)
                hoveredNozzle.Hovered = false;
            hoveredNozzle = nozzle;
            if (nozzle != null)
                nozzle.Hovered = true;
        }

        void OnGUI()
        {
            if (Mouse.current == null)
                return;
            string text = null;
            if (hoveredNozzle != null && hoveredNozzle.Source != null)
            {
                int liquid = hoveredNozzle.Source.Liquid;
                string name = mixing != null && liquid < mixing.LiquidCount ? mixing.Liquids[liquid].name : "liquid";
                text = hoveredNozzle.Pouring ? $"Stop {name}" : $"Pour {name}";
            }
            else if (held != null)
                text = held.HeldHint;
            else if (hoveredItem != null)
                text = hoveredItem is DropObject drop
                    ? $"{hoveredItem.DisplayName} · {drop.Density:N0} kg/m³\nClick to pick up"
                    : $"Pick up {hoveredItem.DisplayName}";
            if (string.IsNullOrEmpty(text))
                return;

            Vector2 m = Mouse.current.position.ReadValue();
            var content = new GUIContent(text);
            Vector2 size = GUI.skin.box.CalcSize(content);
            GUI.Label(new Rect(m.x + 18f, Screen.height - m.y + 10f, size.x, size.y), content, GUI.skin.box);
        }

        sealed class ClickTracker
        {
            bool valid;
            Vector2 pressPosition;

            public bool Clicked { get; private set; }

            public void Update(UnityEngine.InputSystem.Controls.ButtonControl button, Vector2 screen, bool free)
            {
                Clicked = false;
                if (button.wasPressedThisFrame)
                {
                    valid = free;
                    pressPosition = screen;
                }
                if ((screen - pressPosition).sqrMagnitude > ClickSlopPixels * ClickSlopPixels)
                    valid = false;
                if (button.wasReleasedThisFrame && valid)
                {
                    valid = false;
                    Clicked = true;
                }
            }
        }

        sealed class HitDistanceComparer : System.Collections.Generic.IComparer<RaycastHit>
        {
            public static readonly HitDistanceComparer Instance = new HitDistanceComparer();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }
    }
}
