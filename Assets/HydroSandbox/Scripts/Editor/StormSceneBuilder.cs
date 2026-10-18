using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static HydroSandbox.Editor.SceneBuilderUtility;

namespace HydroSandbox.Editor
{
    /// <summary>
    /// Builds the storm scene: a small rocky island with a lighthouse in a gale at dusk. The open
    /// sea is an FFT heightfield out to the horizon; around the island it becomes particle water
    /// (a PBF zone driven at its edges by the same waves), so the swell shoals over the island's
    /// shelf and breaks on its rocks. Rain, lightning and the lighthouse's turning beam complete
    /// it. Clicking sends a tsunami at the lighthouse from a random side (OceanTsunami).
    /// Re-running rebuilds the scene and terrain from scratch.
    /// </summary>
    public static class StormSceneBuilder
    {
        const string Root = "Assets/HydroSandbox";
        const string ScenePath = Root + "/Scenes/StormLighthouse.unity";
        const string TerrainFolder = Root + "/Terrain";
        const string TextureFolder = Root + "/Textures";

        const float SeaLevel = 0f;
        const float TerrainSize = 128f;
        const float TerrainBottom = -18f;
        const float TerrainHeight = 30f;
        const int HeightmapResolution = 1025;
        const int AlphamapResolution = 512;

        // Particle zone around the island, widest on the windward (-x) side where the waves arrive.
        static readonly Rect Zone = Rect.MinMaxRect(-25f, -17.5f, 16f, 17.5f);
        const float ZoneBottom = -8f, ZoneTop = 14f;
        // Coarse for water, but a 41 x 35 m shelf a few meters deep is about 100k particles at this size.
        const float ParticleRadius = 0.17f;

        static readonly Vector3 LighthousePosition = new Vector3(0.8f, 0f, 0.6f);
        const float PlateauHeight = 5.2f;
        // Blowing toward +x, in OceanWaves' convention (degrees clockwise from +z).
        const float WindDirection = 90f;
        const float WindSpeed = 16f;

        static readonly Vector3 CameraTarget = new Vector3(-2f, 3.5f, 0f);
        const float CameraDistance = 50f, CameraYaw = 52f, CameraPitch = 11f;

        // ---- Landform -----------------------------------------------------------------------

        static float Fbm(float x, float z)
        {
            float sum = 0f, amplitude = 0.5f, frequency = 1f;
            for (int i = 0; i < 5; i++)
            {
                sum += amplitude * (Mathf.PerlinNoise(x * frequency + 71.3f, z * frequency + 19.9f) - 0.5f);
                amplitude *= 0.5f;
                frequency *= 2.07f;
            }
            return sum;
        }

        static float Ridged(float x, float z) => 1f - Mathf.Abs(2f * Mathf.PerlinNoise(x + 5.1f, z + 43.7f) - 1f);

        // Shoreline radius by bearing: an irregular island about 20 m across.
        static float ShoreRadius(float angle)
        {
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            return 10f + 1.6f * Mathf.Sin(2f * angle + 0.7f) + 1.1f * Mathf.Sin(3f * angle + 2.1f) + 3f * Fbm(c * 1.4f + 3f, s * 1.4f + 3f);
        }

