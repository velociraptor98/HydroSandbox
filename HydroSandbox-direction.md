# HydroSandbox: Project Direction

Handoff document for Claude Code. It records the decisions made during planning so work can continue without re-deriving them. Treat the **Decisions** section as settled unless the user says otherwise.

## 1. Goal

A realistic, interactive 3D water system in Unity: water that responds to real physical forces (gravity, collisions, pressure/incompressibility) and to user interaction (pushing, pouring, splashing, objects dropped in). Multiple water sources and sinks are first-class.

This is a **tech showcase and sandbox**, not a game. There is no gameplay layer, no monetization, and no backend.

## 2. Decisions (settled)

| Area | Decision |
|---|---|
| Project name | `HydroSandbox` |
| Engine | Unity 6 (latest LTS) |
| Render pipeline | URP (Universal 3D template) |
| Primary target | Desktop (developed on an Apple Silicon Mac, Metal) |
| Secondary target | A scaled-down mobile demo later (iOS/Android not decided yet; do not install mobile modules until asked) |
| Simulation method | **GPU particle-based Position-Based Fluids (PBF)** |
| Not chosen | Heightfield/shallow-water (can't do real splashes), cell-based Noita-style (blocky), Eulerian grid (visual effect only) |
| Rendering | Screen-space fluid rendering via a custom URP `ScriptableRendererFeature` using the **Render Graph** API |
| Source control | Git + Git LFS |
| Monetization / backend | None |
| Web | WebGL is **out of scope** (no compute shaders) |

Possible later upgrade: FLIP/PIC for large bodies of water with less energy dissipation. Not needed now.

## 3. Architecture

### 3.1 Particle data (GPU `GraphicsBuffer`s)
- Position, predicted position, velocity
- Per-particle solver values (density, lambda), delta-position accumulator
- Fluid type ID per particle (for per-source properties such as color, temperature, contamination)
- Fixed maximum particle count, pooled. Dead/inactive particles are recycled rather than reallocated.

### 3.2 Neighbor search
GPU spatial hash grid: compute the cell hash per particle, sort (bitonic or counting sort), then build cell start/end indices. This is the main performance-critical piece. Cell size should equal the smoothing radius.

### 3.3 Solver loop (per substep)
1. Apply gravity and external forces, then predict positions
2. Neighbor search
3. Iterate density constraints: compute lambda, then delta-position, then apply
4. Resolve collisions inside the iteration loop
5. Update velocities from position change
6. Apply XSPH viscosity and optional vorticity confinement

Tunables exposed in the inspector and a debug UI: substeps, solver iterations, particle radius, rest density, viscosity, gravity.

### 3.4 Collisions
- **Primitives** (box, sphere, capsule): a structured buffer of shapes tested directly in the compute shader.
- **Arbitrary meshes**: signed distance fields, baked per collider for static meshes and updated per frame for dynamic ones.
- **Terrain**: heightmap sampling.
- Collider velocities feed into the solver so moving colliders push the water.

### 3.5 Sources and sinks
- `FluidSource` component: flow rate, initial velocity, shape (point / line / area), fluid type ID, continuous or pulsed. Spawns by appending to a GPU spawn buffer.
- `FluidSink` component: a volume that recycles particles at a given rate.
- All sources and sinks are packed into one GPU buffer of records read by the compute shader each step, so adding more costs almost nothing.
- Cheap extras for later: rain over a region, springs, evaporation.

### 3.6 User interaction
- Grab/push radial force fields driven by mouse (and VR hands, if ever relevant).
- **Two-way rigid body coupling**: rigid bodies push the water through collider velocities, and the water pushes back through buoyancy and drag accumulated from particle contacts and read back asynchronously (`AsyncGPUReadback`).
- Tilting containers, pouring, and dropping objects in all work naturally because colliders are part of the solve.

### 3.7 Rendering
Screen-space fluid rendering:
1. Render particles as depth spheres
2. Bilateral-blur the depth
3. Reconstruct normals
4. Shade with refraction, Fresnel, thickness-based absorption, and foam (from velocity and curvature)

Implemented as a custom URP `ScriptableRendererFeature` on the Render Graph API. Marching cubes is a possible alternative for nicer silhouettes at higher cost; not planned.

## 4. Performance expectations
- **Desktop**: aim for roughly 100k to 500k particles in real time. That is a pool or fountain, not an ocean. Large open water could later combine this with a heightfield or FFT ocean around the interaction zone.
- **Mobile demo**: much lower particle counts (starting point: roughly 20k to 50k, to be measured), fewer substeps and iterations, cheaper rendering. Design quality settings so the same solver scales down through configuration rather than a fork.
- Realism depends heavily on rendering and tuning (substeps, iterations, particle radius vs. rest density). Budget time for both.

## 5. Build order

1. **Core solver**: particles falling into a box, spatial hash, basic density constraint, instanced-sphere debug rendering, tuning panel.
2. **Sources and sinks.**
3. **Colliders**: primitives first, then SDF meshes.
4. **Interaction**: push/grab forces and two-way rigid body coupling.
5. **Screen-space rendering and foam.**
6. **Optimization and debug UI**: particle count, solver time, per-pass GPU timings.
7. **Mobile demo scaling**: quality tiers, device testing.

Work one step at a time. Get each step stable and measured before starting the next.

## 6. Suggested layout

```
Assets/
  HydroSandbox/
    Scripts/
      Runtime/        FluidSolver, FluidSource, FluidSink, colliders, interaction
      Editor/         custom inspectors, SDF baking tools
    Shaders/
      Compute/        hash, sort, solver, collision, spawn
      Rendering/      depth/blur/shade shaders
    Rendering/        ScriptableRendererFeature + Render Graph passes
    Scenes/
    Settings/         quality tiers (desktop, mobile)
```

## 7. Conventions and tooling
- Use Unity's standard `.gitignore`. Never commit `Library/`, `Temp/`, `obj/`, or `Build/`. Commit every asset together with its `.meta` file.
- Git LFS for binary assets (`*.fbx`, `*.png`, `*.exr`, baked SDF `.asset` files, etc.).
- Add packages through the Package Manager (or the PackageManager Client API), never by hand-editing `manifest.json`.
- If a Unity Editor is open and the `unity` CLI is available, prefer driving the live Editor over hand-editing `.unity`, `.prefab`, or `.asset` files.
- Keep compute shader kernels small and separate (one kernel per solver stage) so each stage can be profiled and tested alone.
- Avoid per-frame CPU readbacks except the async coupling readback.

## 8. Platform notes
- Compute shaders are required. Metal (macOS/iOS), Vulkan/D3D11/D3D12 all work, WebGL does not.
- Building for iOS later requires macOS and Xcode. Android requires the Android build support modules.
- Check compute shader limits (thread group size, shared memory, buffer count) on the lowest-end mobile target before committing to a kernel design.

## 9. Open questions
- Which mobile platform(s) for the scaled-down demo (iOS, Android, or both)?
- Do any scenes need water interacting with large terrain, or is a container-scale scene enough for now?
- Is VR or hand tracking ever a target, or is mouse/keyboard interaction sufficient?

## 10. First task for Claude Code

Implement build-order step 1 in the new URP project:
- `FluidSolver` MonoBehaviour owning the `GraphicsBuffer`s and dispatching the compute kernels
- Compute shaders for gravity/prediction, spatial-hash neighbor search, and the density-constraint solve
- A box-container scene with an instanced-sphere debug renderer
- An inspector or on-screen panel for substeps, solver iterations, and particle radius

Verify it runs on Metal, report the particle count it sustains at 60 FPS, and commit before moving on to sources and sinks.
