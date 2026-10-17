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
    /// Builds the mountain-stream scene: a procedurally carved, stepped stream channel on a Unity
    /// Terrain, boulders the water splits around, and floating logs carried downstream (two-way
    /// coupled Rigidbodies). Water enters through a rectangular source at the top of the reach
    /// and drains at the bottom. Re-running rebuilds the scene and terrain from scratch.
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
        const float TerrainHeight = 6f;
        const int HeightmapResolution = 513;
        const int AlphamapResolution = 512;
        static readonly Vector3 TerrainOrigin = new Vector3(-13f, -1f, -7f);

        const float ReachStart = -11.5f;
        const float ReachEnd = 11.5f;
        const float ParticleRadius = 0.02f;
        const float LogDensity = 600f;

        // ---- Landform -----------------------------------------------------------------------

        static float CenterlineZ(float x) => 1.1f * Mathf.Sin(x * 0.32f) + 0.35f * Mathf.Sin(x * 0.85f + 1.3f);

        static float CenterlineSlope(float x) => 1.1f * 0.32f * Mathf.Cos(x * 0.32f) + 0.35f * 0.85f * Mathf.Cos(x * 0.85f + 1.3f);

        // A gentle grade with two steps; the water accelerates over each step into rapids.
        static float BedHeight(float x)
        {
            float h = 1.35f - 0.028f * (x + 12f);
            h -= 0.3f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-3.6f, -2.8f, x));
            h -= 0.35f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4.0f, 4.7f, x));
            return h;
        }

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
            float bed = BedHeight(x);
            float banks = 0.75f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 1.45f, d));
            float valley = 0.3f * Mathf.Max(0f, d - 1.45f) + 1.6f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 7f, d));
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
            // Framed on the first step, where the rapids and the boulders around it are.
            orbit.Frame(new Vector3(-3.2f, BedHeight(-3.2f) + 0.2f, CenterlineZ(-3.2f)), 4.8f, 25f, 32f);

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
                (-8.6f, 0.18f, 0.45f, 20f, false),
                (-6.1f, -0.28f, 0.36f, 80f, false),
                // Staggered across each step so they narrow the flow into rapids without damming it.
                (-3.4f, 0.3f, 0.45f, 140f, false),
                (-2.4f, -0.35f, 0.36f, 10f, false),
                (0.4f, 0.02f, 0.6f, 60f, true),
                (3.6f, -0.3f, 0.42f, 110f, false),
                (4.9f, 0.32f, 0.45f, 200f, false),
                (7.6f, 0.1f, 0.42f, 300f, false),
                // On the banks, for the look of the valley.
                (-5f, 1.7f, 0.9f, 30f, false),
                (2.2f, -1.9f, 1.1f, 160f, true),
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
            so.FindProperty("capacity").intValue = 150000;
            so.FindProperty("initialParticles").intValue = 0;
            so.FindProperty("container").boundsValue = new Bounds(new Vector3(-0.25f, 1.5f, 0f), new Vector3(23.5f, 4f, 13.5f));
            so.FindProperty("settings.particleRadius").floatValue = ParticleRadius;
            so.FindProperty("settings.substeps").intValue = 4;
            so.FindProperty("settings.solverIterations").intValue = 3;
            so.FindProperty("settings.cohesion").floatValue = 0.1f;
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
            whitewaterSo.FindProperty("trappedAirRange").vector2Value = new Vector2(5f, 22f);
            whitewaterSo.FindProperty("trappedAirRate").floatValue = 300f;
            whitewaterSo.FindProperty("energyRange").vector2Value = new Vector2(0.6f, 6f);
            whitewaterSo.FindProperty("lifetimeRange").vector2Value = new Vector2(1f, 3f);
            whitewaterSo.ApplyModifiedPropertiesWithoutUndo();

            var surface = fluid.AddComponent<FluidSurface>();
            var surfaceSo = new SerializedObject(surface);
            surfaceSo.FindProperty("solver").objectReferenceValue = solver;
            surfaceSo.FindProperty("whitewater").objectReferenceValue = whitewater;
            surfaceSo.FindProperty("surfaceShader").objectReferenceValue = Shader.Find("HydroSandbox/FluidSurface");
            surfaceSo.FindProperty("rippleNormal").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/WaterRipples_Normal.png");
            surfaceSo.FindProperty("foamMask").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/Foam002_Mask.png");
            // Mountain water: clearer and greener than the tinted fountain pool.
            surfaceSo.FindProperty("absorption").vector3Value = new Vector3(1.1f, 0.35f, 0.45f);
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

            // Inflow: a rectangle across the channel floor, aimed down the channel.
            float sx = ReachStart;
            var source = new GameObject("Inflow").AddComponent<FluidSource>();
            source.transform.position = new Vector3(sx, BedHeight(sx) + 0.11f, CenterlineZ(sx));
            source.transform.rotation = Quaternion.LookRotation(new Vector3(1f, 0f, CenterlineSlope(sx)).normalized, Vector3.up);
            var sourceSo = new SerializedObject(source);
            sourceSo.FindProperty("shape").enumValueIndex = (int)FluidSourceShape.Rectangle;
            sourceSo.FindProperty("size").floatValue = 1.0f;
            sourceSo.FindProperty("rectangleHeight").floatValue = 0.16f;
            sourceSo.FindProperty("speed").floatValue = 1.0f;
            sourceSo.ApplyModifiedPropertiesWithoutUndo();

            // Outflow: everything that reaches the end of the reach is recycled.
            var sink = new GameObject("Outflow").AddComponent<FluidSink>();
            sink.transform.position = new Vector3(ReachEnd - 0.4f, BedHeight(ReachEnd) + 1f, CenterlineZ(ReachEnd));
            sink.transform.localScale = new Vector3(1.4f, 3f, 5f);
            return fluid;
        }

        // Starts the reach already flowing instead of waiting for the inflow to fill it.
        static void AddRiverFill(GameObject fluid, Terrain terrain)
        {
            var points = new System.Collections.Generic.List<Vector3>();
            for (float x = ReachStart; x <= ReachEnd - 0.8f; x += 0.5f)
                points.Add(new Vector3(x, BedHeight(x), CenterlineZ(x)));
            fluid.AddComponent<RiverFill>().Configure(terrain, points.ToArray(), 1.4f, 0.14f, 0.8f);
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
            respawn.position = new Vector3(rx, BedHeight(rx) + 0.45f, CenterlineZ(rx));
            respawn.rotation = Quaternion.LookRotation(new Vector3(0f, 0f, 1f));

            var lengths = new[] { 1.1f, 0.9f, 1.3f, 1.0f };
            var diameters = new[] { 0.18f, 0.15f, 0.22f, 0.17f };
            for (int i = 0; i < lengths.Length; i++)
            {
                float x = ReachStart + 2f + i * 1.6f;
                var go = new GameObject($"Log{i}");
                go.transform.SetParent(parent);
                go.transform.position = new Vector3(x, BedHeight(x) + 0.35f, CenterlineZ(x) + (i % 2 == 0 ? 0.12f : -0.12f));
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
                recyclerSo.FindProperty("region").boundsValue = new Bounds(new Vector3(-0.5f, 1.5f, 0f), new Vector3(22.5f, 6f, 13f));
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
