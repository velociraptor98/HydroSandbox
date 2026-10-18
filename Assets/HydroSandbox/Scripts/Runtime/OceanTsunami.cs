using UnityEngine;
using UnityEngine.InputSystem;

namespace HydroSandbox
{
    /// <summary>
    /// Clicking the scene sends a tsunami at the target from a random bearing (within a set angle
    /// of square-on to one of the zone's sides, so the inflow can span the zone): one solitary wave,
    /// steep in front, that rises out of the storm sea far off and runs in at shallow-water speed.
    /// The heightfield sea draws it everywhere. The particle zone cannot make a wave that size
    /// from the water it holds, so while the crest crosses the zone's upstream edge a source
    /// there feeds in the wave's own water at its height and flow speed (and the edge forcing
    /// adds its flow); once the wave has passed the target, drains along the zone's edges take
    /// the surplus back out until the particle count is what it was before.
    /// </summary>
    // Before the solver and the edge forcing, which read this frame's wave.
    [DefaultExecutionOrder(-60)]
    public class OceanTsunami : MonoBehaviour
    {
        const float ClickSlopPixels = 6f;

        public static OceanTsunami Active { get; private set; }

        [SerializeField] FluidSolver solver;
        [SerializeField] OceanWaves ocean;
        [Tooltip("The wave is aimed here (the lighthouse).")]
        [SerializeField] Transform target;
        [Tooltip("Rectangle source that feeds the wave's water into the zone; disabled between waves.")]
        [SerializeField] FluidSource inflow;
        [Tooltip("Drains along the zone's edges, enabled after the wave until the surplus is gone.")]
        [SerializeField] FluidSink[] drains = System.Array.Empty<FluidSink>();
        [SerializeField] float seaLevel;

        [Header("Wave")]
        [SerializeField, Min(0.5f)] float amplitude = 5.5f;
        [Tooltip("Distance ahead of the crest over which the face falls away, m: the steep front.")]
        [SerializeField, Min(0.5f)] float frontWidth = 3.5f;
        [Tooltip("Distance behind the crest over which the back falls away, m.")]
        [SerializeField, Min(0.5f)] float backWidth = 7f;
        [Tooltip("Half-width of the wave across its travel, m.")]
        [SerializeField, Min(1f)] float lateralWidth = 45f;
        [SerializeField, Min(1f)] float speed = 16f;
        [Tooltip("Where the wave starts, m from the target.")]
        [SerializeField, Min(10f)] float startDistance = 170f;
        [Tooltip("Water depth used for the flow speed under the wave, m.")]
        [SerializeField, Min(0.5f)] float flowDepth = 4f;
        [Tooltip("Widest the inflow gets across the wave, m; it is narrowed to fit the zone.")]
        [SerializeField, Min(1f)] float inflowWidth = 40f;
        [Tooltip("Closest the inflow may be to the target, m: it must stay off the island.")]
        [SerializeField, Min(0f)] float inflowClearance = 13.5f;
        [Tooltip("Largest angle between the wave's bearing and square-on to a side of the zone, degrees. Steeper diagonals leave too little room for the inflow inside the rectangle.")]
        [SerializeField, Range(0f, 45f)] float maxObliquity = 22f;
        [Tooltip("Seconds the drains take to remove the current surplus. They slow as it shrinks, so the readback lag of the particle count does not let them overshoot.")]
        [SerializeField, Min(0.1f)] float drainSeconds = 2.5f;
        [Tooltip("Longest the drains stay open after the wave, s.")]
        [SerializeField, Min(1f)] float maxDrainSeconds = 45f;

        Vector2 direction;
        float launchTime = float.NaN;
        float inflowOffset;
        float inflowLength;
        int baselineCount;
        float drainStart = float.NaN;
        bool fedSinceLaunch;
        bool pressValid;
        Vector2 pressPosition;

        // Launching a player with -hydroTsunami sends one wave once the zone has settled, for
        // measuring the frame cost of an impact (FrameStatsLogger).
        const string CommandLineFlag = "-hydroTsunami";
        bool launchFromCommandLine;

