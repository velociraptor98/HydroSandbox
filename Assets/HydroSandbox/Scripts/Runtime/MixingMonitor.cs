using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace HydroSandbox
{
    /// <summary>
    /// A lab monitor showing what is in a <see cref="FluidTank"/>: each liquid's volume and how
    /// evenly it is mixed through the tank, with a graph of the mixing over the last two minutes.
    /// The particles are binned into coarse cells on the GPU; mixing is one minus Danckwerts'
    /// intensity of segregation of each liquid's per-cell fraction (0 % while the liquids sit in
    /// separate layers or blobs, 100 % once every cell holds the tank-wide blend).
    /// </summary>
    // After the solver, so the cells are binned from this frame's particles.
    [DefaultExecutionOrder(100)]
    public class MixingMonitor : MonoBehaviour
    {
        const int GraphWidth = 240, GraphHeight = 80;
        const float FixedOne = 1024f;
        // The layout is in pixels of a screen this wide, scaled to the screen's size.
        const float LayoutWidth = 840f;

        [SerializeField] FluidSolver solver;
        [SerializeField] FluidTank tank;
        [SerializeField] ComputeShader meterShader;
        [Tooltip("The screen's face: its local XY plane, centered, facing local -Z.")]
        [SerializeField] Transform screen;
        [SerializeField] Vector2 screenSize = new Vector2(0.42f, 0.28f);
        [Tooltip("Binning cell size in meters; liquid dispersed finer than this counts as mixed. One particle layer tall, so liquids lying in flat layers read as unmixed rather than blended where cells straddle the interface.")]
        [SerializeField] Vector3 cellSize = new Vector3(0.04f, 0.016f, 0.04f);
        [Tooltip("How far above the rim to count liquid as in the tank (streams pouring into it).")]
        [SerializeField, Min(0f)] float headroom = 0.12f;
        [SerializeField, Min(0.05f)] float readInterval = 0.25f;
        [SerializeField, Min(10f)] float historySeconds = 120f;

        GraphicsBuffer cells;
        CommandBuffer cmd;
        int clearKernel, accumulateKernel;
        Vector3Int resolution;
        int cellCount;
        Bounds region;
        float nextRead;
        bool pending;
        int generation;

        readonly float[] volumes = new float[FluidMixing.MaxLiquids];
        readonly float[] mixed = new float[FluidMixing.MaxLiquids];
        float spilled;
        readonly float[,] history = new float[FluidMixing.MaxLiquids, GraphWidth];
        int historyCount;
        float nextHistory;

        Text totalText;
        readonly Image[] swatches = new Image[FluidMixing.MaxLiquids];
        readonly Text[] names = new Text[FluidMixing.MaxLiquids];
        readonly Text[] volumeTexts = new Text[FluidMixing.MaxLiquids];
        readonly Image[] bars = new Image[FluidMixing.MaxLiquids];
        readonly Text[] percentTexts = new Text[FluidMixing.MaxLiquids];
        Texture2D graph;
        Color32[] graphPixels;

        public void Configure(FluidSolver fluid, FluidTank targetTank, ComputeShader meter, Transform screenFace, Vector2 faceSize)
        {
            solver = fluid;
            tank = targetTank;
            meterShader = meter;
            screen = screenFace;
            screenSize = faceSize;
        }

        void OnEnable()
        {
            if (solver == null || tank == null || meterShader == null)
            {
                enabled = false;
                return;
            }
            clearKernel = meterShader.FindKernel("ClearCells");
            accumulateKernel = meterShader.FindKernel("AccumulateCells");
            region = tank.Interior;
            region.max += Vector3.up * headroom;
            Vector3 size = Vector3.Max(cellSize, Vector3.one * 0.005f);
            resolution = Vector3Int.Max(Vector3Int.one, Vector3Int.CeilToInt(new Vector3(region.size.x / size.x, region.size.y / size.y, region.size.z / size.z)));
            // Whole cells from the tank floor up, so the particle layers sit mid-cell.
            region.max = region.min + Vector3.Scale(resolution, size);
            cellCount = resolution.x * resolution.y * resolution.z + 1;
            cells = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellCount * 5, 4);
            cmd = new CommandBuffer { name = "HydroSandbox Mixing Meter" };
            generation++;
            pending = false;
            if (totalText == null)
                BuildScreen();
        }

        void OnDisable()
        {
            cells?.Release();
            cells = null;
            cmd?.Release();
            cmd = null;
        }

        void OnDestroy()
        {
            if (graph != null)
                Destroy(graph);
        }

        void Update()
        {
            if (pending || Time.unscaledTime < nextRead || !solver.IsReady || solver.Mixing == null)
                return;
            nextRead = Time.unscaledTime + readInterval;

            cmd.Clear();
            var cs = meterShader;
            cmd.SetComputeIntParam(cs, "_ParticleCount", solver.Capacity);
            cmd.SetComputeVectorParam(cs, "_MeterMin", region.min);
            cmd.SetComputeVectorParam(cs, "_MeterMax", region.max);
            cmd.SetComputeIntParams(cs, "_MeterResolution", resolution.x, resolution.y, resolution.z);
            cmd.SetComputeIntParam(cs, "_MeterCellCount", cellCount);
            cmd.SetComputeBufferParam(cs, clearKernel, "_Cells", cells);
            cmd.DispatchCompute(cs, clearKernel, (cellCount * 5 + 255) / 256, 1, 1);
            cmd.SetComputeBufferParam(cs, accumulateKernel, "_Cells", cells);
            cmd.SetComputeBufferParam(cs, accumulateKernel, "_Positions", solver.PositionBuffer);
            cmd.SetComputeBufferParam(cs, accumulateKernel, "_Composition", solver.CompositionBuffer);
            cmd.DispatchCompute(cs, accumulateKernel, (solver.Capacity + 255) / 256, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);

            pending = true;
            int requestGeneration = generation;
            AsyncGPUReadback.Request(cells, request =>
            {
                if (requestGeneration != generation || this == null)
                    return;
                pending = false;
                if (!request.hasError)
                    Measure(request.GetData<uint>());
            });
        }

        void Measure(Unity.Collections.NativeArray<uint> data)
        {
            float spacing = solver.Settings.ParticleSpacing;
            float litersPerParticle = spacing * spacing * spacing * 1000f;
            int inside = cellCount - 1;

            float total = 0f;
            var sums = new float[FluidMixing.MaxLiquids];
            for (int c = 0; c < inside; c++)
            {
                total += data[c * 5];
                for (int k = 0; k < FluidMixing.MaxLiquids; k++)
                    sums[k] += data[c * 5 + 1 + k] / FixedOne;
            }
            spilled = data[inside * 5] * litersPerParticle;

            for (int k = 0; k < FluidMixing.MaxLiquids; k++)
            {
                volumes[k] = sums[k] * litersPerParticle;
                float f = total > 0f ? sums[k] / total : 0f;
                float spread = f * (1f - f);
                if (spread < 0.002f)
                {
                    mixed[k] = -1f;
                    continue;
                }
                double segregation = 0.0;
                for (int c = 0; c < inside; c++)
                {
                    float n = data[c * 5];
                    if (n <= 0f)
                        continue;
                    float deviation = data[c * 5 + 1 + k] / FixedOne - n * f;
                    segregation += deviation * deviation / n;
                }
                mixed[k] = Mathf.Clamp01(1f - (float)(segregation / (total * spread)));
            }

            if (Time.unscaledTime >= nextHistory)
            {
                nextHistory = Time.unscaledTime + historySeconds / GraphWidth;
                PushHistory();
            }
            Refresh();
        }

        void PushHistory()
        {
            if (historyCount == GraphWidth)
            {
                for (int k = 0; k < FluidMixing.MaxLiquids; k++)
                for (int x = 1; x < GraphWidth; x++)
                    history[k, x - 1] = history[k, x];
                historyCount--;
            }
            for (int k = 0; k < FluidMixing.MaxLiquids; k++)
                history[k, historyCount] = mixed[k];
            historyCount++;
            DrawGraph();
        }

        void Refresh()
        {
            FluidMixing mixing = solver.Mixing;
            float inTank = 0f;
            for (int k = 0; k < mixing.LiquidCount; k++)
                inTank += volumes[k];
            totalText.text = $"{inTank:F1} L in tank   ·   {spilled:F1} L spilled";
            for (int k = 0; k < FluidMixing.MaxLiquids; k++)
            {
                bool shown = k < mixing.LiquidCount;
                names[k].gameObject.SetActive(shown);
                swatches[k].gameObject.SetActive(shown);
                volumeTexts[k].gameObject.SetActive(shown);
                bars[k].transform.parent.gameObject.SetActive(shown);
                percentTexts[k].gameObject.SetActive(shown);
                if (!shown)
                    continue;
                FluidLiquid liquid = mixing.Liquids[k];
                bool present = volumes[k] >= 0.05f;
                Color text = present ? Color.white : new Color(1f, 1f, 1f, 0.35f);
                names[k].text = liquid.name;
                names[k].color = text;
                swatches[k].color = liquid.swatch;
                volumeTexts[k].text = present ? $"{volumes[k]:F1} L" : "—";
                volumeTexts[k].color = text;
                bool measured = present && mixed[k] >= 0f;
                bars[k].rectTransform.sizeDelta = new Vector2(measured ? 280f * mixed[k] : 0f, bars[k].rectTransform.sizeDelta.y);
                bars[k].color = liquid.swatch;
                percentTexts[k].text = measured ? $"{mixed[k] * 100f:F0}%" : "—";
                percentTexts[k].color = text;
            }
        }

        void DrawGraph()
        {
            var background = new Color32(14, 19, 24, 255);
            var grid = new Color32(40, 50, 60, 255);
            for (int i = 0; i < graphPixels.Length; i++)
                graphPixels[i] = background;
            foreach (float level in new[] { 0.25f, 0.5f, 0.75f })
            {
                int y = Mathf.RoundToInt(level * (GraphHeight - 1));
                for (int x = 0; x < GraphWidth; x++)
                    graphPixels[y * GraphWidth + x] = grid;
            }

            FluidMixing mixing = solver.Mixing;
            int offset = GraphWidth - historyCount;
            for (int k = 0; mixing != null && k < mixing.LiquidCount; k++)
            {
                Color32 color = mixing.Liquids[k].swatch;
                int previous = -1;
                for (int i = 0; i < historyCount; i++)
                {
                    float value = history[k, i];
                    if (value < 0f)
                    {
                        previous = -1;
                        continue;
                    }
                    int y = Mathf.RoundToInt(value * (GraphHeight - 1));
                    int from = previous < 0 ? y : previous;
                    for (int yy = Mathf.Min(from, y); yy <= Mathf.Max(from, y); yy++)
                        graphPixels[yy * GraphWidth + offset + i] = color;
                    previous = y;
                }
            }
            graph.SetPixels32(graphPixels);
            graph.Apply(false);
        }

        void BuildScreen()
        {
            float pixelsPerMeter = LayoutWidth / screenSize.x;
            Vector2 px = screenSize * pixelsPerMeter;
            RectTransform canvas = WorldText.CreateCanvas(screen != null ? screen : transform, "Display",
                Vector3.zero, Quaternion.identity, screenSize, pixelsPerMeter);
            var dim = new Color(0.62f, 0.68f, 0.74f);
            WorldText.AddPanel(canvas, "Background", new Rect(0f, 0f, px.x, px.y), new Color(0.035f, 0.05f, 0.065f));
            WorldText.AddText(canvas, "Title", "TANK CONTENTS", new Rect(28f, 18f, 400f, 40f), 30, dim, TextAnchor.MiddleLeft);
            totalText = WorldText.AddText(canvas, "Total", "", new Rect(px.x - 428f, 18f, 400f, 40f), 26, dim, TextAnchor.MiddleRight);
            WorldText.AddText(canvas, "Mixed Heading", "mixed through the tank", new Rect(420f, 66f, 380f, 30f), 22, dim, TextAnchor.MiddleLeft);

            for (int k = 0; k < FluidMixing.MaxLiquids; k++)
            {
                float y = 100f + k * 50f;
                swatches[k] = WorldText.AddPanel(canvas, "Swatch", new Rect(28f, y + 10f, 24f, 24f), Color.white);
                names[k] = WorldText.AddText(canvas, "Name", "", new Rect(66f, y, 190f, 44f), 28, Color.white, TextAnchor.MiddleLeft);
                volumeTexts[k] = WorldText.AddText(canvas, "Volume", "", new Rect(250f, y, 140f, 44f), 28, Color.white, TextAnchor.MiddleRight);
                Image track = WorldText.AddPanel(canvas, "Track", new Rect(420f, y + 12f, 280f, 20f), new Color(1f, 1f, 1f, 0.08f));
                var bar = WorldText.Add<Image>((RectTransform)track.transform, "Bar", new Rect(0f, 0f, 0f, 20f));
                bar.raycastTarget = false;
                bars[k] = bar;
                percentTexts[k] = WorldText.AddText(canvas, "Percent", "", new Rect(710f, y, 100f, 44f), 28, Color.white, TextAnchor.MiddleRight);
            }

            graph = new Texture2D(GraphWidth, GraphHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            graphPixels = new Color32[GraphWidth * GraphHeight];
            var image = WorldText.Add<RawImage>(canvas, "Graph", new Rect(28f, 316f, px.x - 56f, px.y - 316f - 52f));
            image.texture = graph;
            image.raycastTarget = false;
            WorldText.AddText(canvas, "Graph Caption", "mixing, last 2 minutes", new Rect(28f, px.y - 48f, 400f, 36f), 22, dim, TextAnchor.MiddleLeft);
            DrawGraph();
            for (int k = 0; k < FluidMixing.MaxLiquids; k++)
                mixed[k] = -1f;
        }
    }
}
