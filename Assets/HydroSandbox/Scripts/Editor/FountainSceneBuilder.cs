using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace HydroSandbox.Editor
{
    /// <summary>
    /// Builds the wall-fountain showcase scene: a spout on a stone wall pouring into a basin, with a
    /// floor drain and an overflow lip. The basin interior is the solver's container, so the scene
    /// needs no mesh colliders. Re-running rebuilds the scene from scratch.
    /// </summary>
    public static class FountainSceneBuilder
    {
        const string Root = "Assets/HydroSandbox";
        const string ScenePath = Root + "/Scenes/WallFountain.unity";
        const string MaterialFolder = Root + "/Materials";
        const string TextureFolder = Root + "/Textures";

        // Basin interior, in meters. Water sits on y = 0.
        static readonly Vector3 BasinSize = new Vector3(1.8f, 0.45f, 0.9f);
        const float BasinWall = 0.12f;
        const float BasinFloorThickness = 0.1f;
        const float ContainerHeight = 1.4f;
        const float SpoutHeight = 1.0f;

        const float ParticleRadius = 0.01f;
        const float InitialWaterDepth = 0.3f;

        [MenuItem("HydroSandbox/Build Wall Fountain Scene")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Material wallMaterial = StoneMaterial("FountainWall", "medieval_blocks_03", 0.2f);
            Material basinMaterial = StoneMaterial("FountainBasin", "stone_tiles_02", 0.3f);
            Material groundMaterial = StoneMaterial("FountainGround", "cobblestone_floor_04", 0.15f);

            BuildEnvironment();
            BuildGeometry(wallMaterial, basinMaterial, groundMaterial);
            BuildFluid();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuild(ScenePath);
        }

        static void BuildEnvironment()
        {
            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(TextureFolder + "/Sky/symmetrical_garden_02_2k.hdr");
            Material skybox = LoadOrCreateMaterial("FountainSky", "Skybox/Cubemap");
            skybox.SetTexture("_Tex", cubemap);
            skybox.SetFloat("_Exposure", 1f);
            EditorUtility.SetDirty(skybox);

            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = cubemap;
            RenderSettings.fog = false;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.6f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.fieldOfView = 45f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 200f;
            camera.transform.position = new Vector3(0.55f, 1.55f, -1.9f);
            camera.transform.LookAt(new Vector3(0f, 0.45f, 0f));
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/SampleSceneProfile.asset");
            if (profile != null)
            {
                var volume = new GameObject("Global Volume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
            }
        }

        static void BuildGeometry(Material wall, Material basin, Material ground)
        {
            var parent = new GameObject("Fountain").transform;
            float x = BasinSize.x * 0.5f;
            float z = BasinSize.z * 0.5f;
            float h = BasinSize.y;
            float t = BasinWall;

            // Ground outside the basin, with its top flush with the basin floor.
            Box(parent, "Ground", new Vector3(0f, -0.25f, -2f), new Vector3(12f, 0.5f, 8f), ground, 1.5f);
            Box(parent, "BasinFloor", new Vector3(0f, -BasinFloorThickness * 0.5f + 0.001f, -t * 0.5f),
                new Vector3(BasinSize.x + 2f * t, BasinFloorThickness, BasinSize.z + t), basin, 0.8f);
            Box(parent, "BasinFront", new Vector3(0f, h * 0.5f, -z - t * 0.5f), new Vector3(BasinSize.x + 2f * t, h, t), basin, 0.8f);
            Box(parent, "BasinLeft", new Vector3(-x - t * 0.5f, h * 0.5f, 0f), new Vector3(t, h, BasinSize.z), basin, 0.8f);
            Box(parent, "BasinRight", new Vector3(x + t * 0.5f, h * 0.5f, 0f), new Vector3(t, h, BasinSize.z), basin, 0.8f);

            // Rim caps overhang slightly, like dressed coping stones.
            float cap = 0.06f;
            float capWidth = t + 0.06f;
            Box(parent, "RimFront", new Vector3(0f, h + cap * 0.5f, -z - t * 0.5f), new Vector3(BasinSize.x + 2f * capWidth, cap, capWidth), basin, 0.8f);
            Box(parent, "RimLeft", new Vector3(-x - t * 0.5f, h + cap * 0.5f, 0.0f), new Vector3(capWidth, cap, BasinSize.z), basin, 0.8f);
            Box(parent, "RimRight", new Vector3(x + t * 0.5f, h + cap * 0.5f, 0.0f), new Vector3(capWidth, cap, BasinSize.z), basin, 0.8f);

            Box(parent, "BackWall", new Vector3(0f, 1.2f, z + 0.15f), new Vector3(3.6f, 2.6f, 0.3f), wall, 1.2f);
            Box(parent, "SpoutPlaque", new Vector3(0f, SpoutHeight + 0.05f, z - 0.02f), new Vector3(0.36f, 0.42f, 0.05f), basin, 0.8f);

            var spout = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            spout.name = "Spout";
            Object.DestroyImmediate(spout.GetComponent<Collider>());
            spout.transform.SetParent(parent);
            spout.transform.position = new Vector3(0f, SpoutHeight, z - 0.1f);
            spout.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            spout.transform.localScale = new Vector3(0.09f, 0.08f, 0.09f);
            spout.GetComponent<MeshRenderer>().sharedMaterial = basin;
        }

        static void BuildFluid()
        {
            float z = BasinSize.z * 0.5f;
            var fluid = new GameObject("Fluid");

            var solver = fluid.AddComponent<FluidSolver>();
            var so = new SerializedObject(solver);
            SetObject(so, "predictShader", "Shaders/Compute/FluidPredict.compute");
            SetObject(so, "neighborSearchShader", "Shaders/Compute/FluidNeighborSearch.compute");
            SetObject(so, "neighborListShader", "Shaders/Compute/FluidNeighborList.compute");
            SetObject(so, "densityConstraintShader", "Shaders/Compute/FluidDensityConstraint.compute");
            SetObject(so, "collisionShader", "Shaders/Compute/FluidCollision.compute");
            SetObject(so, "velocityShader", "Shaders/Compute/FluidVelocity.compute");
            SetObject(so, "emissionShader", "Shaders/Compute/FluidEmission.compute");

            float spacing = ParticleRadius * 2f;
            int perLayer = (Mathf.FloorToInt((BasinSize.x - spacing) / spacing) + 1) *
                           (Mathf.FloorToInt((BasinSize.z - spacing) / spacing) + 1);
            int initial = perLayer * Mathf.RoundToInt(InitialWaterDepth / spacing);
            so.FindProperty("initialParticles").intValue = initial;
            so.FindProperty("capacity").intValue = initial + 40000;
            so.FindProperty("container").boundsValue = new Bounds(
                new Vector3(0f, ContainerHeight * 0.5f, 0f), new Vector3(BasinSize.x, ContainerHeight, BasinSize.z));
            so.FindProperty("spawnMin").vector3Value = Vector3.zero;
            so.FindProperty("spawnMax").vector3Value = Vector3.one;
            so.FindProperty("settings.particleRadius").floatValue = ParticleRadius;
            so.FindProperty("settings.substeps").intValue = 4;
            so.FindProperty("settings.solverIterations").intValue = 3;
            so.FindProperty("settings.cohesion").floatValue = 0.3f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var whitewater = fluid.AddComponent<FluidWhitewater>();
            var whitewaterSo = new SerializedObject(whitewater);
            SetObject(whitewaterSo, "whitewaterShader", "Shaders/Compute/FluidWhitewater.compute");
            whitewaterSo.ApplyModifiedPropertiesWithoutUndo();

            var surface = fluid.AddComponent<FluidSurface>();
            var surfaceSo = new SerializedObject(surface);
            surfaceSo.FindProperty("solver").objectReferenceValue = solver;
            surfaceSo.FindProperty("whitewater").objectReferenceValue = whitewater;
            surfaceSo.FindProperty("surfaceShader").objectReferenceValue = Shader.Find("HydroSandbox/FluidSurface");
            SetObject(surfaceSo, "rippleNormal", "Textures/Water/WaterRipples_Normal.png");
            SetObject(surfaceSo, "foamMask", "Textures/Water/Foam002_Mask.png");
            surfaceSo.ApplyModifiedPropertiesWithoutUndo();

            var debugRenderer = fluid.AddComponent<FluidDebugRenderer>();
            var debugSo = new SerializedObject(debugRenderer);
            debugSo.FindProperty("solver").objectReferenceValue = solver;
            debugSo.FindProperty("particleShader").objectReferenceValue = Shader.Find("HydroSandbox/FluidDebugParticle");
            debugSo.ApplyModifiedPropertiesWithoutUndo();
            debugRenderer.enabled = false;

            var panel = fluid.AddComponent<FluidDebugPanel>();
            var panelSo = new SerializedObject(panel);
            panelSo.FindProperty("solver").objectReferenceValue = solver;
            panelSo.FindProperty("visible").boolValue = false;
            panelSo.FindProperty("sphereRenderer").objectReferenceValue = debugRenderer;
            panelSo.FindProperty("waterSurface").objectReferenceValue = surface;
            panelSo.FindProperty("viewMode").enumValueIndex = (int)FluidViewMode.Water;
            panelSo.ApplyModifiedPropertiesWithoutUndo();

            // Pitched 10 degrees down, pointing out of the wall (-Z).
            var source = new GameObject("SpoutSource").AddComponent<FluidSource>();
            source.transform.position = new Vector3(0f, SpoutHeight, z - 0.19f);
            source.transform.rotation = Quaternion.Euler(10f, 180f, 0f);
            var sourceSo = new SerializedObject(source);
            sourceSo.FindProperty("shape").enumValueIndex = (int)FluidSourceShape.Area;
            sourceSo.FindProperty("size").floatValue = 0.07f;
            sourceSo.FindProperty("speed").floatValue = 1.2f;
            sourceSo.ApplyModifiedPropertiesWithoutUndo();

            // The drain takes slightly less than the spout delivers; the overflow lip along the
            // front edge removes the rest, which pins the water level just below the rim.
            Sink("FloorDrain", new Vector3(-0.6f, 0.03f, -0.25f), new Vector3(0.12f, 0.06f, 0.12f), 4f);
            Sink("OverflowLip", new Vector3(0f, BasinSize.y - 0.03f, -z + 0.06f),
                new Vector3(BasinSize.x, 0.1f, 0.12f), 0f);
        }

        static void Sink(string name, Vector3 position, Vector3 size, float drainRate)
        {
            var sink = new GameObject(name).AddComponent<FluidSink>();
            sink.transform.position = position;
            sink.transform.localScale = size;
            var so = new SerializedObject(sink);
            so.FindProperty("drainRate").floatValue = drainRate;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetObject(SerializedObject so, string field, string assetPath)
        {
            so.FindProperty(field).objectReferenceValue = AssetDatabase.LoadAssetAtPath<Object>($"{Root}/{assetPath}");
        }

        static Material StoneMaterial(string name, string textureId, float smoothness)
        {
            Material material = LoadOrCreateMaterial(name, "Universal Render Pipeline/Lit");
            string stone = $"{TextureFolder}/Stone/{textureId}";
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{stone}_diff_2k.jpg"));
            material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{stone}_nor_gl_2k.jpg"));
            material.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{stone}_ao_2k.jpg"));
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_BumpScale", 1f);
            material.EnableKeyword("_NORMALMAP");
            material.EnableKeyword("_OCCLUSIONMAP");
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material LoadOrCreateMaterial(string name, string shaderName)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder(Root, "Materials");
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find(shaderName));
                AssetDatabase.CreateAsset(material, path);
            }
            return material;
        }

        /// <summary>
        /// A box whose UVs are in world meters divided by <paramref name="textureMeters"/>, so stone
        /// keeps a consistent scale however the box is sized (Unity's cube stretches 0..1 per face).
        /// </summary>
        static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material material, float textureMeters)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.AddComponent<MeshFilter>().sharedMesh = BoxMesh(name, size, textureMeters);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        static Mesh BoxMesh(string name, Vector3 size, float textureMeters)
        {
            Vector3 e = size * 0.5f;
            var vertices = new System.Collections.Generic.List<Vector3>();
            var normals = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var triangles = new System.Collections.Generic.List<int>();

            void Face(Vector3 normal, Vector3 u, Vector3 v, float uSize, float vSize)
            {
                int start = vertices.Count;
                Vector3 c = Vector3.Scale(normal, e);
                Vector3 du = u * (uSize * 0.5f);
                Vector3 dv = v * (vSize * 0.5f);
                vertices.Add(c - du - dv);
                vertices.Add(c + du - dv);
                vertices.Add(c + du + dv);
                vertices.Add(c - du + dv);
                for (int i = 0; i < 4; i++)
                    normals.Add(normal);
                float su = uSize / textureMeters;
                float sv = vSize / textureMeters;
                uvs.Add(new Vector2(0, 0));
                uvs.Add(new Vector2(su, 0));
                uvs.Add(new Vector2(su, sv));
                uvs.Add(new Vector2(0, sv));
                triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            Face(Vector3.forward, Vector3.left, Vector3.up, size.x, size.y);
            Face(Vector3.back, Vector3.right, Vector3.up, size.x, size.y);
            Face(Vector3.right, Vector3.forward, Vector3.up, size.z, size.y);
            Face(Vector3.left, Vector3.back, Vector3.up, size.z, size.y);
            Face(Vector3.up, Vector3.right, Vector3.forward, size.x, size.z);
            Face(Vector3.down, Vector3.right, Vector3.back, size.x, size.z);

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddSceneToBuild(string path)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s.path == path))
                return;
            scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