        /// <summary>A wave is on its way in or still within sight; another click waits for it.</summary>
        public bool InProgress => !float.IsNaN(launchTime);
        bool Draining => !float.IsNaN(drainStart);

        /// <summary>Shader parameters of the current wave (amplitude 0 when there is none); see OceanCommon.hlsl.</summary>
        public Vector4 Params0 { get; private set; }
        public Vector4 Params1 => new Vector4(frontWidth, backWidth, lateralWidth, speed);
        public Vector4 Params2 => new Vector4(TargetXZ.x, TargetXZ.y, flowDepth, 0f);

        public void Configure(FluidSolver fluid, OceanWaves waves, Transform aim, FluidSource source, FluidSink[] edgeDrains, float level)
        {
            solver = fluid;
            ocean = waves;
            target = aim;
            inflow = source;
            drains = edgeDrains;
            seaLevel = level;
        }

        Vector2 TargetXZ => target != null ? new Vector2(target.position.x, target.position.z) : Vector2.zero;
        float Now => ocean != null ? ocean.Time : Time.time;

        void OnEnable()
        {
            Active = this;
            launchFromCommandLine = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), CommandLineFlag) >= 0;
            SetDrains(false);
            if (inflow != null)
                inflow.enabled = false;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
            Params0 = Vector4.zero;
            Publish();
        }

        void Update()
        {
            // The count to drain back to: what the zone holds before the first wave. Kept from
            // then on, since each drain stops a little off it and re-measuring would let the sea
            // level drift wave by wave.
            if (baselineCount == 0 && !InProgress && solver != null && solver.AliveCount > 0)
                baselineCount = solver.AliveCount;
            bool commandLine = launchFromCommandLine && baselineCount > 0;
            if (commandLine)
                launchFromCommandLine = false;
            if ((ReadClick() || commandLine) && !InProgress)
                Launch();
            if (InProgress)
                Advance();
            if (Draining)
                Drain();
            Publish();
        }

        bool ReadClick()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return false;
            Vector2 position = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame)
            {
                pressValid = GUIUtility.hotControl == 0;
                pressPosition = position;
            }
            if ((position - pressPosition).sqrMagnitude > ClickSlopPixels * ClickSlopPixels)
                pressValid = false;
            if (!mouse.leftButton.wasReleasedThisFrame || !pressValid)
                return false;
            pressValid = false;
            return true;
        }

        public void Launch()
        {
            float bearing = (Random.Range(0, 4) * 90f + Random.Range(-maxObliquity, maxObliquity)) * Mathf.Deg2Rad;
            direction = -new Vector2(Mathf.Cos(bearing), Mathf.Sin(bearing));
            launchTime = Now;
            fedSinceLaunch = false;
            PlaceInflow();
        }

        float Crest => -startDistance + speed * (Now - launchTime);

        void Advance()
        {
            float crest = Crest;
            // Rises out of the sea at the start of its run and sinks back at the end, so neither
            // the first wave appearing nor a new one replacing it pops.
            float presence = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-startDistance, -startDistance + 30f, crest))
                           * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(startDistance - 50f, startDistance, crest)));
            Params0 = new Vector4(direction.x, direction.y, crest, amplitude * presence);
            UpdateInflow(crest);

            // Drain once the wave is past the target and has stopped feeding in water.
            if (fedSinceLaunch && crest > backWidth * 3f && (!Draining || drainStart < launchTime))
            {
                drainStart = Now;
                SetDrains(true);
            }
            if (crest > startDistance)
            {
                launchTime = float.NaN;
                Params0 = Vector4.zero;
            }
        }

        void Drain()
        {
            // The inflow sits inside the drain strip on its side: a new wave arriving while the
            // last one drains must not be drained as it comes in.
            if (inflow != null && inflow.enabled)
            {
                SetDrains(false);
                return;
            }
            SetDrains(true);
            bool drained = (solver != null && solver.AliveCount <= baselineCount) || Now - drainStart > maxDrainSeconds;
            if (!drained)
            {
                ThrottleDrains();
                return;
            }
            SetDrains(false);
            drainStart = float.NaN;
        }

        // Height of the wave at the inflow line, and the source feeding it in at that height.
        void UpdateInflow(float crest)
        {
            if (inflow == null || float.IsNaN(inflowOffset))
                return;
            float along = inflowOffset - crest;
            float width = along > 0f ? frontWidth : backWidth;
            float t = (float)System.Math.Tanh(Mathf.Clamp(along / width, -15f, 15f));
            float height = amplitude * (1f - t * t);
            bool feeding = height > 0.15f && !(crest > inflowOffset + backWidth * 3f);
            inflow.enabled = feeding;
            if (!feeding)
                return;
            fedSinceLaunch = true;
            Vector2 center = TargetXZ + direction * inflowOffset;
            var forward = new Vector3(direction.x, 0f, direction.y);
            inflow.transform.SetPositionAndRotation(new Vector3(center.x, seaLevel + height * 0.5f, center.y), Quaternion.LookRotation(forward, Vector3.up));
            inflow.SetRectangle(inflowLength, height);
            inflow.Speed = speed * height / (flowDepth + height);
        }

        // The inflow line crosses the wave's path upstream of the island: as far upstream as it
        // can be while still at least 70% of its full width once clipped to the zone, but never
        // closer to the target than the clearance.
        void PlaceInflow()
        {
            inflowOffset = float.NaN;
            if (solver == null)
                return;
            Bounds zone = solver.Container;
            for (float s = -100f; s <= -inflowClearance; s += 0.25f)
            {
                float half = HalfWidthInZone(zone, TargetXZ + direction * s);
                if (half >= inflowWidth * 0.35f || s + 0.25f > -inflowClearance)
                {
                    inflowOffset = s;
                    inflowLength = 2f * Mathf.Min(half, inflowWidth * 0.5f);
                    return;
                }
            }
        }

        // Half-length of the line across the wave, centered at c, that stays inside the zone.
        float HalfWidthInZone(Bounds zone, Vector2 c)
        {
            const float inset = 0.6f;
            var side = new Vector2(-direction.y, direction.x);
            Vector2 min = new Vector2(zone.min.x, zone.min.z) + Vector2.one * inset;
            Vector2 max = new Vector2(zone.max.x, zone.max.z) - Vector2.one * inset;
            if (c.x <= min.x || c.y <= min.y || c.x >= max.x || c.y >= max.y)
                return 0f;
            float half = float.MaxValue;
            for (int axis = 0; axis < 2; axis++)
            {
                if (Mathf.Abs(side[axis]) < 1e-5f)
                    continue;
                half = Mathf.Min(half, Mathf.Min((max[axis] - c[axis]) / Mathf.Abs(side[axis]), (c[axis] - min[axis]) / Mathf.Abs(side[axis])));
            }
            return half;
        }

        void ThrottleDrains()
        {
            if (solver == null || drains.Length == 0)
                return;
            float spacing = solver.Settings.ParticleSpacing;
            float surplusLiters = Mathf.Max(0, solver.AliveCount - baselineCount) * spacing * spacing * spacing * 1000f;
            // A small floor keeps the last of it moving; 0 would mean unlimited.
            float rate = Mathf.Max(surplusLiters / drainSeconds / drains.Length, 50f);
            foreach (FluidSink drain in drains)
            {
                if (drain != null)
                    drain.DrainRate = rate;
            }
        }

        void SetDrains(bool open)
        {
            foreach (FluidSink drain in drains)
            {
                if (drain != null)
                    drain.enabled = open;
            }
        }

        void Publish()
        {
            Shader.SetGlobalVector(Ids.Tsunami0, Params0);
            Shader.SetGlobalVector(Ids.Tsunami1, Params1);
            Shader.SetGlobalVector(Ids.Tsunami2, Params2);
        }

        static class Ids
        {
            public static readonly int Tsunami0 = Shader.PropertyToID("_Tsunami0");
            public static readonly int Tsunami1 = Shader.PropertyToID("_Tsunami1");
            public static readonly int Tsunami2 = Shader.PropertyToID("_Tsunami2");
        }
    }
}