        static float Height(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + z * z);
            float angle = Mathf.Atan2(z, x);
            float shore = ShoreRadius(angle);
            // The windward side faces the waves with low cliffs; the lee slopes gently.
            float windward = Mathf.Clamp01(-Mathf.Cos(angle) * 1.3f);
            float h;
            if (r <= shore)
            {
                float s = r / shore;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 1f, s));
                t = Mathf.Pow(t, Mathf.Lerp(1.2f, 0.4f, windward));
                float top = PlateauHeight + 0.9f * Fbm(x * 0.12f, z * 0.12f);
                h = Mathf.Lerp(top, -0.6f, t);
            }
            else
            {
                // A shelf the waves shoal over, then the drop to deep water beyond the zone.
                float d = r - shore;
                h = -0.6f - 3.4f * Mathf.SmoothStep(0f, 1f, d / 9f) - 12f * Mathf.SmoothStep(0f, 1f, (d - 10f) / 25f);
            }
            // Rocky outcrops along the shore and down the slopes.
            float rocky = Mathf.Exp(-Mathf.Pow((r - shore) / 4f, 2f));
            h += 1.1f * rocky * (Ridged(x * 0.32f, z * 0.32f) - 0.55f) + 0.35f * Fbm(x * 0.6f, z * 0.6f) * rocky;
            // A level pad for the lighthouse.
            float pad = Vector2.Distance(new Vector2(x, z), new Vector2(LighthousePosition.x, LighthousePosition.z));
            h = Mathf.Lerp(PlateauHeight, h, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 5.5f, pad)));
            return h;
        }

        // ---- Build --------------------------------------------------------------------------

        [MenuItem("HydroSandbox/Build Storm Lighthouse Scene")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ConfigureTextures();

            Light key = BuildEnvironment();
            Terrain terrain = BuildTerrain();
            BuildRocks();
            Transform lighthouse = BuildLighthouse();
            FluidSolver solver = BuildFluid(terrain);
            OceanWaves waves = BuildOcean(solver);
            BuildTsunami(solver, waves, lighthouse);
            BuildWeather(key);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuild(ScenePath);
        }

        static Light BuildEnvironment()
        {
            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(TextureFolder + "/Sky/kloppenheim_01_puresky_2k.hdr");
            Material skybox = LoadOrCreateMaterial("StormSky", "Skybox/Cubemap");
            skybox.SetTexture("_Tex", cubemap);
            // Dusk under storm cloud: the overcast sky darkened and cooled.
            skybox.SetFloat("_Exposure", 0.32f);
            skybox.SetColor("_Tint", new Color(0.42f, 0.46f, 0.52f));
            skybox.SetFloat("_Rotation", 0f);
            EditorUtility.SetDirty(skybox);
            RenderSettings.skybox = skybox;
            // Explicit ambient and the HDRI itself as the reflection, so nothing depends on a
            // lighting bake; the reflection is dimmed to match the darkened sky.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.16f, 0.18f, 0.21f);
            RenderSettings.ambientEquatorColor = new Color(0.12f, 0.135f, 0.15f);
            RenderSettings.ambientGroundColor = new Color(0.05f, 0.055f, 0.06f);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = cubemap;
            RenderSettings.reflectionIntensity = 0.3f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0065f;
            RenderSettings.fogColor = new Color(0.115f, 0.13f, 0.15f);

            var key = new GameObject("Dusk Light").AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 0.35f;
            key.color = new Color(0.78f, 0.74f, 0.78f);
            key.shadows = LightShadows.Soft;
            key.transform.rotation = Quaternion.Euler(14f, -70f, 0f);

            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.fieldOfView = 45f;
            camera.nearClipPlane = 0.2f;
            camera.farClipPlane = 3000f;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var orbit = camera.gameObject.AddComponent<OrbitCamera>();
            var orbitSo = new SerializedObject(orbit);
            orbitSo.FindProperty("distanceRange").vector2Value = new Vector2(4f, 160f);
            orbitSo.ApplyModifiedPropertiesWithoutUndo();
            orbit.Frame(CameraTarget, CameraDistance, CameraYaw, CameraPitch);

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/SampleSceneProfile.asset");
            if (profile != null)
            {
                var volume = new GameObject("Global Volume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
            }
            return key;
        }

        static Terrain BuildTerrain()
        {
            string dataPath = TerrainFolder + "/StormIslandTerrain.asset";
            AssetDatabase.DeleteAsset(dataPath);
            var data = new TerrainData
            {
                heightmapResolution = HeightmapResolution,
                alphamapResolution = AlphamapResolution,
            };
            data.size = new Vector3(TerrainSize, TerrainHeight, TerrainSize);
            // Saved first so the splat maps are kept as sub-assets (see RiverSceneBuilder).
            AssetDatabase.CreateAsset(data, dataPath);

            var origin = new Vector3(-TerrainSize * 0.5f, TerrainBottom, -TerrainSize * 0.5f);
            var heights = new float[HeightmapResolution, HeightmapResolution];
            for (int zi = 0; zi < HeightmapResolution; zi++)
            for (int xi = 0; xi < HeightmapResolution; xi++)
            {
                float x = origin.x + xi / (HeightmapResolution - 1f) * TerrainSize;
                float z = origin.z + zi / (HeightmapResolution - 1f) * TerrainSize;
                heights[zi, xi] = Mathf.Clamp01((Height(x, z) - TerrainBottom) / TerrainHeight);
            }
            data.SetHeights(0, 0, heights);

            data.terrainLayers = new[]
            {
                AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{TerrainFolder}/RiverPebbles.terrainlayer"),
                AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{TerrainFolder}/MossyRock.terrainlayer"),
                AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{TerrainFolder}/ForestGround.terrainlayer"),
                AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{TerrainFolder}/LeafyGrass.terrainlayer"),
            };
            PaintLayers(data, origin);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = "Island";
            go.transform.position = origin;
            var terrain = go.GetComponent<Terrain>();
            terrain.materialTemplate = LoadOrCreateMaterial("StormIslandTerrain", "Universal Render Pipeline/Terrain/Lit");
            terrain.heightmapPixelError = 4f;

            var collider = go.AddComponent<FluidTerrainCollider>();
            var so = new SerializedObject(collider);
            so.FindProperty("terrain").objectReferenceValue = terrain;
            so.FindProperty("friction").floatValue = 0f;
            so.ApplyModifiedPropertiesWithoutUndo();
            return terrain;
        }

        static void PaintLayers(TerrainData data, Vector3 origin)
        {
            var alpha = new float[AlphamapResolution, AlphamapResolution, 4];
            for (int zi = 0; zi < AlphamapResolution; zi++)
            for (int xi = 0; xi < AlphamapResolution; xi++)
            {
                float u = xi / (AlphamapResolution - 1f), v = zi / (AlphamapResolution - 1f);
                float x = origin.x + u * TerrainSize, z = origin.z + v * TerrainSize;
                float h = Height(x, z);
                float steepness = data.GetSteepness(u, v);
                float breakup = Fbm(x * 0.5f, z * 0.5f);
                float seabed = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.8f, 0.6f, h + breakup));
                float rock = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(12f, 26f, steepness + breakup * 10f)) * (1f - seabed);
                float grass = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.8f, 3.2f, h + breakup * 2f)) * (1f - rock) * (1f - seabed);
                float ground = Mathf.Max(0f, 1f - seabed - rock - grass);
                float sum = seabed + rock + grass + ground;
                alpha[zi, xi, 0] = seabed / sum;
                alpha[zi, xi, 1] = rock / sum;
                alpha[zi, xi, 2] = ground / sum;
                alpha[zi, xi, 3] = grass / sum;
            }
            data.SetAlphamaps(0, 0, alpha);
        }

        // Boulders along the windward shore for the waves to break on.
        static void BuildRocks()
        {
            var parent = new GameObject("Rocks").transform;
            Mesh boulder = ModelMesh("Boulder01/boulder_01.fbx", "boulder_01_LOD1");
            Mesh slab = ModelMesh("NamaqualandBoulder02/namaqualand_boulder_02.fbx", "namaqualand_boulder_02_LOD0");
            Material boulderMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{Root}/Materials/Boulder01.mat");
            Material slabMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{Root}/Materials/NamaqualandBoulder02.mat");

            // (bearing in degrees from +x, offset from the shoreline in meters, size, yaw, slab)
            var placements = new (float bearing, float offset, float size, float yaw, bool slab)[]
            {
                (140f, 0.5f, 3.2f, 20f, false),
                (155f, 2.2f, 2.4f, 100f, true),
                (168f, 0.2f, 3.6f, 200f, false),
                (180f, 1.8f, 2.8f, 60f, false),
                (192f, -0.3f, 3.0f, 140f, true),
                (205f, 1.2f, 2.6f, 300f, false),
                (218f, 0.4f, 3.4f, 10f, false),
                (232f, 2.5f, 2.2f, 250f, true),
                (120f, 1.5f, 2.0f, 80f, false),
                (250f, 0.8f, 2.4f, 170f, false),
                // A few on the lee shore, smaller.
                (20f, 0.8f, 1.8f, 40f, false),
                (330f, 0.5f, 2.0f, 210f, true),
            };
            int index = 0;
            foreach (var p in placements)
            {
                float a = p.bearing * Mathf.Deg2Rad;
                float r = ShoreRadius(a) + p.offset;
                float x = Mathf.Cos(a) * r, z = Mathf.Sin(a) * r;
                var bottom = new Vector3(x, Height(x, z) - p.size * 0.3f, z);
                PlaceRock(parent, $"Rock{index++}", p.slab ? slab : boulder, p.slab ? slabMaterial : boulderMaterial, bottom, p.size, p.yaw);
            }
        }

        static void PlaceRock(Transform parent, string name, Mesh mesh, Material material, Vector3 bottom, float size, float yaw)
        {
            Bounds b = mesh.bounds;
            float scale = size / Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            go.transform.position = bottom - go.transform.rotation * (new Vector3(b.center.x, b.min.y, b.center.z) * scale);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);

            int axis = b.size.x >= b.size.z ? 0 : 2;
            float radius = 0.5f * Mathf.Min(b.size.y, axis == 0 ? b.size.z : b.size.x) * 0.92f;
            float length = (axis == 0 ? b.size.x : b.size.z) * 0.92f;
            var fluidCollider = go.AddComponent<FluidCollider>();
            var so = new SerializedObject(fluidCollider);
            so.FindProperty("shape").enumValueIndex = (int)FluidColliderShape.Capsule;
            so.FindProperty("center").vector3Value = b.center;
            so.FindProperty("radius").floatValue = radius;
            so.FindProperty("height").floatValue = length;
            so.FindProperty("direction").intValue = axis;
            so.FindProperty("friction").floatValue = 0.1f;
            so.FindProperty("twoWayCoupling").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---- Lighthouse ---------------------------------------------------------------------

        static Transform BuildLighthouse()
        {
            var root = new GameObject("Lighthouse").transform;
            root.position = new Vector3(LighthousePosition.x, PlateauHeight, LighthousePosition.z);

            Material stone = LoadOrCreateMaterial("LighthouseStone", "Universal Render Pipeline/Lit");
            stone.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Stone/medieval_blocks_03_diff_2k.jpg"));
            stone.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Stone/medieval_blocks_03_nor_gl_2k.jpg"));
            stone.EnableKeyword("_NORMALMAP");
            stone.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(stone);
            Material white = Paint("LighthouseWhite", new Color(0.86f, 0.86f, 0.83f), 0.35f);
            Material red = Paint("LighthouseRed", new Color(0.55f, 0.07f, 0.06f), 0.4f);
            Material dark = Paint("LighthouseIron", new Color(0.07f, 0.075f, 0.08f), 0.45f);
            Material glass = LoadOrCreateMaterial("LighthouseLantern", "Universal Render Pipeline/Lit");
            glass.SetColor("_BaseColor", new Color(0.2f, 0.18f, 0.12f));
            glass.SetFloat("_Smoothness", 0.9f);
            glass.EnableKeyword("_EMISSION");
            glass.SetColor("_EmissionColor", new Color(1f, 0.78f, 0.45f) * 6f);
            glass.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(glass);

            const float towerHeight = 8.6f, baseRadius = 1.55f, topRadius = 1.05f;
            float RadiusAt(float y) => Mathf.Lerp(baseRadius, topRadius, y / towerHeight);

            LatheObject(root, "Plinth", stone, new[] { new Vector2(0f, -1.2f), new Vector2(2.3f, -1.2f), new Vector2(2.3f, 0.7f), new Vector2(0f, 0.7f) }, 2f);
            // Stripes: white, red, white, red, white up the taper.
            float[] bands = { 0.7f, 2.6f, 3.8f, 5.9f, 7.1f, towerHeight };
            for (int i = 0; i < bands.Length - 1; i++)
            {
                float y0 = bands[i], y1 = bands[i + 1];
                LatheObject(root, $"Tower{i}", i % 2 == 0 ? white : red,
                    new[] { new Vector2(RadiusAt(y0), y0), new Vector2(RadiusAt(y1), y1) }, 3f);
            }

            float deck = towerHeight;
            LatheObject(root, "Gallery", dark, new[]
            {
                new Vector2(0f, deck - 0.05f), new Vector2(1.95f, deck - 0.05f), new Vector2(1.95f, deck + 0.12f), new Vector2(0f, deck + 0.12f),
            }, 1f);
            // Railing: posts and a top rail around the gallery.
            const int posts = 20;
            for (int i = 0; i < posts; i++)
            {
                float a = i * Mathf.PI * 2f / posts;
                var post = Primitive(root, "Rail Post", PrimitiveType.Cylinder, dark,
                    new Vector3(Mathf.Cos(a) * 1.85f, deck + 0.6f, Mathf.Sin(a) * 1.85f), new Vector3(0.05f, 0.48f, 0.05f));
                post.transform.localRotation = Quaternion.identity;
            }
            LatheObject(root, "Top Rail", dark, new[]
            {
                new Vector2(1.8f, deck + 1.05f), new Vector2(1.9f, deck + 1.05f), new Vector2(1.9f, deck + 1.12f), new Vector2(1.8f, deck + 1.12f), new Vector2(1.8f, deck + 1.05f),
            }, 1f);

            float lanternBottom = deck + 0.12f, lanternTop = lanternBottom + 1.5f;
            LatheObject(root, "Lantern", glass, new[] { new Vector2(0.95f, lanternBottom), new Vector2(0.95f, lanternTop) }, 1f);
            for (int i = 0; i < 8; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / 8f;
                Primitive(root, "Mullion", PrimitiveType.Cube, dark,
                    new Vector3(Mathf.Cos(a) * 0.97f, (lanternBottom + lanternTop) * 0.5f, Mathf.Sin(a) * 0.97f), new Vector3(0.06f, lanternTop - lanternBottom, 0.06f))
                    .transform.localRotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);
            }
            LatheObject(root, "Roof", red, new[]
            {
                new Vector2(1.2f, lanternTop), new Vector2(1.1f, lanternTop + 0.25f), new Vector2(0.7f, lanternTop + 0.75f),
                new Vector2(0.25f, lanternTop + 1.05f), new Vector2(0.12f, lanternTop + 1.2f), new Vector2(0f, lanternTop + 1.25f),
            }, 1f);
            Primitive(root, "Finial", PrimitiveType.Sphere, dark, new Vector3(0f, lanternTop + 1.35f, 0f), Vector3.one * 0.22f);

            // Door and windows on the camera's side.
            Vector3 toCamera = Vector3.ProjectOnPlane(CameraPosition() - root.position, Vector3.up).normalized;
            float facing = Mathf.Atan2(toCamera.x, toCamera.z) * Mathf.Rad2Deg;
            var door = Primitive(root, "Door", PrimitiveType.Cube, dark, Vector3.zero, new Vector3(0.8f, 1.6f, 0.2f));
            door.transform.localPosition = Quaternion.Euler(0f, facing, 0f) * new Vector3(0f, 1.5f, RadiusAt(1.5f) - 0.05f);
            door.transform.localRotation = Quaternion.Euler(0f, facing, 0f);
            foreach (float y in new[] { 4.3f, 6.5f })
            {
                var window = Primitive(root, "Window", PrimitiveType.Cube, dark, Vector3.zero, new Vector3(0.35f, 0.55f, 0.2f));
                window.transform.localPosition = Quaternion.Euler(0f, facing + 25f, 0f) * new Vector3(0f, y, RadiusAt(y) - 0.04f);
                window.transform.localRotation = Quaternion.Euler(0f, facing + 25f, 0f);
            }

            // Spray hits the tower and its plinth.
            var towerCollider = root.gameObject.AddComponent<FluidCollider>();
            var so = new SerializedObject(towerCollider);
            so.FindProperty("shape").enumValueIndex = (int)FluidColliderShape.Capsule;
            so.FindProperty("center").vector3Value = new Vector3(0f, towerHeight * 0.5f, 0f);
            so.FindProperty("radius").floatValue = 1.5f;
            so.FindProperty("height").floatValue = towerHeight + 3f;
            so.FindProperty("direction").intValue = 1;
            so.FindProperty("friction").floatValue = 0.1f;
            so.FindProperty("twoWayCoupling").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();

            BuildLamp(root, (lanternBottom + lanternTop) * 0.5f);
            return root;
        }

        static void BuildLamp(Transform lighthouse, float height)
        {
            var glow = new GameObject("Lantern Glow").AddComponent<Light>();
            glow.transform.SetParent(lighthouse, false);
            glow.transform.localPosition = new Vector3(0f, height, 0f);
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.8f, 0.5f);
            glow.intensity = 6f;
            glow.range = 14f;
            glow.shadows = LightShadows.None;

            var optic = new GameObject("Optic").transform;
            optic.SetParent(lighthouse, false);
            optic.localPosition = new Vector3(0f, height, 0f);
            optic.gameObject.AddComponent<LighthouseLamp>();

            const float length = 90f, endRadius = 4.5f;
            Mesh cone = ConeMesh(length, 0.25f, endRadius, 32);
            Material beam = LoadOrCreateMaterial("LighthouseBeam", "HydroSandbox/LightBeam");
            beam.SetColor("_Color", new Color(1f, 0.82f, 0.55f) * 0.35f);
            beam.SetFloat("_Length", length);
            EditorUtility.SetDirty(beam);
            foreach (float yaw in new[] { 0f, 180f })
            {
                var arm = new GameObject($"Beam {yaw:0}").transform;
                arm.SetParent(optic, false);
                // Tilted a little down, so the beam sweeps the sea rather than the sky.
                arm.localRotation = Quaternion.Euler(3f, yaw, 0f);
                arm.gameObject.AddComponent<MeshFilter>().sharedMesh = cone;
                var renderer = arm.gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = beam;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                var spot = arm.gameObject.AddComponent<Light>();
                spot.type = LightType.Spot;
                spot.color = new Color(1f, 0.85f, 0.62f);
                spot.intensity = 60f;
                spot.range = 120f;
                spot.spotAngle = 9f;
                spot.innerSpotAngle = 4f;
                spot.shadows = LightShadows.None;
            }
        }

        static Material Paint(string name, Color color, float smoothness)
        {
            Material material = LoadOrCreateMaterial(name, "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        static GameObject Primitive(Transform parent, string name, PrimitiveType type, Material material, Vector3 localPosition, Vector3 localScale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        static void LatheObject(Transform parent, string name, Material material, Vector2[] profile, float textureMeters)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Lathe(name, profile, 48, textureMeters);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        // A surface of revolution about +y from a profile of (radius, height) points, with flat
        // normals across each profile segment so hard edges (the plinth top, the gallery) stay hard.
        static Mesh Lathe(string name, Vector2[] profile, int segments, float textureMeters)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            float circumference = 2f * Mathf.PI * profile.Max(p => p.x);
            float v = 0f;
            for (int s = 0; s < profile.Length - 1; s++)
            {
                Vector2 a = profile[s], b = profile[s + 1];
                Vector2 edge = b - a;
                if (edge.sqrMagnitude < 1e-8f)
                    continue;
                // Outward normal of this segment in the (r, y) plane.
                var n2 = new Vector2(edge.y, -edge.x).normalized;
                int start = vertices.Count;
                for (int i = 0; i <= segments; i++)
                {
                    float angle = i * Mathf.PI * 2f / segments;
                    float c = Mathf.Cos(angle), sn = Mathf.Sin(angle);
                    float u = i / (float)segments * circumference / textureMeters;
                    vertices.Add(new Vector3(c * a.x, a.y, sn * a.x));
                    vertices.Add(new Vector3(c * b.x, b.y, sn * b.x));
                    var normal = new Vector3(c * n2.x, n2.y, sn * n2.x);
                    normals.Add(normal);
                    normals.Add(normal);
                    uvs.Add(new Vector2(u, v / textureMeters));
                    uvs.Add(new Vector2(u, (v + edge.magnitude) / textureMeters));
                }
                v += edge.magnitude;
                for (int i = 0; i < segments; i++)
                {
                    int i0 = start + i * 2;
                    triangles.AddRange(new[] { i0, i0 + 1, i0 + 3, i0, i0 + 3, i0 + 2 });
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        // An open cone from the origin along +z, for the beam.
        static Mesh ConeMesh(float length, float startRadius, float endRadius, int segments)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            float slope = (endRadius - startRadius) / length;
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                vertices.Add(dir * startRadius);
                vertices.Add(dir * endRadius + Vector3.forward * length);
                Vector3 n = (dir - Vector3.forward * slope).normalized;
                normals.Add(n);
                normals.Add(n);
            }
            for (int i = 0; i < segments; i++)
            {
                int i0 = i * 2;
                triangles.AddRange(new[] { i0, i0 + 1, i0 + 3, i0, i0 + 3, i0 + 2 });
            }
            var mesh = new Mesh { name = "LighthouseBeam" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- Water --------------------------------------------------------------------------

        static FluidSolver BuildFluid(Terrain terrain)
        {
            var fluid = new GameObject("Surf Zone");
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

            int estimate = EstimateParticles(terrain);
            Debug.Log($"Storm surf zone: about {estimate:N0} particles at radius {ParticleRadius} m.");
            // Headroom for the water a tsunami brings in (OceanTsunami), 20-27k particles, but no
            // more than 2^17: the neighbor hash table is sized to twice the pool rounded up to a
            // power of two, and past that it doubles, which costs more than the particles do.
            // A full pool only stops the inflow early.
            so.FindProperty("capacity").intValue = Mathf.Max(131072, Mathf.CeilToInt(estimate * 1.08f / 1024f) * 1024);
            so.FindProperty("initialParticles").intValue = 0;
            so.FindProperty("container").boundsValue = new Bounds(
                new Vector3(Zone.center.x, (ZoneBottom + ZoneTop) * 0.5f, Zone.center.y),
                new Vector3(Zone.width, ZoneTop - ZoneBottom, Zone.height));
            so.FindProperty("settings.particleRadius").floatValue = ParticleRadius;
            so.FindProperty("settings.substeps").intValue = 3;
            so.FindProperty("settings.solverIterations").intValue = 3;
            // Surface tension is negligible at this scale.
            so.FindProperty("settings.cohesion").floatValue = 0.02f;
            so.FindProperty("settings.viscosity").floatValue = 0.01f;
            so.FindProperty("settings.vorticityConfinement").floatValue = 0.04f;
            so.ApplyModifiedPropertiesWithoutUndo();

            fluid.AddComponent<OceanFill>().Configure(terrain, SeaLevel);

            var whitewater = fluid.AddComponent<FluidWhitewater>();
            var whitewaterSo = new SerializedObject(whitewater);
            whitewaterSo.FindProperty("whitewaterShader").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/FluidWhitewater.compute");
            whitewaterSo.FindProperty("capacity").intValue = 131072;
            whitewaterSo.FindProperty("wind").vector3Value = new Vector3(WindSpeed * 0.6f, 0f, 0f);
            whitewaterSo.FindProperty("sprayDrag").floatValue = 1.2f;
            // Surf runs at 2-4 m/s, slower than the fall in the stream scene the defaults suit;
            // storm foam also lingers on the water in sheets.
            whitewaterSo.FindProperty("energyRange").vector2Value = new Vector2(1f, 6f);
            whitewaterSo.FindProperty("trappedAirRange").vector2Value = new Vector2(4f, 18f);
            whitewaterSo.FindProperty("waveCrestRange").vector2Value = new Vector2(0.3f, 2.5f);
            whitewaterSo.FindProperty("trappedAirRate").floatValue = 220f;
            whitewaterSo.FindProperty("lifetimeRange").vector2Value = new Vector2(2f, 5f);
            whitewaterSo.ApplyModifiedPropertiesWithoutUndo();

            var surface = fluid.AddComponent<FluidSurface>();
            var surfaceSo = new SerializedObject(surface);
            surfaceSo.FindProperty("solver").objectReferenceValue = solver;
            surfaceSo.FindProperty("whitewater").objectReferenceValue = whitewater;
            surfaceSo.FindProperty("surfaceShader").objectReferenceValue = Shader.Find("HydroSandbox/FluidSurface");
            surfaceSo.FindProperty("rippleNormal").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/WaterRipples_Normal.png");
            surfaceSo.FindProperty("foamMask").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/Foam002_Mask.png");
            // Storm water: churned and murky, so a couple of meters of it hide the bed as the
            // opaque heightfield sea does, and the zone does not read as a clear lagoon.
            surfaceSo.FindProperty("absorption").vector3Value = new Vector3(2.4f, 1.5f, 1.4f);
            surfaceSo.FindProperty("scatterColor").colorValue = new Color(0.05f, 0.17f, 0.16f);
            // The same ripple layers as the heightfield sea's detail normals (OceanSurface
            // material), so the two waters have the same texture; the chop between ripple and
            // swell comes from the FFT sea's small cascade (see FluidSurface.shader).
            surfaceSo.FindProperty("rippleScale").floatValue = 0.6f;
            surfaceSo.FindProperty("rippleStrength").floatValue = 0.5f;
            surfaceSo.FindProperty("rippleSpeed").floatValue = 0.03f;
            surfaceSo.FindProperty("blurIterations").intValue = 2;
            surfaceSo.FindProperty("blurRadius").floatValue = 8f;
            // Seen from tens of meters at a grazing angle, flat water changes depth by a meter
            // or more between neighboring blur taps; a tighter falloff leaves the particle rows
            // as terraces.
            surfaceSo.FindProperty("blurDepthFalloff").floatValue = 8f;
            surfaceSo.FindProperty("depthRadiusScale").floatValue = 1.5f;
            surfaceSo.FindProperty("smoothness").floatValue = 0.86f;
            surfaceSo.FindProperty("flowSpeedMin").floatValue = 2f;
            surfaceSo.FindProperty("flowSpeedRange").floatValue = 4f;
            surfaceSo.FindProperty("foamScale").floatValue = 0.4f;
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
            return solver;
        }

        static int EstimateParticles(Terrain terrain)
        {
            float spacing = ParticleRadius * 2f;
            int count = 0;
            for (float z = Zone.yMin + spacing * 0.5f; z < Zone.yMax; z += spacing)
            for (float x = Zone.xMin + spacing * 0.5f; x < Zone.xMax; x += spacing)
            {
                float bed = Mathf.Max(Height(x, z), ZoneBottom);
                if (bed < SeaLevel)
                    count += Mathf.CeilToInt((SeaLevel - bed - spacing * 0.5f) / spacing);
            }
            return count;
        }

        static OceanWaves BuildOcean(FluidSolver solver)
        {
            var waves = new GameObject("Ocean Waves").AddComponent<OceanWaves>();
            var so = new SerializedObject(waves);
            so.FindProperty("fftShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/OceanFFT.compute");
            so.FindProperty("solver").objectReferenceValue = solver;
            so.FindProperty("windSpeed").floatValue = WindSpeed;
            so.FindProperty("windDirection").floatValue = WindDirection;
            // A gale over a 30 km fetch: seas of about 37 m, short and steep enough to break on
            // the island's shelf (the open ocean's longer swell would only heave the zone).
            so.FindProperty("fetchKilometers").floatValue = 30f;
            so.FindProperty("heightScale").floatValue = 1.6f;
            so.FindProperty("choppiness").floatValue = 1.25f;
            so.FindProperty("foamBias").floatValue = 0.8f;
            so.FindProperty("foamGain").floatValue = 3f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var boundary = solver.gameObject.AddComponent<FluidOceanBoundary>();
            boundary.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/FluidOcean.compute"), waves, SeaLevel);
            var boundarySo = new SerializedObject(boundary);
            // The seas break over the shelf, so the zone's waves are lower than the open sea's.
            boundarySo.FindProperty("shelterScale").floatValue = 0.6f;
            boundarySo.FindProperty("shelterDistance").floatValue = 35f;
            boundarySo.FindProperty("fadeWidth").floatValue = 2.5f;
            boundarySo.ApplyModifiedPropertiesWithoutUndo();

            Material material = LoadOrCreateMaterial("OceanSurface", "HydroSandbox/Ocean");
            material.SetTexture("_FoamTexture", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/Foam002_Mask.png"));
            material.SetTexture("_DetailNormal", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/WaterRipples_Normal.png"));
            EditorUtility.SetDirty(material);
            var ocean = new GameObject("Ocean");
            ocean.AddComponent<MeshFilter>();
            ocean.AddComponent<MeshRenderer>().sharedMaterial = material;
            var surface = ocean.AddComponent<OceanSurface>();
            var surfaceSo = new SerializedObject(surface);
            surfaceSo.FindProperty("farDistance").floatValue = 3000f;
            surfaceSo.ApplyModifiedPropertiesWithoutUndo();
            return waves;
        }

        // A click sends a tsunami at the lighthouse (OceanTsunami).
        static void BuildTsunami(FluidSolver solver, OceanWaves waves, Transform lighthouse)
        {
            var root = new GameObject("Tsunami");
            var inflow = new GameObject("Tsunami Inflow").AddComponent<FluidSource>();
            inflow.transform.SetParent(root.transform);
            var inflowSo = new SerializedObject(inflow);
            inflowSo.FindProperty("shape").enumValueIndex = (int)FluidSourceShape.Rectangle;
            inflowSo.ApplyModifiedPropertiesWithoutUndo();
            inflow.enabled = false;

            // Strips along the zone's sides, from just above sea level up: the surplus the wave
            // brought in drains out over them, like water over a weir. They close by particle
            // count, so skimming the odd storm crest with it does not lower the sea.
            const float strip = 2.5f, bottom = SeaLevel + 0.05f, top = ZoneTop;
            float midY = (bottom + top) * 0.5f, height = top - bottom;
            var drains = new List<FluidSink>();
            foreach (var (name, center, size) in new[]
                     {
                         ("West", new Vector3(Zone.xMin + strip * 0.5f, midY, Zone.center.y), new Vector3(strip, height, Zone.height)),
                         ("East", new Vector3(Zone.xMax - strip * 0.5f, midY, Zone.center.y), new Vector3(strip, height, Zone.height)),
                         ("South", new Vector3(Zone.center.x, midY, Zone.yMin + strip * 0.5f), new Vector3(Zone.width, height, strip)),
                         ("North", new Vector3(Zone.center.x, midY, Zone.yMax - strip * 0.5f), new Vector3(Zone.width, height, strip)),
                     })
            {
                var drain = new GameObject($"Tsunami Drain {name}").AddComponent<FluidSink>();
                drain.transform.SetParent(root.transform);
                drain.transform.position = center;
                drain.transform.localScale = size;
                drain.enabled = false;
                drains.Add(drain);
            }

            var tsunami = root.AddComponent<OceanTsunami>();
            tsunami.Configure(solver, waves, lighthouse, inflow, drains.ToArray(), SeaLevel);
            EditorUtility.SetDirty(tsunami);
        }

        // ---- Weather ------------------------------------------------------------------------

        static void BuildWeather(Light key)
        {
            var flash = new GameObject("Lightning").AddComponent<Light>();
            flash.type = LightType.Directional;
            flash.color = new Color(0.78f, 0.84f, 1f);
            flash.intensity = 0f;
            flash.shadows = LightShadows.None;
            flash.gameObject.AddComponent<StormLightning>().Configure(flash);

            var rain = new GameObject("Rain");
            rain.AddComponent<FollowCamera>();
            var system = rain.AddComponent<ParticleSystem>();
            var main = system.main;
            main.loop = true;
            main.duration = 5f;
            main.startLifetime = 1.6f;
            main.startSpeed = 0f;
            main.startSize = 0.012f;
            main.startColor = new Color(0.75f, 0.8f, 0.86f, 0.22f);
            main.maxParticles = 8000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.prewarm = true;
            var emission = system.emission;
            emission.rateOverTime = 4000f;
            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(70f, 1f, 70f);
            // Driven sideways by the gale.
            var velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(WindSpeed * 0.45f, WindSpeed * 0.65f);
            velocity.y = new ParticleSystem.MinMaxCurve(-15f, -12f);
            velocity.z = new ParticleSystem.MinMaxCurve(-1f, 1f);
            var renderer = rain.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.035f;
            renderer.lengthScale = 1f;
            renderer.sharedMaterial = RainMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        static Material RainMaterial()
        {
            string path = $"{TextureFolder}/Water/RainStreak.png";
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) == null)
            {
                // A soft vertical streak: bright core, fading to the ends and sides.
                var texture = new Texture2D(16, 64, TextureFormat.RGBA32, false);
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 16; x++)
                {
                    float across = 1f - Mathf.Abs((x + 0.5f) / 8f - 1f);
                    float along = Mathf.Sin((y + 0.5f) / 64f * Mathf.PI);
                    float a = Mathf.Pow(across, 2f) * along;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path);
            }
            Material material = LoadOrCreateMaterial("Rain", "Universal Render Pipeline/Particles/Unlit");
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        // ---- Helpers ------------------------------------------------------------------------

        static Vector3 CameraPosition() => CameraTarget - Quaternion.Euler(CameraPitch, CameraYaw, 0f) * Vector3.forward * CameraDistance;

        static Mesh ModelMesh(string fbx, string meshName) =>
            AssetDatabase.LoadAllAssetsAtPath($"{Root}/Models/{fbx}").OfType<Mesh>().First(m => m.name == meshName);

        static void ConfigureTextures()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath($"{TextureFolder}/Sky/kloppenheim_01_puresky_2k.hdr");
            if (importer.textureShape != TextureImporterShape.TextureCube)
            {
                importer.textureShape = TextureImporterShape.TextureCube;
                importer.SaveAndReimport();
            }
        }
    }
}
