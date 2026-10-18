using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace HydroSandbox.Editor
{
    /// <summary>
    /// Builds the mountain-stream scene: a procedurally carved cascade on a Unity Terrain. An
    /// upper run with riffles pours over a rock lip as a waterfall into a deep plunge pool, spills
    /// over the pool's sill into two boulder-garden steps, and calms into a run-out. Boulders split
    /// the water and floating logs (two-way coupled Rigidbodies) are carried down, over the fall
    /// too. Water enters through a rectangular source at the top of the reach and drains over a
    /// weir at the bottom. Re-running rebuilds the scene and terrain from scratch.
    /// </summary>
    public static class RiverSceneBuilder
    {
        const string Root = "Assets/HydroSandbox";
        const string ScenePath = Root + "/Scenes/MountainStream.unity";
        const string TerrainFolder = Root + "/Terrain";
        const string TextureFolder = Root + "/Textures";
        const string ModelFolder = Root + "/Models";
        const string MaterialFolder = Root + "/Materials";

        const float TerrainSizeX = 26f;
        const float TerrainSizeZ = 14f;
        const float TerrainHeight = 8f;
        const int HeightmapResolution = 513;
        const int AlphamapResolution = 512;
        static readonly Vector3 TerrainOrigin = new Vector3(-13f, -1f, -7f);

        const float ReachStart = -11.5f;
        const float ReachEnd = 11.5f;
        // Coarser than the other scenes: the reach and its plunge pool hold about 21 m^3 of
        // water, which at 2 cm particles would need some 330k of them.
        const float ParticleRadius = 0.034f;
        const float LogDensity = 600f;

        const float RiverDepth = 0.42f;
        // Lip of the waterfall, and the pool below it: spill level and its sill downstream.
        const float FallX = -3.5f;
        const float PoolEndX = 0.3f;
        const float PoolLevel = 1.45f;
        const float RiverSpeed = 1.0f;
        const float BedFriction = 0.004f;
        const float WeirHeight = 0.2f;

        // ---- Landform -----------------------------------------------------------------------

        static float CenterlineZ(float x) => 1.1f * Mathf.Sin(x * 0.32f) + 0.35f * Mathf.Sin(x * 0.85f + 1.3f);

        static float CenterlineSlope(float x) => 1.1f * 0.32f * Mathf.Cos(x * 0.32f) + 0.35f * 0.85f * Mathf.Cos(x * 0.85f + 1.3f);

        static float Step(float from, float to, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, to, x));

        // Bed along the reach: an upper run, a 2 m drop over the lip into the plunge pool, which
        // shallows toward its sill, then a grade with two steps into rapids. The drop is
        // fallWidth long: 15 cm makes the waterfall face near vertical.
        static float BedHeight(float x, float fallWidth = 0.15f)
        {
            float upper = 3.0f - 0.03f * (x + 12f);
            float pool = 0.8f + 0.3f * Step(-1.2f, PoolEndX, x);
            float lower = 1.1f - 0.025f * (x - PoolEndX) - 0.35f * Step(2.8f, 3.3f, x) - 0.3f * Step(6.2f, 6.7f, x);
            if (x >= PoolEndX)
                return lower;
            return Mathf.Lerp(upper, pool, Step(FallX - fallWidth * 0.66f, FallX + fallWidth * 0.33f, x));
        }

        // 0 outside the plunge pool, 1 inside it: the channel widens and its banks rise there.
        static float PoolWeight(float x) => Step(FallX - 0.3f, FallX + 0.5f, x) * (1f - Step(-0.6f, PoolEndX + 0.2f, x));

        static float Fbm(float x, float z)
        {
            float sum = 0f, amplitude = 0.5f, frequency = 1f;
            for (int i = 0; i < 4; i++)
            {
                sum += amplitude * (Mathf.PerlinNoise(x * frequency + 37.1f, z * frequency + 11.7f) - 0.5f);
                amplitude *= 0.5f;
                frequency *= 2.03f;
            }
            return sum;
        }

        static float Height(float x, float z)
        {
            float d = Mathf.Abs(z - CenterlineZ(x));
            // The escarpment is a sheer face only where the water falls; away from the channel it
            // grades into a steep rocky slope (terrain textures smear on vertical faces) whose
            // line wanders across the valley.
            float nearFall = Mathf.Exp(-Mathf.Pow((x - FallX) / 1.5f, 2f));
            float cliffShift = (-0.3f * Step(0.6f, 3.5f, d) + 0.5f * Fbm(z * 0.9f, 7.7f)) * nearFall;
            float bed = BedHeight(x + cliffShift, Mathf.Lerp(0.15f, 1.8f, Step(0.65f, 2.2f, d)));
            float pool = PoolWeight(x);
            float halfWidth = 0.6f + 0.8f * pool;
            float bankHeight = 0.75f + 0.6f * pool;
            float banks = bankHeight * Step(halfWidth, halfWidth + 0.85f, d);
            float valley = 0.3f * Mathf.Max(0f, d - halfWidth - 0.85f) + 1.6f * Step(3f, 7f, d);
            float bedRoughness = 0.04f * Fbm(x * 1.8f, z * 1.8f);
            float hills = 0.7f * Fbm(x * 0.25f, z * 0.25f) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.5f, 4f, d));
            return bed + banks + valley + bedRoughness + hills;
        }

        // ---- Build --------------------------------------------------------------------------

        [MenuItem("HydroSandbox/Build Mountain Stream Scene")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EnsureFolder(TerrainFolder);
            EnsureFolder(MaterialFolder);

            BuildEnvironment();
            Terrain terrain = BuildTerrain();
            BuildBoulders(terrain.transform);
            GameObject fluid = BuildFluid();
            AddRiverFill(fluid, terrain);
            BuildLogs();

            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Exists(s => s.path == ScenePath))
            {
                scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
        }

        static void BuildEnvironment()
        {
            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(TextureFolder + "/Sky/alps_field_2k.hdr");
            Material skybox = LoadOrCreateMaterial("MountainStreamSky", "Skybox/Cubemap");
            skybox.SetTexture("_Tex", cubemap);
            EditorUtility.SetDirty(skybox);
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = cubemap;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.5f;
            sun.color = new Color(1f, 0.95f, 0.88f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(42f, -55f, 0f);

            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.fieldOfView = 50f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 300f;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var orbit = camera.gameObject.AddComponent<OrbitCamera>();
            // From downstream, looking up the cascade: run-out, plunge pool, fall and upper run.
            orbit.Frame(new Vector3(-2.0f, PoolLevel + 0.15f, -0.3f), 8f, -85f, 27f);

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/SampleSceneProfile.asset");
            if (profile != null)
            {
                var volume = new GameObject("Global Volume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
            }
        }

        static Terrain BuildTerrain()
        {
            string dataPath = TerrainFolder + "/MountainStreamTerrain.asset";
            AssetDatabase.DeleteAsset(dataPath);
            var data = new TerrainData
            {
                heightmapResolution = HeightmapResolution,
                alphamapResolution = AlphamapResolution,
            };
            data.size = new Vector3(TerrainSizeX, TerrainHeight, TerrainSizeZ);
            // Save first: splat textures painted before the TerrainData is an asset are not kept
            // as sub-assets, and the terrain falls back to its first layer everywhere.
            AssetDatabase.CreateAsset(data, dataPath);

            var heights = new float[HeightmapResolution, HeightmapResolution];
            for (int zi = 0; zi < HeightmapResolution; zi++)
            for (int xi = 0; xi < HeightmapResolution; xi++)
            {
                float x = TerrainOrigin.x + xi / (HeightmapResolution - 1f) * TerrainSizeX;
                float z = TerrainOrigin.z + zi / (HeightmapResolution - 1f) * TerrainSizeZ;
                heights[zi, xi] = Mathf.Clamp01((Height(x, z) - TerrainOrigin.y) / TerrainHeight);
            }
            data.SetHeights(0, 0, heights);

            data.terrainLayers = new[]
            {
                TerrainLayerAsset("RiverPebbles", "ganges_river_pebbles", 1.2f),
                TerrainLayerAsset("MossyRock", "mossy_rock", 2.5f),
                TerrainLayerAsset("ForestGround", "forrest_ground_01", 3f),
                TerrainLayerAsset("LeafyGrass", "leafy_grass", 2.5f),
            };
            PaintLayers(data);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            go.transform.position = TerrainOrigin;
            var terrain = go.GetComponent<Terrain>();
            terrain.materialTemplate = LoadOrCreateMaterial("MountainStreamTerrain", "Universal Render Pipeline/Terrain/Lit");
            terrain.heightmapPixelError = 3f;

            var collider = go.AddComponent<FluidTerrainCollider>();
            var so = new SerializedObject(collider);
            so.FindProperty("terrain").objectReferenceValue = terrain;
            so.FindProperty("friction").floatValue = BedFriction;
            so.ApplyModifiedPropertiesWithoutUndo();
            return terrain;
        }

        static void PaintLayers(TerrainData data)
        {
            var alpha = new float[AlphamapResolution, AlphamapResolution, 4];
            for (int zi = 0; zi < AlphamapResolution; zi++)
            for (int xi = 0; xi < AlphamapResolution; xi++)
            {
                float u = xi / (AlphamapResolution - 1f);
                float v = zi / (AlphamapResolution - 1f);
                float x = TerrainOrigin.x + u * TerrainSizeX;
                float z = TerrainOrigin.z + v * TerrainSizeZ;
                float d = Mathf.Abs(z - CenterlineZ(x));
                float steepness = data.GetSteepness(u, v);
                float breakup = Fbm(x * 0.9f, z * 0.9f);

                float pebbles = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.9f, 1.2f, d + breakup * 0.3f));
                float rock = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(22f, 38f, steepness)) * (1f - pebbles);
                float grass = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.8f, 3f, d + breakup * 1.5f)) * (1f - pebbles) * (1f - rock);
                float ground = Mathf.Max(0f, 1f - pebbles - rock - grass);
                float sum = pebbles + rock + grass + ground;
                alpha[zi, xi, 0] = pebbles / sum;
                alpha[zi, xi, 1] = rock / sum;
                alpha[zi, xi, 2] = ground / sum;
                alpha[zi, xi, 3] = grass / sum;
            }
            data.SetAlphamaps(0, 0, alpha);
        }

        static TerrainLayer TerrainLayerAsset(string name, string textureId, float tileMeters)
        {
            string path = $"{TerrainFolder}/{name}.terrainlayer";
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, path);
            }
            layer.diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Terrain/{textureId}_diff_2k.jpg");
            layer.normalMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Terrain/{textureId}_nor_gl_2k.jpg");
            layer.tileSize = new Vector2(tileMeters, tileMeters);
            layer.smoothness = 0.15f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        static void BuildBoulders(Transform terrain)
        {
            var parent = new GameObject("Boulders").transform;
            Mesh boulder = ModelMesh("Boulder01/boulder_01.fbx", "boulder_01_LOD1");
            Mesh slab = ModelMesh("NamaqualandBoulder02/namaqualand_boulder_02.fbx", "namaqualand_boulder_02_LOD0");
            Material boulderMaterial = ModelMaterial("Boulder01", "Boulder01/boulder_01", 0.25f);
            Material slabMaterial = ModelMaterial("NamaqualandBoulder02", "NamaqualandBoulder02/namaqualand_boulder_02", 0.25f);

            // (x along the reach, offset from the centerline, size in meters, yaw)
            var placements = new (float x, float offset, float size, float yaw, bool slab)[]
            {
                // Upper run: staggered riffle stones, big enough to break bank-full water.
                (-9.6f, 0.25f, 0.7f, 20f, false),
                (-8.1f, -0.32f, 0.65f, 80f, false),
                (-6.3f, 0.15f, 0.9f, 60f, true),
                (-4.7f, -0.35f, 0.6f, 140f, false),
                // Flanking the lip, so the fall pours through a notch, and fallen blocks at the foot
                // of the sheer face on either side of the falling water.
                (FallX - 0.25f, 0.75f, 0.95f, 10f, false),
                (FallX - 0.3f, -0.8f, 0.85f, 200f, false),
                // Around and in the plunge pool.
                (-2.6f, -1.35f, 0.95f, 300f, false),
                (-1.4f, 1.25f, 1.05f, 120f, false),
                (-0.7f, 0.3f, 0.7f, 45f, false),
                // On the sill and through the two steps below: boulder gardens and chutes.
                (0.45f, -0.5f, 0.7f, 250f, false),
                (0.6f, 0.5f, 0.65f, 90f, false),
                (2.9f, -0.3f, 0.7f, 110f, false),
                (3.3f, 0.38f, 0.6f, 330f, false),
                (4.1f, -0.05f, 0.75f, 15f, true),
                (6.3f, 0.32f, 0.75f, 200f, false),
                (6.7f, -0.38f, 0.65f, 70f, false),
                (8.6f, 0.1f, 0.65f, 300f, false),
                // On the banks, for the look of the valley.
                (-7.4f, -1.8f, 1.0f, 30f, false),
                (-5f, 1.9f, 1.1f, 30f, false),
                (-2.1f, -2.5f, 1.3f, 160f, true),
                (1.8f, -1.9f, 1.1f, 160f, true),
                (8.8f, 1.6f, 0.8f, 250f, false),
            };
            int index = 0;
            foreach (var p in placements)
            {
                Mesh mesh = p.slab ? slab : boulder;
                float z = CenterlineZ(p.x) + p.offset;
                // Sink the rock about a third into the ground so it reads as bedded, not dropped.
                Vector3 bottom = new Vector3(p.x, Height(p.x, z) - p.size * 0.3f, z);
                Place(parent, $"Boulder{index++}", mesh, p.slab ? slabMaterial : boulderMaterial, bottom, p.size, p.yaw);
            }

            // Fallen blocks in the pool against the sheer face on either side of the fall: they
            // hide the lower face, where terrain textures smear, and frame the falling water.
            foreach (float offset in new[] { 1.0f, -1.05f })
            {
                float z = CenterlineZ(FallX) + offset;
                var bottom = new Vector3(FallX + 0.3f, BedHeight(FallX + 0.5f) - 0.15f, z);
                Place(parent, $"FaceBlock{index++}", boulder, boulderMaterial, bottom, 1.5f, offset > 0f ? 35f : 215f);
            }
        }

        static void Place(Transform parent, string name, Mesh mesh, Material material, Vector3 bottom, float size, float yaw)
        {
            Bounds b = mesh.bounds;
            float scale = size / Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            Vector3 pivotToBottom = go.transform.rotation * (new Vector3(b.center.x, b.min.y, b.center.z) * scale);
            go.transform.position = bottom - pivotToBottom;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);

            // A capsule along the rock's longest horizontal axis, slightly inside the scan, for
            // both the water and the logs.
            int axis = b.size.x >= b.size.z ? 0 : 2;
            float radius = 0.5f * Mathf.Min(b.size.y, axis == 0 ? b.size.z : b.size.x) * 0.92f;
            float length = (axis == 0 ? b.size.x : b.size.z) * 0.92f;
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = axis;
            capsule.center = b.center;
            capsule.radius = radius;
            capsule.height = length;

            var fluidCollider = go.AddComponent<FluidCollider>();
            var so = new SerializedObject(fluidCollider);
            so.FindProperty("shape").enumValueIndex = (int)FluidColliderShape.Capsule;
            so.FindProperty("center").vector3Value = b.center;
            so.FindProperty("radius").floatValue = radius;
            so.FindProperty("height").floatValue = length;
            so.FindProperty("direction").intValue = axis;
            so.FindProperty("friction").floatValue = 0.25f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static GameObject BuildFluid()
        {
            var fluid = new GameObject("Fluid");
            var solver = fluid.AddComponent<FluidSolver>();
            var so = new SerializedObject(solver);
            foreach (var (field, file) in new[]
                     {
                         ("predictShader", "FluidPredict"), ("neighborSearchShader", "FluidNeighborSearch"),
                         ("neighborListShader", "FluidNeighborList"), ("densityConstraintShader", "FluidDensityConstraint"),
                         ("collisionShader", "FluidCollision"), ("velocityShader", "FluidVelocity"),
                         ("emissionShader", "FluidEmission"),
                     })
                so.FindProperty(field).objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/{file}.compute");
            so.FindProperty("capacity").intValue = 110000;
            so.FindProperty("initialParticles").intValue = 0;
            so.FindProperty("container").boundsValue = new Bounds(new Vector3(-0.25f, 2.25f, 0f), new Vector3(23.5f, 5.5f, 13.5f));
            so.FindProperty("settings.particleRadius").floatValue = ParticleRadius;
            so.FindProperty("settings.substeps").intValue = 3;
            so.FindProperty("settings.solverIterations").intValue = 3;
            // More cohesion holds the falling sheet together but makes splashes blobby and the
            // water look syrupy; at this particle size even 0.3 is far stronger than real surface
            // tension.
            so.FindProperty("settings.cohesion").floatValue = 0.1f;
            so.FindProperty("settings.viscosity").floatValue = 0.01f;
            so.FindProperty("settings.vorticityConfinement").floatValue = 0.04f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var coupling = fluid.AddComponent<FluidRigidbodyCoupling>();
            var couplingSo = new SerializedObject(coupling);
            couplingSo.FindProperty("couplingShader").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/FluidCoupling.compute");
            couplingSo.ApplyModifiedPropertiesWithoutUndo();

            var whitewater = fluid.AddComponent<FluidWhitewater>();
            var whitewaterSo = new SerializedObject(whitewater);
            whitewaterSo.FindProperty("whitewaterShader").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/FluidWhitewater.compute");
            // The fall's impact alone would fill a 256k pool; each live particle is advected and
            // drawn every frame, so a smaller pool and gentler emission keep whitewater affordable.
            whitewaterSo.FindProperty("capacity").intValue = 98304;
            whitewaterSo.FindProperty("trappedAirRange").vector2Value = new Vector2(5f, 22f);
            whitewaterSo.FindProperty("trappedAirRate").floatValue = 150f;
            whitewaterSo.FindProperty("energyRange").vector2Value = new Vector2(0.6f, 6f);
            whitewaterSo.FindProperty("lifetimeRange").vector2Value = new Vector2(0.8f, 2.2f);
            whitewaterSo.ApplyModifiedPropertiesWithoutUndo();

            var surface = fluid.AddComponent<FluidSurface>();
            var surfaceSo = new SerializedObject(surface);
            surfaceSo.FindProperty("solver").objectReferenceValue = solver;
            surfaceSo.FindProperty("whitewater").objectReferenceValue = whitewater;
            surfaceSo.FindProperty("surfaceShader").objectReferenceValue = Shader.Find("HydroSandbox/FluidSurface");
            surfaceSo.FindProperty("rippleNormal").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/WaterRipples_Normal.png");
            surfaceSo.FindProperty("foamMask").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/Foam002_Mask.png");
            // Mountain water: clearer and greener than the tinted fountain pool, but absorbing enough
            // that the deep middle of the channel reads darker than the margins.
            surfaceSo.FindProperty("absorption").vector3Value = new Vector3(1.8f, 0.55f, 0.7f);
            surfaceSo.FindProperty("scatterColor").colorValue = new Color(0.07f, 0.2f, 0.17f);
            surfaceSo.ApplyModifiedPropertiesWithoutUndo();

            var debugRenderer = fluid.AddComponent<FluidDebugRenderer>();
            var debugSo = new SerializedObject(debugRenderer);
            debugSo.FindProperty("solver").objectReferenceValue = solver;
            debugSo.FindProperty("particleShader").objectReferenceValue = Shader.Find("HydroSandbox/FluidDebugParticle");
            debugSo.ApplyModifiedPropertiesWithoutUndo();

            var panel = fluid.AddComponent<FluidDebugPanel>();
            var panelSo = new SerializedObject(panel);
            panelSo.FindProperty("solver").objectReferenceValue = solver;
            panelSo.FindProperty("visible").boolValue = false;
            panelSo.FindProperty("sphereRenderer").objectReferenceValue = debugRenderer;
            panelSo.FindProperty("waterSurface").objectReferenceValue = surface;
            panelSo.FindProperty("viewMode").enumValueIndex = (int)FluidViewMode.Water;
            panelSo.ApplyModifiedPropertiesWithoutUndo();

            // Inflow: a rectangle across the channel, from the bed to near the surface, aimed down
            // the channel.
            float sx = ReachStart;
            var source = new GameObject("Inflow").AddComponent<FluidSource>();
            source.transform.position = new Vector3(sx, BedHeight(sx) + 0.05f + RiverDepth * 0.5f, CenterlineZ(sx));
            source.transform.rotation = Quaternion.LookRotation(new Vector3(1f, 0f, CenterlineSlope(sx)).normalized, Vector3.up);
            var sourceSo = new SerializedObject(source);
            sourceSo.FindProperty("shape").enumValueIndex = (int)FluidSourceShape.Rectangle;
            sourceSo.FindProperty("size").floatValue = 1.3f;
            sourceSo.FindProperty("rectangleHeight").floatValue = RiverDepth;
            sourceSo.FindProperty("speed").floatValue = RiverSpeed;
            sourceSo.ApplyModifiedPropertiesWithoutUndo();

            // Outflow over a weir: only water above the sill is drained, so the end of the reach
            // holds its depth instead of draining down to a free overfall.
            var sink = new GameObject("Outflow").AddComponent<FluidSink>();
            sink.transform.position = new Vector3(ReachEnd - 0.4f, BedHeight(ReachEnd) + WeirHeight + 1.5f, CenterlineZ(ReachEnd));
            sink.transform.localScale = new Vector3(1.4f, 3f, 5f);
            return fluid;
        }

        // Starts the reach already flowing instead of waiting for the inflow to fill it.
        static void AddRiverFill(GameObject fluid, Terrain terrain)
        {
            var points = new System.Collections.Generic.List<Vector3>();
            // A point's height is a minimum water level: the pool fills to its spill level.
            for (float x = ReachStart; x <= ReachEnd - 0.8f; x += 0.5f)
            {
                bool inPool = x > FallX + 0.1f && x < PoolEndX;
                points.Add(new Vector3(x, inPool ? PoolLevel : BedHeight(x), CenterlineZ(x)));
            }
            fluid.AddComponent<RiverFill>().Configure(terrain, points.ToArray(), 4.0f, RiverDepth, RiverSpeed);
        }

        static void BuildLogs()
        {
            var parent = new GameObject("Logs").transform;
            Mesh trunk = ModelMesh("DeadTreeTrunk02/dead_tree_trunk_02.fbx", "dead_tree_trunk_02_LOD2");
            Material bark = ModelMaterial("DeadTreeTrunk02", "DeadTreeTrunk02/dead_tree_trunk_02", 0.2f);
            Bounds b = trunk.bounds;

            var respawn = new GameObject("LogRespawn").transform;
            respawn.SetParent(parent);
            float rx = ReachStart + 1.8f;
            respawn.position = new Vector3(rx, BedHeight(rx) + RiverDepth + 0.15f, CenterlineZ(rx));
            respawn.rotation = Quaternion.LookRotation(new Vector3(0f, 0f, 1f));

            var lengths = new[] { 1.1f, 0.9f, 1.3f, 1.0f };
            var diameters = new[] { 0.18f, 0.15f, 0.22f, 0.17f };
            for (int i = 0; i < lengths.Length; i++)
            {
                float x = ReachStart + 2f + i * 1.6f;
                var go = new GameObject($"Log{i}");
                go.transform.SetParent(parent);
                go.transform.position = new Vector3(x, BedHeight(x) + RiverDepth + 0.05f, CenterlineZ(x) + (i % 2 == 0 ? 0.12f : -0.12f));
                go.transform.rotation = Quaternion.Euler(0f, 70f + i * 25f, 0f);
                // The trunk scan's long axis is X; scale it to a stream-sized log.
                go.transform.localScale = new Vector3(lengths[i] / b.size.x, diameters[i] / b.size.y, diameters[i] / b.size.z);
                go.AddComponent<MeshFilter>().sharedMesh = trunk;
                go.AddComponent<MeshRenderer>().sharedMaterial = bark;

                float radius = 0.5f * b.size.y * 0.9f;
                var capsule = go.AddComponent<CapsuleCollider>();
                capsule.direction = 0;
                capsule.center = b.center;
                capsule.radius = radius;
                capsule.height = b.size.x * 0.95f;

                var body = go.AddComponent<Rigidbody>();
                float r = diameters[i] * 0.5f * 0.9f;
                body.mass = LogDensity * Mathf.PI * r * r * lengths[i];
                body.linearDamping = 0.05f;
                body.angularDamping = 0.4f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                var fluidCollider = go.AddComponent<FluidCollider>();
                var so = new SerializedObject(fluidCollider);
                so.FindProperty("shape").enumValueIndex = (int)FluidColliderShape.Capsule;
                so.FindProperty("center").vector3Value = b.center;
                so.FindProperty("radius").floatValue = radius;
                so.FindProperty("height").floatValue = b.size.x * 0.95f;
                so.FindProperty("direction").intValue = 0;
                so.FindProperty("friction").floatValue = 0.35f;
                so.ApplyModifiedPropertiesWithoutUndo();

                var recycler = go.AddComponent<DebrisRecycler>();
                var recyclerSo = new SerializedObject(recycler);
                // Ends where the weir starts: the water over the sill is too shallow to float a log.
                recyclerSo.FindProperty("region").boundsValue = new Bounds(new Vector3(-0.75f, 2.5f, 0f), new Vector3(22f, 7f, 13f));
                recyclerSo.FindProperty("respawnPoint").objectReferenceValue = respawn;
                recyclerSo.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ---- Assets -------------------------------------------------------------------------

        static Mesh ModelMesh(string fbx, string meshName) =>
            AssetDatabase.LoadAllAssetsAtPath($"{ModelFolder}/{fbx}").OfType<Mesh>().First(m => m.name == meshName);

        static Material ModelMaterial(string name, string texturePrefix, float smoothness)
        {
            Material material = LoadOrCreateMaterial(name, "Universal Render Pipeline/Lit");
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{ModelFolder}/{texturePrefix}_diff_2k.jpg"));
            material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{ModelFolder}/{texturePrefix}_nor_gl_2k.jpg"));
            material.SetFloat("_Smoothness", smoothness);
            material.EnableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material LoadOrCreateMaterial(string name, string shaderName)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find(shaderName));
                AssetDatabase.CreateAsset(material, path);
            }
            return material;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
