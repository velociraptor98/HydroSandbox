using UnityEngine;
using UnityEngine.Rendering;

namespace HydroSandbox
{
    /// <summary>
    /// A wind-driven FFT ocean (Tessendorf): two tiling cascades of a JONSWAP storm sea, a large
    /// patch for the swell and a small one for the chop, evolved on the GPU every frame. It
    /// publishes the displacement, slope, foam and velocity textures as globals for the ocean
    /// surface shader, and for <see cref="FluidOceanBoundary"/> to drive the particle water with.
    /// With a solver set, the waves run on the solver's simulated clock so both stay in step when
    /// the frame rate drops.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class OceanWaves : MonoBehaviour
    {
        public const int Resolution = 256;
        public const int CascadeCount = 2;
        const int Fields = 6;
        const float Gravity = 9.81f;

        public static OceanWaves Active { get; private set; }

        [SerializeField] ComputeShader fftShader;
        [Tooltip("Optional: run on this solver's simulated time instead of real time.")]
        [SerializeField] FluidSolver solver;

        [Header("Wind")]
        [Tooltip("Wind speed 10 m above the sea, m/s. 15-20 is a gale.")]
        [SerializeField, Range(2f, 30f)] float windSpeed = 16f;
        [Tooltip("Direction the wind blows toward, in degrees clockwise from +Z.")]
        [SerializeField, Range(0f, 360f)] float windDirection = 90f;
        [Tooltip("Distance over which the wind has been blowing, km. Longer fetch, bigger and longer waves.")]
        [SerializeField, Range(1f, 1000f)] float fetchKilometers = 100f;
        [Tooltip("How tightly waves align with the wind (cos^2s exponent at the spectral peak).")]
        [SerializeField, Range(1f, 30f)] float spreading = 6f;
        [SerializeField, Range(1f, 7f)] float peakEnhancement = 3.3f;

        [Header("Shape")]
        [SerializeField, Range(0f, 3f)] float heightScale = 1f;
        [Tooltip("Horizontal displacement that sharpens crests and flattens troughs; above about 1.3 crests fold through themselves.")]
        [SerializeField, Range(0f, 2f)] float choppiness = 1.1f;
        [Tooltip("Patch size of each cascade, m.")]
        [SerializeField] Vector2 cascadeSizes = new Vector2(250f, 37f);
        [SerializeField] int seed = 7;

        [Header("Foam")]
        [Tooltip("Jacobian below which a crest makes foam (1 is a flat sea, 0 a fold).")]
        [SerializeField, Range(0f, 1.5f)] float foamBias = 0.55f;
        [SerializeField, Min(0f)] float foamGain = 2.5f;
        [Tooltip("Per second; lower leaves longer streaks behind the breaking crests.")]
        [SerializeField, Min(0f)] float foamDecay = 0.35f;

        readonly Cascade[] cascades = new Cascade[CascadeCount];
        CommandBuffer cmd;
        int initKernel, timeKernel, rowsKernel, columnsKernel, assembleKernel;
        bool spectrumDirty = true;
        float time;

        public float Time => time;
        public float WindSpeed => windSpeed;
        public Vector2 WindDirection
        {
            get
            {
                float a = windDirection * Mathf.Deg2Rad;
                return new Vector2(Mathf.Sin(a), Mathf.Cos(a));
            }
        }
        public Vector2 CascadeSizes => cascadeSizes;
        /// <summary>Wavenumber of the spectral peak (rad/m): sets how fast wave motion fades with depth.</summary>
        public float PeakWavenumber
        {
            get
            {
                float omega = PeakOmega;
                return omega * omega / Gravity;
            }
        }
        public Texture Displacement(int cascade) => cascades[cascade]?.displacement;
        public Texture Velocity(int cascade) => cascades[cascade]?.velocity;
        public bool IsReady => cmd != null;

        float Fetch => fetchKilometers * 1000f;
        float PeakOmega => 22f * Mathf.Pow(Gravity * Gravity / (windSpeed * Fetch), 1f / 3f);

        class Cascade
        {
            public RenderTexture h0, spectrum, displacement, derivatives, foam, velocity;

            public void Release()
            {
                foreach (RenderTexture rt in new[] { h0, spectrum, displacement, derivatives, foam, velocity })
                {
                    if (rt != null)
                        rt.Release();
                }
            }
        }

        void OnValidate() => spectrumDirty = true;

        void OnEnable()
        {
            if (fftShader == null || !SystemInfo.supportsComputeShaders)
            {
                enabled = false;
                return;
            }
            initKernel = fftShader.FindKernel("InitSpectrum");
            timeKernel = fftShader.FindKernel("TimeSpectrum");
            rowsKernel = fftShader.FindKernel("FFTRows");
            columnsKernel = fftShader.FindKernel("FFTColumns");
            assembleKernel = fftShader.FindKernel("Assemble");
            for (int c = 0; c < CascadeCount; c++)
            {
                cascades[c] = new Cascade
                {
                    h0 = Target(RenderTextureFormat.ARGBFloat, false),
                    spectrum = Target(RenderTextureFormat.RGFloat, false, Fields),
                    displacement = Target(RenderTextureFormat.ARGBHalf, true),
                    derivatives = Target(RenderTextureFormat.ARGBHalf, true),
                    foam = Target(RenderTextureFormat.RGHalf, true),
                    velocity = Target(RenderTextureFormat.ARGBHalf, false),
                };
            }
            cmd = new CommandBuffer { name = "HydroSandbox Ocean FFT" };
            spectrumDirty = true;
            Active = this;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
            foreach (Cascade c in cascades)
                c?.Release();
            cmd?.Release();
            cmd = null;
        }

        static RenderTexture Target(RenderTextureFormat format, bool mips, int slices = 1)
        {
            var rt = new RenderTexture(Resolution, Resolution, 0, format, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                useMipMap = mips,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                anisoLevel = mips ? 4 : 0,
            };
            if (slices > 1)
            {
                rt.dimension = TextureDimension.Tex2DArray;
                rt.volumeDepth = slices;
            }
            rt.Create();
            return rt;
        }

        void Update()
        {
            if (cmd == null)
                return;
            float dt = UnityEngine.Time.deltaTime;
            if (solver != null)
                dt = solver.Paused ? 0f : solver.Settings.timeStep;
            time += dt;

            cmd.Clear();
            var cs = fftShader;
            if (spectrumDirty)
                RecordSpectrum(cs);
            cmd.SetComputeFloatParam(cs, Ids.Time, time);
            cmd.SetComputeFloatParam(cs, Ids.DeltaTime, dt);
            cmd.SetComputeFloatParam(cs, Ids.Choppiness, choppiness);
            cmd.SetComputeFloatParam(cs, Ids.FoamBias, foamBias);
            cmd.SetComputeFloatParam(cs, Ids.FoamGain, foamGain);
            cmd.SetComputeFloatParam(cs, Ids.FoamDecay, foamDecay);
            int groups = Resolution / 8;
            for (int c = 0; c < CascadeCount; c++)
            {
                Cascade cascade = cascades[c];
                cmd.SetComputeFloatParam(cs, Ids.PatchSize, cascadeSizes[c]);
                cmd.SetComputeTextureParam(cs, timeKernel, Ids.H0, cascade.h0);
                cmd.SetComputeTextureParam(cs, timeKernel, Ids.Spectrum, cascade.spectrum);
                cmd.DispatchCompute(cs, timeKernel, groups, groups, 1);
                cmd.SetComputeTextureParam(cs, rowsKernel, Ids.Spectrum, cascade.spectrum);
                cmd.DispatchCompute(cs, rowsKernel, Resolution, Fields, 1);
                cmd.SetComputeTextureParam(cs, columnsKernel, Ids.Spectrum, cascade.spectrum);
                cmd.DispatchCompute(cs, columnsKernel, Resolution, Fields, 1);
                cmd.SetComputeTextureParam(cs, assembleKernel, Ids.Spectrum, cascade.spectrum);
                cmd.SetComputeTextureParam(cs, assembleKernel, Ids.Displacement, cascade.displacement);
                cmd.SetComputeTextureParam(cs, assembleKernel, Ids.Derivatives, cascade.derivatives);
                cmd.SetComputeTextureParam(cs, assembleKernel, Ids.Foam, cascade.foam);
                cmd.SetComputeTextureParam(cs, assembleKernel, Ids.Velocity, cascade.velocity);
                cmd.DispatchCompute(cs, assembleKernel, groups, groups, 1);
                cmd.GenerateMips(cascade.displacement);
                cmd.GenerateMips(cascade.derivatives);
                cmd.GenerateMips(cascade.foam);
            }
            Graphics.ExecuteCommandBuffer(cmd);
            PublishGlobals();
        }

        void RecordSpectrum(ComputeShader cs)
        {
            spectrumDirty = false;
            float omega = PeakOmega;
            float alpha = 0.076f * Mathf.Pow(windSpeed * windSpeed / (Fetch * Gravity), 0.22f);
            cmd.SetComputeFloatParam(cs, Ids.Alpha, alpha);
            cmd.SetComputeFloatParam(cs, Ids.PeakOmega, omega);
            cmd.SetComputeFloatParam(cs, Ids.Gamma, peakEnhancement);
            cmd.SetComputeVectorParam(cs, Ids.WindDirection, WindDirection);
            cmd.SetComputeFloatParam(cs, Ids.Spreading, spreading);
            cmd.SetComputeFloatParam(cs, Ids.HeightScale, heightScale);
            cmd.SetComputeIntParam(cs, Ids.Seed, seed);
            for (int c = 0; c < CascadeCount; c++)
            {
                // Each cascade keeps the wavenumbers the next larger one cannot resolve, so no
                // wave is counted twice: the big patch stops where the small patch's longest
                // waves begin to be well sampled.
                float kMin = c == 0 ? 0f : CutoffWavenumber(c);
                float kMax = c == CascadeCount - 1 ? float.MaxValue : CutoffWavenumber(c + 1);
                cmd.SetComputeFloatParam(cs, Ids.PatchSize, cascadeSizes[c]);
                cmd.SetComputeFloatParam(cs, Ids.KMin, kMin);
                cmd.SetComputeFloatParam(cs, Ids.KMax, kMax);
                cmd.SetComputeTextureParam(cs, initKernel, Ids.H0, cascades[c].h0);
                cmd.DispatchCompute(cs, initKernel, Resolution / 8, Resolution / 8, 1);
                cmd.SetRenderTarget(cascades[c].foam);
                cmd.ClearRenderTarget(false, true, Color.clear);
            }
        }

        // Four of the smaller patch's fundamental wavelengths.
        float CutoffWavenumber(int cascade) => 2f * Mathf.PI * 4f / cascadeSizes[cascade];

        void PublishGlobals()
        {
            for (int c = 0; c < CascadeCount; c++)
            {
                Shader.SetGlobalTexture(Ids.GlobalDisplacement[c], cascades[c].displacement);
                Shader.SetGlobalTexture(Ids.GlobalDerivatives[c], cascades[c].derivatives);
                Shader.SetGlobalTexture(Ids.GlobalFoam[c], cascades[c].foam);
            }
            Shader.SetGlobalVector(Ids.GlobalCascadeSizes, new Vector4(cascadeSizes.x, cascadeSizes.y, 1f / cascadeSizes.x, 1f / cascadeSizes.y));
        }

        static class Ids
        {
            public static readonly int H0 = Shader.PropertyToID("_H0");
            public static readonly int Spectrum = Shader.PropertyToID("_Spectrum");
            public static readonly int Displacement = Shader.PropertyToID("_Displacement");
            public static readonly int Derivatives = Shader.PropertyToID("_Derivatives");
            public static readonly int Foam = Shader.PropertyToID("_Foam");
            public static readonly int Velocity = Shader.PropertyToID("_Velocity");
            public static readonly int PatchSize = Shader.PropertyToID("_PatchSize");
            public static readonly int KMin = Shader.PropertyToID("_KMin");
            public static readonly int KMax = Shader.PropertyToID("_KMax");
            public static readonly int Time = Shader.PropertyToID("_WaveTime");
            public static readonly int DeltaTime = Shader.PropertyToID("_DeltaTime");
            public static readonly int Choppiness = Shader.PropertyToID("_Choppiness");
            public static readonly int Alpha = Shader.PropertyToID("_Alpha");
            public static readonly int PeakOmega = Shader.PropertyToID("_PeakOmega");
            public static readonly int Gamma = Shader.PropertyToID("_Gamma");
            public static readonly int WindDirection = Shader.PropertyToID("_WindDirection");
            public static readonly int Spreading = Shader.PropertyToID("_Spreading");
            public static readonly int HeightScale = Shader.PropertyToID("_HeightScale");
            public static readonly int Seed = Shader.PropertyToID("_Seed");
            public static readonly int FoamBias = Shader.PropertyToID("_FoamBias");
            public static readonly int FoamGain = Shader.PropertyToID("_FoamGain");
            public static readonly int FoamDecay = Shader.PropertyToID("_FoamDecay");

            public static readonly int[] GlobalDisplacement = { Shader.PropertyToID("_OceanDisplacement0"), Shader.PropertyToID("_OceanDisplacement1") };
            public static readonly int[] GlobalDerivatives = { Shader.PropertyToID("_OceanDerivatives0"), Shader.PropertyToID("_OceanDerivatives1") };
            public static readonly int[] GlobalFoam = { Shader.PropertyToID("_OceanFoam0"), Shader.PropertyToID("_OceanFoam1") };
            public static readonly int GlobalCascadeSizes = Shader.PropertyToID("_OceanCascadeSizes");
        }
    }
}
