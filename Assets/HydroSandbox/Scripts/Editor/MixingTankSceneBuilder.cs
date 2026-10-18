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
    /// Builds the miscibility demo: a glass tank on a workbench holding a honey, water and oil
    /// column, and four nozzles above it for blue ink, honey, oil and water; clicking a nozzle
    /// starts or stops its pour. Ink and water mix quickly, honey sinks and dissolves slowly, and
    /// oil is immiscible with everything and floats. Everything else is done with things on the
    /// bench: a paddle to stir with, a drill that punches holes in the glass (what leaks out runs
    /// over the bench and off its edge), sealant to plug them, and a tray of objects of different
    /// densities to drop in. A monitor shows each liquid's volume and how mixed it is. An
    /// overflow at the surface keeps the tank from filling up. Re-running rebuilds the scene.
    /// </summary>
    public static class MixingTankSceneBuilder
    {
        const string Root = "Assets/HydroSandbox";
        const string ScenePath = Root + "/Scenes/MixingTank.unity";
        const string TextureFolder = Root + "/Textures";

        // Tank interior, in meters; its glass floor sits on the bench top at y = 0.
        static readonly Vector3 TankSize = new Vector3(1.0f, 0.55f, 0.42f);
        const float Glass = 0.01f;
        const float NozzleHeight = 0.05f;
        const float OverflowLevel = 0.47f;
        const float FloorY = -0.78f;

        const float ParticleRadius = 0.008f;

        static readonly Vector3 CameraTarget = new Vector3(0f, 0.2f, 0.05f);
        const float CameraDistance = 1.9f, CameraYaw = -14f, CameraPitch = 11f;

        const int Water = 0, Ink = 1, Oil = 2, Honey = 3;

        [MenuItem("HydroSandbox/Build Mixing Tank Scene")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ConfigureTextures();

            BuildEnvironment();
            Material metal = MetalMaterial();
            BuildBench();
            FluidTank tank = BuildTank(metal);
            FluidLiquid[] liquids = Liquids();
            FluidSolver solver = BuildFluid(liquids, BuildNozzles(liquids, metal), tank);
            BuildTools(metal);
            BuildDropObjects(metal);
            BuildMonitor(solver, tank, metal);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuild(ScenePath);
        }

        // ---- Liquids -------------------------------------------------------------------------

        static FluidLiquid[] Liquids() => new[]
        {
            new FluidLiquid
            {
                name = "Water", swatch = new Color(0.55f, 0.75f, 0.95f),
                density = 1000f, viscosity = 0.01f,
                absorption = new Vector3(0.45f, 0.07f, 0.03f), scattering = new Vector3(0.01f, 0.015f, 0.02f),
            },
            // Dye absorbs strongly, so a single particle's worth reads as deep blue and the
            // plume pales as diffusion thins it.
            new FluidLiquid
            {
                name = "Blue ink", swatch = new Color(0.1f, 0.25f, 0.85f),
                density = 1030f, viscosity = 0.01f,
                absorption = new Vector3(70f, 32f, 3f), scattering = new Vector3(0.1f, 0.2f, 0.6f),
            },
            new FluidLiquid
            {
                name = "Oil", swatch = new Color(0.85f, 0.85f, 0.3f),
                density = 880f, viscosity = 0.2f, wallFriction = 0.05f,
                absorption = new Vector3(0.8f, 2.2f, 16f), scattering = new Vector3(0.06f, 0.05f, 0.01f),
            },
            new FluidLiquid
            {
                name = "Honey", swatch = new Color(0.75f, 0.35f, 0.05f),
                density = 1420f, viscosity = 0.85f, wallFriction = 0.6f,
                absorption = new Vector3(5f, 18f, 60f), scattering = new Vector3(0.3f, 0.1f, 0.02f),
            },
        };

        static FluidPairing[] Pairings() => new[]
        {
            new FluidPairing { a = Water, b = Ink, miscible = true, diffusion = 1.5f, tension = 0f },
            new FluidPairing { a = Water, b = Oil, miscible = false, tension = 25f },
            // Honey does dissolve, but over minutes: it should sink and pool first.
            new FluidPairing { a = Water, b = Honey, miscible = true, diffusion = 0.02f, tension = 12f },
            new FluidPairing { a = Ink, b = Oil, miscible = false, tension = 25f },
            new FluidPairing { a = Ink, b = Honey, miscible = true, diffusion = 0.02f, tension = 12f },
            new FluidPairing { a = Oil, b = Honey, miscible = false, tension = 25f },
        };

        // ---- Scene ---------------------------------------------------------------------------

        static void BuildEnvironment()
        {
            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(TextureFolder + "/Sky/photo_studio_loft_hall_2k.hdr");
            Material skybox = LoadOrCreateMaterial("MixingTankSky", "Skybox/Cubemap");
            skybox.SetTexture("_Tex", cubemap);
            skybox.SetFloat("_Exposure", 1f);
            EditorUtility.SetDirty(skybox);
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = cubemap;
            RenderSettings.fog = false;

            var key = new GameObject("Key Light").AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 1.2f;
            key.color = new Color(1f, 0.97f, 0.92f);
            key.shadows = LightShadows.Soft;
            key.transform.rotation = Quaternion.Euler(55f, -30f, 0f);

            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.fieldOfView = 40f;
            camera.nearClipPlane = 0.02f;
            camera.farClipPlane = 100f;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            camera.gameObject.AddComponent<OrbitCamera>().Frame(CameraTarget, CameraDistance, CameraYaw, CameraPitch);

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/SampleSceneProfile.asset");
            if (profile != null)
            {
                var volume = new GameObject("Global Volume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
            }
        }

        static void BuildBench()
        {
            var parent = new GameObject("Bench").transform;
            Material wood = LoadOrCreateMaterial("BenchWood", "Universal Render Pipeline/Lit");
            wood.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Wood/wood_table_001_diff_2k.jpg"));
            wood.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Wood/wood_table_001_nor_gl_2k.jpg"));
            wood.SetFloat("_Smoothness", 0.3f);
            wood.EnableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(wood);

            // A plain matte studio floor keeps attention on the tank.
            Material floor = LoadOrCreateMaterial("MixingTankFloor", "Universal Render Pipeline/Lit");
            floor.SetTexture("_BaseMap", null);
            floor.SetTexture("_BumpMap", null);
            floor.DisableKeyword("_NORMALMAP");
            floor.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.21f));
            floor.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(floor);

            const float top = 0.05f, height = 0.78f;
            // Liquid leaking from the tank runs over the top and drips past the legs.
            Solid(Box(parent, "Top", new Vector3(0f, -top * 0.5f, 0.1f), BenchTop(top), wood, 1.4f), BenchTop(top));
            var leg = new Vector3(0.07f, height - top, 0.07f);
            foreach (float x in new[] { -1.0f, 1.0f })
            foreach (float z in new[] { -0.36f, 0.56f })
                Solid(Box(parent, "Leg", new Vector3(x, -top - (height - top) * 0.5f, z), leg, wood, 1.4f), leg);
            Box(parent, "Floor", new Vector3(0f, -height - 0.02f, 0f), new Vector3(14f, 0.04f, 14f), floor, 1.5f);
        }

        static Vector3 BenchTop(float thickness) => new Vector3(2.2f, thickness, 1.1f);

        static void Solid(GameObject box, Vector3 size)
        {
            var collider = box.AddComponent<FluidCollider>();
            var so = new SerializedObject(collider);
            so.FindProperty("shape").enumValueIndex = (int)FluidColliderShape.Box;
            so.FindProperty("size").vector3Value = size;
            so.FindProperty("friction").floatValue = 0.02f;
            so.FindProperty("twoWayCoupling").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static FluidTank BuildTank(Material metal)
        {
            var parent = new GameObject("Tank").transform;
            Material glass = GlassMaterial();
            float x = TankSize.x * 0.5f, z = TankSize.z * 0.5f, h = TankSize.y, t = Glass;
            float midY = t + h * 0.5f;

            var tank = parent.gameObject.AddComponent<FluidTank>();
            tank.Configure(new Bounds(new Vector3(0f, midY, 0f), TankSize), t, GlassEdgeMaterial());
            EditorUtility.SetDirty(tank);

            foreach (GameObject pane in new[]
                     {
                         Box(parent, "GlassFloor", new Vector3(0f, t * 0.5f, 0f), new Vector3(TankSize.x + 2f * t, t, TankSize.z + 2f * t), glass, 1f),
                         Box(parent, "GlassFront", new Vector3(0f, midY, -z - t * 0.5f), new Vector3(TankSize.x + 2f * t, h, t), glass, 1f),
                         Box(parent, "GlassBack", new Vector3(0f, midY, z + t * 0.5f), new Vector3(TankSize.x + 2f * t, h, t), glass, 1f),
                         Box(parent, "GlassLeft", new Vector3(-x - t * 0.5f, midY, 0f), new Vector3(t, h, TankSize.z), glass, 1f),
                         Box(parent, "GlassRight", new Vector3(x + t * 0.5f, midY, 0f), new Vector3(t, h, TankSize.z), glass, 1f),
                     })
            {
                pane.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                GameObjectUtility.SetStaticEditorFlags(pane, 0);
                // For the drill's raycasts only; the water collides with the FluidTank.
                pane.AddComponent<BoxCollider>();
            }

            // Aluminium angle along every edge gives the glass box a readable outline.
            const float bar = 0.016f;
            float ox = x + t, oz = z + t, top = t + h;
            BuildScale(parent, -x + 0.04f, -oz - 0.001f);
            foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
                Box(parent, "Post", new Vector3(sx * ox, top * 0.5f, sz * oz), new Vector3(bar, top + bar, bar), metal, 1f);
            foreach (float y in new[] { bar * 0.5f, top })
            {
                foreach (float sz in new[] { -1f, 1f })
                    Box(parent, "RailX", new Vector3(0f, y, sz * oz), new Vector3(2f * ox + bar, bar, bar), metal, 1f);
                foreach (float sx in new[] { -1f, 1f })
                    Box(parent, "RailZ", new Vector3(sx * ox, y, 0f), new Vector3(bar, bar, 2f * oz + bar), metal, 1f);
            }
            return tank;
        }

        static FluidSource[] BuildNozzles(FluidLiquid[] liquids, Material metal)
        {
            var parent = new GameObject("Nozzles").transform;
            float barY = Glass + TankSize.y + NozzleHeight + 0.06f;
            Box(parent, "Gantry", new Vector3(0f, barY, 0f), new Vector3(TankSize.x + 0.3f, 0.03f, 0.04f), metal, 1f);
            foreach (float sx in new[] { -1f, 1f })
                Box(parent, "GantryPost", new Vector3(sx * (TankSize.x * 0.5f + 0.13f), barY * 0.5f, 0f), new Vector3(0.03f, barY, 0.03f), metal, 1f);

            // Left to right: ink, honey, oil, water. Honey and ink sit apart from oil so the
            // sinking honey and the ink plume are not hidden under the oil film. All start off;
            // clicking a nozzle toggles it.
            var order = new[] { (Ink, -0.33f), (Honey, -0.11f), (Oil, 0.11f), (Water, 0.33f) };
            var sources = new FluidSource[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                (int liquid, float x) = order[i];
                float y = Glass + TankSize.y + NozzleHeight;
                GameObject nozzle = Cylinder(parent, $"{liquids[liquid].name} Nozzle", new Vector3(x, y + 0.03f, 0f), 0.08f, 0.06f, metal);
                Material band = LoadOrCreateMaterial($"Nozzle{liquids[liquid].name.Replace(" ", "")}", "Universal Render Pipeline/Lit");
                band.SetColor("_BaseColor", liquids[liquid].swatch);
                band.SetFloat("_Smoothness", 0.5f);
                // PourNozzle drives the glow; URP strips _EMISSION from a material whose own
                // emission is black, so it keeps an imperceptible amount.
                band.EnableKeyword("_EMISSION");
                band.SetColor("_EmissionColor", liquids[liquid].swatch * 0.02f);
                band.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                EditorUtility.SetDirty(band);
                GameObject bandObject = Cylinder(parent, $"{liquids[liquid].name} Band", new Vector3(x, y + 0.045f, 0f), 0.084f, 0.016f, band);

                var source = new GameObject($"{liquids[liquid].name} Source").AddComponent<FluidSource>();
                source.transform.position = new Vector3(x, y, 0f);
                source.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var so = new SerializedObject(source);
                so.FindProperty("shape").enumValueIndex = (int)FluidSourceShape.Area;
                // Wide, fast and a short drop: a falling stream thins as it accelerates, and one
                // only two or three particles across breaks into drips.
                so.FindProperty("size").floatValue = liquid == Honey ? 0.07f : 0.06f;
                so.FindProperty("speed").floatValue = 0.8f;
                so.FindProperty("pulsed").boolValue = false;
                so.FindProperty("fluidType").uintValue = (uint)liquid;
                so.ApplyModifiedPropertiesWithoutUndo();
                source.enabled = false;
                sources[i] = source;

                // A click target a little larger than the nozzle itself.
                // A trigger, so nothing in the scene bumps into it.
                var click = nozzle.AddComponent<BoxCollider>();
                click.size = new Vector3(1.4f, 1.6f, 1.4f);
                click.isTrigger = true;
                var pour = nozzle.AddComponent<PourNozzle>();
                pour.Configure(source, bandObject.GetComponent<Renderer>(), liquids[liquid].swatch);
                EditorUtility.SetDirty(pour);
            }
            return sources;
        }

        static FluidSolver BuildFluid(FluidLiquid[] liquids, FluidSource[] sources, FluidTank tank)
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
            so.FindProperty("capacity").intValue = 90000;
            so.FindProperty("initialParticles").intValue = 0;
            // The room around the bench, so leaks can run off it to the floor; the FluidTank holds
            // the liquid in.
            so.FindProperty("container").boundsValue = new Bounds(
                new Vector3(0f, (FloorY + 1.2f) * 0.5f, 0.1f), new Vector3(3.2f, 1.2f - FloorY, 2.0f));
            so.FindProperty("settings.particleRadius").floatValue = ParticleRadius;
            // The tank is about 25 particles deep; at 3 iterations the density solve never quite
            // holds that column up and still water keeps churning (0.08 m/s at rest, 0.02 at 4).
            // 3 x 4 costs the same as 4 x 3.
            so.FindProperty("settings.substeps").intValue = 3;
            so.FindProperty("settings.solverIterations").intValue = 4;
            so.FindProperty("settings.cohesion").floatValue = 0.1f;
            so.FindProperty("settings.vorticityConfinement").floatValue = 0.04f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var mixing = fluid.AddComponent<FluidMixing>();
            mixing.Configure(liquids, Pairings());
            EditorUtility.SetDirty(mixing);

            // A density column, so the objects on the tray have three layers to settle between.
            fluid.AddComponent<LayeredFill>().Configure(new[]
            {
                new LayeredFill.Layer { liquid = Honey, thickness = 0.06f },
                // Not a whole number of particle layers, or the top water layer counts as oil.
                new LayeredFill.Layer { liquid = Water, thickness = 0.305f },
                new LayeredFill.Layer { liquid = Oil, thickness = 0.03f },
            }, tank);

            var coupling = fluid.AddComponent<FluidRigidbodyCoupling>();
            var couplingSo = new SerializedObject(coupling);
            couplingSo.FindProperty("couplingShader").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/FluidCoupling.compute");
            couplingSo.ApplyModifiedPropertiesWithoutUndo();

            var surface = fluid.AddComponent<FluidSurface>();
            var surfaceSo = new SerializedObject(surface);
            surfaceSo.FindProperty("solver").objectReferenceValue = solver;
            surfaceSo.FindProperty("surfaceShader").objectReferenceValue = Shader.Find("HydroSandbox/FluidSurface");
            surfaceSo.FindProperty("rippleNormal").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/Water/WaterRipples_Normal.png");
            surfaceSo.FindProperty("rippleStrength").floatValue = 0.12f;
            surfaceSo.FindProperty("flowSheen").floatValue = 0.15f;
            // Loose droplets would be drawn as white spray, which is wrong for ink or honey.
            surfaceSo.FindProperty("dropletMaxNeighbors").intValue = 0;
            surfaceSo.ApplyModifiedPropertiesWithoutUndo();

            var panel = fluid.AddComponent<MixingPanel>();
            var panelSo = new SerializedObject(panel);
            panelSo.FindProperty("solver").objectReferenceValue = solver;
            panelSo.FindProperty("mixing").objectReferenceValue = mixing;
            // A tuning panel, not part of play: F1 shows it.
            panelSo.FindProperty("visible").boolValue = false;
            var sourcesProperty = panelSo.FindProperty("sources");
            sourcesProperty.arraySize = sources.Length;
            for (int i = 0; i < sources.Length; i++)
                sourcesProperty.GetArrayElementAtIndex(i).objectReferenceValue = sources[i];
            panelSo.ApplyModifiedPropertiesWithoutUndo();

            var tools = fluid.AddComponent<TankTools>();
            tools.Configure(tank, solver, mixing, panel);
            EditorUtility.SetDirty(tools);

            // The overflow holds the level while the nozzles pour. It is a slot along the back
            // wall: across the whole tank it would also delete the streams falling through it.
            // There is no drain, or the tank would empty while nothing pours.
            float z = TankSize.z * 0.5f;
            Sink("Overflow", new Vector3(0f, Glass + OverflowLevel + 0.03f, z - 0.03f), new Vector3(TankSize.x, 0.06f, 0.06f), 0f);
            // Whatever reaches the floor is gone; a puddle there would only cost particles.
            Sink("Floor Drain", new Vector3(0f, FloorY + 0.02f, 0.1f), new Vector3(3.2f, 0.04f, 2.0f), 0f);
            return solver;
        }

        // ---- Bench items ---------------------------------------------------------------------

        // Graduations on the front glass, in liters of tank volume.
        static void BuildScale(Transform parent, float x, float z)
        {
            const float ppm = 2000f, width = 0.08f;
            float height = OverflowLevel + 0.02f;
            RectTransform canvas = WorldText.CreateCanvas(parent, "Scale", new Vector3(x + width * 0.5f, Glass + height * 0.5f, z),
                Quaternion.identity, new Vector2(width, height), ppm);
            float litersPerMeter = TankSize.x * TankSize.z * 1000f;
            var ink = new Color(1f, 1f, 1f, 0.75f);
            for (int liters = 10; liters / litersPerMeter <= OverflowLevel; liters += 10)
            {
                float y = (height - liters / litersPerMeter) * ppm;
                bool major = liters % 50 == 0;
                WorldText.AddPanel(canvas, "Tick", new Rect(0f, y - 1.5f, major ? 44f : 24f, 3f), ink);
                if (major)
                    WorldText.AddText(canvas, "Label", $"{liters} L", new Rect(50f, y - 20f, 120f, 40f), 30, ink, TextAnchor.MiddleLeft);
            }
        }

        static void BuildTools(Material metal)
        {
            var parent = new GameObject("Tools").transform;
            Material wood = AssetDatabase.LoadAssetAtPath<Material>($"{Root}/Materials/BenchWood.mat");
            Material dark = Plastic("ToolGrip", new Color(0.12f, 0.12f, 0.13f), 0.35f);
            // Satin: polished metal this thin shows the sky's reflection as stripes.
            Material steel = LoadOrCreateMaterial("PaddleSteel", "Universal Render Pipeline/Lit");
            steel.SetColor("_BaseColor", new Color(0.72f, 0.73f, 0.75f));
            steel.SetFloat("_Metallic", 0.8f);
            steel.SetFloat("_Smoothness", 0.4f);
            EditorUtility.SetDirty(steel);
            BuildPaddle(parent, steel, wood);
            BuildDrill(parent, metal, dark);
            BuildSealant(parent, metal);
        }

        static void BuildPaddle(Transform parent, Material metal, Material wood)
        {
            const float tipDepth = 0.31f;
            var blade = new Vector3(0.012f, 0.1f, 0.08f);
            // Stands with its blade in a slotted block.
            var home = new Vector3(-0.8f, tipDepth + 0.006f, -0.02f);
            Box(parent, "Paddle Block", new Vector3(home.x, 0.0125f, home.z), new Vector3(0.11f, 0.025f, 0.11f), wood, 1.4f);

            var paddle = new GameObject("Stir Paddle");
            paddle.transform.SetParent(parent);
            paddle.transform.SetPositionAndRotation(home, Quaternion.Euler(0f, CameraYaw, 0f));
            Transform t = paddle.transform;
            GameObject bladeObject = Box(t, "Blade", Vector3.zero, blade, metal, 1f);
            bladeObject.transform.localPosition = new Vector3(0f, -tipDepth + blade.y * 0.5f, 0f);
            bladeObject.transform.localRotation = Quaternion.identity;
            GameObjectUtility.SetStaticEditorFlags(bladeObject, 0);
            float shaftBottom = -tipDepth + blade.y - 0.01f, shaftTop = 0.31f;
            GameObject shaft = Part(t, "Shaft", PrimitiveType.Cylinder, new Vector3(0f, (shaftBottom + shaftTop) * 0.5f, 0f),
                Quaternion.identity, new Vector3(0.012f, (shaftTop - shaftBottom) * 0.5f, 0.012f), metal);
            // Too thin for the shadow map: it shadows itself in stripes.
            shaft.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            Part(t, "Grip", PrimitiveType.Cylinder, new Vector3(0f, 0.25f, 0f), Quaternion.identity, new Vector3(0.026f, 0.06f, 0.026f),
                Plastic("PaddleGrip", new Color(0.1f, 0.35f, 0.7f), 0.4f));

            // Solid only up to a little above the blade, so the hanging paddle cannot catch on
            // the nozzles; a trigger along its whole length is the click target.
            bladeObject.AddComponent<BoxCollider>().size = blade;
            var lower = shaft.AddComponent<CapsuleCollider>();
            lower.radius = 0.5f;
            lower.height = 0.24f / ((shaftTop - shaftBottom) * 0.5f);
            lower.center = new Vector3(0f, -1f + lower.height * 0.5f, 0f);
            var grab = paddle.AddComponent<CapsuleCollider>();
            grab.isTrigger = true;
            grab.radius = 0.035f;
            grab.height = 0.64f;
            AddFluidCollider(bladeObject, FluidColliderShape.Box, blade, 0f, 0f, 0.3f, false);
            AddFluidCollider(shaft, FluidColliderShape.Capsule, Vector3.zero, 0.5f * 0.008f / 0.006f, 2f, 0.3f, false);

            var body = paddle.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            var item = paddle.AddComponent<StirPaddle>();
            item.ConfigureItem("stir paddle", "Hold the left button to dip it in, move to stir\nRight-click to put it back");
            EditorUtility.SetDirty(item);
        }

        static void BuildDrill(Transform parent, Material metal, Material grip)
        {
            var drill = new GameObject("Glass Drill");
            drill.transform.SetParent(parent);
            Transform t = drill.transform;
            Material body = Plastic("DrillBody", new Color(0.95f, 0.55f, 0.08f), 0.45f);
            Box(t, "Body", new Vector3(0f, 0f, -0.02f), new Vector3(0.055f, 0.07f, 0.15f), body, 1f);
            Box(t, "Handle", new Vector3(0f, -0.085f, -0.06f), new Vector3(0.042f, 0.11f, 0.05f), grip, 1f);
            Box(t, "Battery", new Vector3(0f, -0.15f, -0.055f), new Vector3(0.06f, 0.03f, 0.09f), grip, 1f);
            Part(t, "Chuck", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.075f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.03f, 0.02f, 0.03f), metal);
            var bit = new GameObject("Bit").transform;
            bit.SetParent(t, false);
            bit.localPosition = new Vector3(0f, 0f, 0.095f);
            Part(bit, "Shank", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.0525f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.008f, 0.0525f, 0.008f), metal);
            foreach (Transform child in t)
                GameObjectUtility.SetStaticEditorFlags(child.gameObject, 0);

            var click = drill.AddComponent<BoxCollider>();
            click.isTrigger = true;
            click.center = new Vector3(0f, -0.05f, 0.02f);
            click.size = new Vector3(0.08f, 0.2f, 0.32f);
            // Lies on its side on the bench, bit toward the tank.
            t.SetPositionAndRotation(new Vector3(0.82f, 0.029f, -0.1f), Quaternion.Euler(0f, -100f, 90f));

            var item = drill.AddComponent<GlassDrill>();
            item.ConfigureItem("glass drill", "Click a side of the tank to drill a hole\nRight-click to put it back");
            item.Configure(bit, 0.2f, GlassEdgeMaterial());
            EditorUtility.SetDirty(item);
        }

        static void BuildSealant(Transform parent, Material metal)
        {
            var tube = new GameObject("Sealant");
            tube.transform.SetParent(parent);
            Transform t = tube.transform;
            Material white = Plastic("SealantTube", new Color(0.92f, 0.93f, 0.95f), 0.5f);
            Material label = Plastic("SealantLabel", new Color(0.1f, 0.55f, 0.3f), 0.3f);
            Part(t, "Tube", PrimitiveType.Cylinder, new Vector3(0f, 0f, -0.03f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.036f, 0.065f, 0.036f), white);
            Part(t, "Label", PrimitiveType.Cylinder, new Vector3(0f, 0f, -0.035f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.0375f, 0.03f, 0.0375f), label);
            Part(t, "Shoulder", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.04f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.024f, 0.006f, 0.024f), white);
            Part(t, "Nozzle", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.07f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.01f, 0.025f, 0.01f), white);
            Box(t, "Crimp", new Vector3(0f, 0f, -0.1f), new Vector3(0.04f, 0.006f, 0.012f), metal, 1f);
            foreach (Transform child in t)
                GameObjectUtility.SetStaticEditorFlags(child.gameObject, 0);

            var click = tube.AddComponent<BoxCollider>();
            click.isTrigger = true;
            click.center = new Vector3(0f, 0f, -0.01f);
            click.size = new Vector3(0.06f, 0.06f, 0.21f);
            t.SetPositionAndRotation(new Vector3(0.76f, 0.018f, -0.3f), Quaternion.Euler(0f, -80f, 0f));

            var item = tube.AddComponent<SealantTube>();
            item.ConfigureItem("glass sealant", "Click a hole to plug it\nRight-click to put it back");
            item.Configure(0.1f, GlassEdgeMaterial());
            EditorUtility.SetDirty(item);
        }

        // Densities straddle the liquids': oil 880, water 1000, honey 1420.
        static void BuildDropObjects(Material metal)
        {
            var parent = new GameObject("Drop Objects").transform;
            var tray = new Vector3(-0.8f, 0f, -0.24f);
            Material trayMaterial = Plastic("ObjectTray", new Color(0.18f, 0.19f, 0.2f), 0.3f);
            Box(parent, "Tray", tray + new Vector3(0f, 0.004f, 0f), new Vector3(0.4f, 0.008f, 0.14f), trayMaterial, 1f);
            foreach (float sz in new[] { -1f, 1f })
                Box(parent, "Tray Rim", tray + new Vector3(0f, 0.012f, sz * 0.068f), new Vector3(0.4f, 0.016f, 0.004f), trayMaterial, 1f);
            foreach (float sx in new[] { -1f, 1f })
                Box(parent, "Tray Rim", tray + new Vector3(sx * 0.198f, 0.012f, 0f), new Vector3(0.004f, 0.016f, 0.14f), trayMaterial, 1f);

            var objects = new (string name, float density, PrimitiveType primitive, Vector3 scale, Material material)[]
            {
                ("Cork", 240f, PrimitiveType.Cube, new Vector3(0.05f, 0.035f, 0.05f), Plastic("Cork", new Color(0.7f, 0.52f, 0.33f), 0.08f)),
                ("Ice cube", 917f, PrimitiveType.Cube, new Vector3(0.042f, 0.042f, 0.042f), Plastic("Ice", new Color(0.82f, 0.92f, 0.98f), 0.92f)),
                ("Egg", 1080f, PrimitiveType.Capsule, new Vector3(0.042f, 0.029f, 0.042f), Plastic("Eggshell", new Color(0.93f, 0.87f, 0.78f), 0.25f)),
                ("Steel ball", 7850f, PrimitiveType.Sphere, new Vector3(0.036f, 0.036f, 0.036f), metal),
            };

            const float ppm = 2000f;
            RectTransform placard = WorldText.CreateCanvas(parent, "Labels", tray + new Vector3(0f, 0.022f, -0.1f),
                Quaternion.Euler(35f, 0f, 0f), new Vector2(0.4f, 0.06f), ppm);
            for (int i = 0; i < objects.Length; i++)
            {
                var (name, density, primitive, scale, material) = objects[i];
                float x = tray.x - 0.15f + i * 0.1f;
                var go = GameObject.CreatePrimitive(primitive);
                go.name = name;
                go.transform.SetParent(parent);
                go.transform.localScale = scale;
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
                // The egg lies on its side.
                Quaternion rotation = primitive == PrimitiveType.Capsule ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.Euler(0f, 20f * i, 0f);
                float halfHeight = primitive == PrimitiveType.Capsule ? scale.x * 0.5f : scale.y * 0.5f;
                go.transform.SetPositionAndRotation(new Vector3(x, 0.008f + halfHeight + 0.001f, tray.z), rotation);

                float volume;
                float radius;
                switch (primitive)
                {
                    case PrimitiveType.Sphere:
                        radius = scale.x * 0.5f;
                        volume = 4f / 3f * Mathf.PI * radius * radius * radius;
                        AddFluidCollider(go, FluidColliderShape.Sphere, Vector3.zero, 0.5f, 0f, 0.3f, true);
                        break;
                    case PrimitiveType.Capsule:
                    {
                        float r = scale.x * 0.5f, h = scale.y * 2f;
                        radius = h * 0.5f;
                        volume = Mathf.PI * r * r * (h - 2f * r) + 4f / 3f * Mathf.PI * r * r * r;
                        AddFluidCollider(go, FluidColliderShape.Capsule, Vector3.zero, 0.5f, 2f, 0.3f, true);
                        break;
                    }
                    default:
                        radius = scale.magnitude * 0.5f;
                        volume = scale.x * scale.y * scale.z;
                        AddFluidCollider(go, FluidColliderShape.Box, Vector3.one, 0f, 0f, 0.3f, true);
                        break;
                }

                var body = go.AddComponent<Rigidbody>();
                body.linearDamping = 0.05f;
                body.angularDamping = 0.5f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                var item = go.AddComponent<DropObject>();
                item.ConfigureItem(name, $"{name} · {density:N0} kg/m³\nClick to drop it\nRight-click to put it back");
                item.Configure(density, volume, radius, -0.2f);
                EditorUtility.SetDirty(item);

                float labelX = (x - (tray.x - 0.2f)) * ppm;
                WorldText.AddText(placard, $"{name} Label", $"{name}\n{density:N0} kg/m³", new Rect(labelX - 100f, 0f, 200f, 120f), 32,
                    new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleCenter);
            }
        }

        static void BuildMonitor(FluidSolver solver, FluidTank tank, Material metal)
        {
            var screenSize = new Vector2(0.46f, 0.307f);
            var monitor = new GameObject("Monitor");
            var basePosition = new Vector3(0.9f, 0f, 0.3f);
            Vector3 cameraPosition = CameraTarget - Quaternion.Euler(CameraPitch, CameraYaw, 0f) * Vector3.forward * CameraDistance;
            Vector3 facing = Vector3.ProjectOnPlane(basePosition - cameraPosition, Vector3.up).normalized;
            monitor.transform.SetPositionAndRotation(basePosition, Quaternion.LookRotation(facing, Vector3.up));
            Transform t = monitor.transform;
            Material housing = Plastic("MonitorHousing", new Color(0.09f, 0.09f, 0.1f), 0.4f);
            GameObject foot = Box(t, "Foot", Vector3.zero, new Vector3(0.2f, 0.012f, 0.14f), metal, 1f);
            foot.transform.localPosition = new Vector3(0f, 0.006f, 0.02f);
            GameObject neck = Box(t, "Neck", Vector3.zero, new Vector3(0.03f, 0.22f, 0.02f), metal, 1f);
            neck.transform.localPosition = new Vector3(0f, 0.12f, 0.05f);
            const float centerY = 0.38f;
            var tilt = Quaternion.Euler(-8f, 0f, 0f);
            GameObject frame = Box(t, "Housing", Vector3.zero, new Vector3(screenSize.x + 0.03f, screenSize.y + 0.03f, 0.025f), housing, 1f);
            frame.transform.localPosition = new Vector3(0f, centerY, 0.03f);
            frame.transform.localRotation = tilt;
            foreach (Transform child in t)
                child.localRotation = child == frame.transform ? tilt : Quaternion.identity;

            var screen = new GameObject("Screen").transform;
            screen.SetParent(frame.transform, false);
            screen.localPosition = new Vector3(0f, 0f, -0.0135f);

            var meter = monitor.AddComponent<MixingMonitor>();
            meter.Configure(solver, tank, AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Root}/Shaders/Compute/FluidMixingMeter.compute"), screen, screenSize);
            EditorUtility.SetDirty(meter);
        }

        static GameObject Part(Transform parent, string name, PrimitiveType primitive, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Material material)
        {
            var go = GameObject.CreatePrimitive(primitive);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation;
            go.transform.localScale = localScale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        static void AddFluidCollider(GameObject go, FluidColliderShape shape, Vector3 size, float radius, float height, float friction, bool coupled)
        {
            var collider = go.AddComponent<FluidCollider>();
            var so = new SerializedObject(collider);
            so.FindProperty("shape").enumValueIndex = (int)shape;
            so.FindProperty("size").vector3Value = size;
            so.FindProperty("radius").floatValue = radius;
            so.FindProperty("height").floatValue = height;
            so.FindProperty("direction").intValue = 1;
            so.FindProperty("friction").floatValue = friction;
            so.FindProperty("twoWayCoupling").boolValue = coupled;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static Material Plastic(string name, Color color, float smoothness)
        {
            Material material = LoadOrCreateMaterial(name, "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);
            return material;
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

        static GameObject Cylinder(Transform parent, string name, Vector3 center, float diameter, float height, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        // ---- Assets -------------------------------------------------------------------------

        static Material GlassMaterial()
        {
            Material glass = LoadOrCreateMaterial("TankGlass", "Universal Render Pipeline/Lit");
            glass.SetFloat("_Surface", 1f);
            glass.SetFloat("_Blend", 0f);
            glass.SetFloat("_BlendModePreserveSpecular", 0f);
            glass.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            glass.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            glass.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            glass.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            glass.SetFloat("_ZWrite", 0f);
            glass.SetColor("_BaseColor", new Color(0.86f, 0.94f, 0.95f, 0.04f));
            glass.SetFloat("_Smoothness", 0.97f);
            glass.SetFloat("_Metallic", 0f);
            glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            glass.SetOverrideTag("RenderType", "Transparent");
            glass.renderQueue = (int)RenderQueue.Transparent;
            glass.SetShaderPassEnabled("DepthOnly", false);
            glass.SetShaderPassEnabled("ShadowCaster", false);
            EditorUtility.SetDirty(glass);
            return glass;
        }

        // Ground glass at a drilled hole's edge; double-sided so the bore shows from both faces.
        static Material GlassEdgeMaterial()
        {
            Material edge = LoadOrCreateMaterial("TankGlassEdge", "Universal Render Pipeline/Lit");
            edge.SetFloat("_Surface", 1f);
            edge.SetFloat("_Blend", 0f);
            edge.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            edge.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            edge.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            edge.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            edge.SetFloat("_ZWrite", 0f);
            edge.SetFloat("_Cull", (float)CullMode.Off);
            edge.SetColor("_BaseColor", new Color(0.88f, 0.96f, 0.95f, 0.6f));
            edge.SetFloat("_Smoothness", 0.35f);
            edge.SetFloat("_Metallic", 0f);
            edge.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            edge.SetOverrideTag("RenderType", "Transparent");
            edge.renderQueue = (int)RenderQueue.Transparent;
            edge.SetShaderPassEnabled("DepthOnly", false);
            edge.SetShaderPassEnabled("ShadowCaster", false);
            EditorUtility.SetDirty(edge);
            return edge;
        }

        static Material MetalMaterial()
        {
            Material metal = LoadOrCreateMaterial("BrushedAluminium", "Universal Render Pipeline/Lit");
            metal.SetColor("_BaseColor", new Color(0.78f, 0.79f, 0.8f));
            metal.SetFloat("_Metallic", 1f);
            metal.SetFloat("_Smoothness", 0.72f);
            EditorUtility.SetDirty(metal);
            return metal;
        }

        static void ConfigureTextures()
        {
            Configure($"{TextureFolder}/Sky/photo_studio_loft_hall_2k.hdr", i => i.textureShape = TextureImporterShape.TextureCube);
            Configure($"{TextureFolder}/Wood/wood_table_001_nor_gl_2k.jpg", i => i.textureType = TextureImporterType.NormalMap);
        }

        static void Configure(string path, System.Action<TextureImporter> apply)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var before = EditorJsonUtility.ToJson(importer);
            apply(importer);
            if (EditorJsonUtility.ToJson(importer) != before)
                importer.SaveAndReimport();
        }
    }
}
