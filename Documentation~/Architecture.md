# Architecture

Internal reference for contributors. End-user docs live in
[index.md](index.md); this file describes how the library is structured
internally, including the WebGL adaptations and shader-registration
mechanism that the README only mentions in passing.

## Pipeline overview

The pipeline mirrors MapLibre GL JS's update / layout / render split:

```
User Input ─▶ MapInputHandler ─▶ MapState ─▶ MapLibreMap
                                                  │
                       ┌──────────────────────────┼──────────────────────────┐
                       ▼                          ▼                          ▼
                   MapCamera             TileGrid.GetVisibleTiles()      EventBus
                                                  │                  (styledata, sourcedata,
                                                  ▼                   click, hover, …)
                                            TileManager
                                          (cache + dispatch)
                                                  │
                ┌─────────────────────────────────┼─────────────────────────────────┐
                ▼                                 ▼                                 ▼
           Source impl                   Vector tile worker                    Renderers
   Raster / Vector / GeoJSON         (Task.Run: PBF → meshes,           Fill / Line / Symbol /
   PMTiles / MBTiles / Custom        applies expressions, filters,      FillExtrusion / Raster /
                                     style props per zoom)              Heatmap / Hillshade /
                                                                        Sky / Background / Custom
```

`Style` (parsed from `style.json`) is the source of truth for which
sources / layers exist, their order, and their visual properties. Edits
go through `MapLibreMap` (`AddLayer`, `SetPaintProperty`, …) which
re-evaluates expressions and rebuilds only the affected layers — without
re-fetching tile data when the underlying source bytes are still cached.

## Project structure

```
Packages/com.kazukikuriyama.maplibre-unity/
  Runtime/
    Core/         MapLibreMap, MapState, event bus, MapConstants
    Coordinates/  LngLat, MercatorCoordinate, CoordinateConversion
    Style/        StyleParser, StyleSpec, layer / source / sprite / glyph defs
    Tiles/        TileID, TileGrid, TileManager, TileState
    Source/       Raster / Vector / GeoJSON / RasterDem / Image sources
      PMTiles/    Pure-C# PMTiles archive reader + raster/vector source
      MBTiles/    SQLite-backed MBTiles wrapper (pluggable backend)
    VectorTile/   PBF parser, geometry decoder, feature / layer types
    Expression/   MapLibre style-spec expression evaluator + colour spaces
    Rendering/    Per-layer renderers + SDF glyph atlas / mesh builders
    Terrain/      DEM source, terrain mesh, hillshade integration
    Camera/       MapCamera, MapAnimator (easeTo / flyTo / fitBounds),
                  free-camera, MapInputHandler
    UI/           Navigation / Scale / Geolocate / Fullscreen / Attribution
    Pool/         ObjectPool, TileObjectPool
  Shaders/        URP shaders (raster, line, fill-extrusion, sdf-text,
                  heatmap, hillshade, sky)
  Editor/         Inspectors, Font Setup wizard
  Samples/        37 demo scenes plus a Home/ launcher — bundled with
                  the package and visible directly under Packages/ in
                  the Project window (read-only)
  Tests/          EditMode + PlayMode test assemblies
```

## Shader registration

Every layer's material is created at runtime via
`Shader.Find("MapLibre/...")`, which only resolves shaders that the build
pipeline kept around. URP strips unreferenced shaders from non-Editor
builds, so the package shaders have to live in **Project Settings →
Graphics → Always Included Shaders** to survive.

Maintaining that list by hand would be a permanent papercut. The
package handles it via an Editor-only hook
(`Editor/AlwaysIncludedShadersRegistration.cs`):

- `[InitializeOnLoad]` runs the hook on first Editor load after install.
- It scans `Packages/com.kazukikuriyama.maplibre-unity/Shaders/`.
- Each `MapLibre/*` shader missing from `GraphicsSettings.asset`'s
  `m_AlwaysIncludedShaders` list is appended.
- Existing entries are preserved and re-runs are no-ops.

If a registration ever needs to be re-run manually (for example after
a shader file was added while the Editor was closed, or to confirm the
list is in sync), invoke **MapLibreUnity → Register Always Included
Shaders** from the menu bar. The result is reported in the Console:
either `Registered N shader(s)` or `All N package shader(s) are
already registered`.

## WebGL implementation notes

WebGL Player builds run the same managed runtime as the rest of the
supported platforms, but several APIs behave differently. Adaptations
are guarded with `#if UNITY_WEBGL && !UNITY_EDITOR` so native builds
keep the simpler synchronous code paths.

### IndexedDB tile / glyph cache bridge

`Application.persistentDataPath` on WebGL is IDBFS-backed and every
`File.*` call blocks on IndexedDB sync. Loading 50 tiles × dozens of
synchronous writes would freeze the main thread waiting for IndexedDB
turns. The package routes `TileDiskCache` and `GlyphSource` through a
small JSLib bridge (`Plugins/WebGL/MapLibreCacheBridge.jslib`) instead:

- C# kicks off a request and gets back a slot id.
- The JS side runs the IndexedDB transaction asynchronously and flips
  the slot's state to hit / miss when it settles.
- The C# coroutine yields one or two frames per lookup, polling the
  slot until ready.
- Cached bytes, ETags, and `Cache-Control` metadata survive page
  reloads. Eviction is byte-budgeted by `lastAccess` order, matching
  the native `TileDiskCache.MaxBytes` semantics.

The browser's HTTP cache continues to layer on top of this for free —
an HTTP-level hit short-circuits the network entirely while the
IndexedDB bridge handles cross-session persistence and `If-None-Match`
revalidation.

### Background work

`BackgroundTask.Run` is a thin wrapper around `Task.Run`. On WebGL
Player it invokes the body inline (no real worker thread is available)
and returns a pre-completed task so the existing polling pattern exits
immediately. This keeps the call sites identical between platforms.

### Vector tile parse budget

`VectorTileSource` runs an incremental `ParseLazy`-based coroutine on
WebGL with a 4 ms per-frame budget so heavy planet tiles spread across
multiple frames instead of stalling the main loop. On native builds the
parser still runs on a worker thread via `Task.Run`.

### Font fallback

WebGL Player has no OS font enumeration, so UI Toolkit and Symbol
layers fall back to Unity's bundled `LegacyRuntime.ttf`. CJK / emoji
fonts must be wired through `MapLibreMap > Text > Symbol Font`
(TMP_FontAsset) for non-ASCII labels.

### Known limitation

Mesh-building still runs synchronously inline, so large tiles can
introduce a frame stall during initial load; pan / zoom remains smooth
between tile arrivals. WebGPU build target works the same way at
compile time but is currently untested.
