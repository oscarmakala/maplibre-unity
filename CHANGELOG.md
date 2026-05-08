# Changelog

All notable changes to MapLibre Unity are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
intends to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
once 1.0 is cut. The history here is curated from commit messages — see
`git log` for the full record.

## [Unreleased]

## [0.4.6] - 2026-05-09

### Fixed
- **WebGL Player: 三角形チェッカー柄でラスタタイルが部分的にグレー
  (background 色) 抜けする問題**。`BackgroundRenderer` が background
  plane を `y = -0.01f` に置いていたが、WebGL の vertex shader は
  `mediump` (16-bit float, ~11 bit mantissa) で頂点を扱う実装があり、
  カメラから離れた絶対座標では y=0 (raster tile) と y=-0.01 (background)
  の差が clip-space depth で 1 ULP 未満に潰れる。結果、`ZTest LEqual`
  が三角形ごとに pass/fail を切り替え、fail した三角形だけ描画されず
  下の background `#e0e0e0` が透けてチェッカー柄になっていた。
  `BackgroundRenderer.cs` で background plane の Y 位置を
  `-0.01f` → `-1f` に変更し、mediump でも確実に区別できる差を確保。
  カメラ移動でパターンが変化していたのも、絶対座標の変化で量子化境界
  がずれていたため (修正後は解消)。デスクトップ Player は `highp`
  (32-bit float) で頂点を扱うため元から再現せず、挙動への影響なし。

## [0.4.5] - 2026-05-09

### Reverted
- **Reverts the v0.4.4 WebGL raster tile "fix"** which actually broke
  raster tile rendering completely on WebGL Player. The change combined
  `Texture2D.LoadImage(rawBytes, markNonReadable: true)` -- which frees
  the CPU-side pixel buffer once the GPU upload completes -- with a
  follow-up explicit `texture.Apply(updateMipmaps: false,
  makeNoLongerReadable: true)`. After `markNonReadable: true` the CPU
  buffer is gone, so the explicit `Apply` re-uploaded an effectively
  empty buffer to the GPU, leaving every raster tile blank. This
  release restores the v0.4.3 `LoadImage(rawBytes)` call.

  The original "occasional gray / black raster tiles on WebGL"
  symptom is therefore unaddressed and remains a known issue. Future
  fix attempts must avoid the markNonReadable + Apply combination.

## [0.4.4] - 2026-05-08

### Fixed
- **WebGL Player: occasional gray / black raster tiles**. When several
  tiles arrived in the same frame, the WebGL2 `texImage2D` call queued
  by `Texture2D.LoadImage` could lag behind the renderer's bind, so the
  shader sampled the still-empty texture object and showed the default
  `_MainTex` value (gray on the raster shader, black on the icon
  shader). `RasterTileSource` now forces an explicit
  `texture.Apply(updateMipmaps: false, makeNoLongerReadable: true)` on
  WebGL Player builds after `LoadImage` so the upload is committed
  before the texture enters the cache. Non-WebGL platforms keep the
  prior behaviour. Also passes `markNonReadable: true` to `LoadImage`
  on every platform to free the CPU-side pixel copy once the GPU has
  the data, reducing overall memory pressure (especially relevant on
  WebGL's ~2 GB wasm heap).

## [0.4.3] - 2026-05-08

### Changed
- **WebGL build now uses a custom fullscreen template** so the Unity
  canvas fills the browser viewport instead of being shown inside
  Unity's bundled "Default" template (Unity logo, footer, fixed
  960x600 canvas). New template lives at
  `Assets/WebGLTemplates/Fullscreen/index.html`. Body is set to
  `width:100%, height:100%, overflow:hidden`, the canvas is pinned
  to `100% / 100%`, and a small loading overlay covers the few
  seconds before the first scene paints. `webGLTemplate` Player
  Setting was switched from `APPLICATION:Default` to
  `PROJECT:Fullscreen`.

## [0.4.2] - 2026-05-08

### Fixed
- **GitHub Pages site failed to start with `Unable to parse Build/*.framework.js.br`**.
  The WebGL build was emitting Brotli-compressed `.br` artefacts
  (`webGLCompressionFormat: 0` is Brotli in Unity 6, not Disabled as
  the prior CHANGELOG mistakenly assumed), and GitHub Pages does not
  attach a `Content-Encoding: br` response header to those files, so
  the browser tried to parse raw Brotli bytes as JS and aborted on
  startup. Enabled `webGLDecompressionFallback: 1` so Unity bundles a
  client-side Brotli decoder into the loader. The build keeps the
  smaller download size (~2-3x smaller than uncompressed) without
  requiring server-side header configuration.

## [0.4.1] - 2026-05-08

### Fixed
- **GitHub Pages 404 on every release**. `actions/upload-pages-artifact`
  was uploading `build/WebGL` as the artifact root, but unity-builder
  writes its actual HTML output one level deeper at
  `build/WebGL/maplibre-unity/` (the directory is named after `buildName`).
  Pages serves whatever sits at the artifact root, so the previous
  configuration left the deployed site without an `index.html` at `/`
  and returned `404: For root URLs you must provide an index.html file`.
  Fixed by pointing the upload step at `build/WebGL/maplibre-unity`.
  v0.4.0 deployed but is unreachable; v0.4.1 is the first release that
  actually serves the sample browser at <https://kazukikuriyama.github.io/maplibre-unity/>.

## [0.4.0] - 2026-05-08

### Changed
- **GitHub Release notes are now the canonical install URL source.** Tag
  pushes auto-create a Release whose body embeds the exact UPM Git URL
  (`?path=...#vX.Y.Z`) and `Packages/manifest.json` snippet alongside the
  CHANGELOG excerpt and Pages demo link. README's Installation section
  was simplified to link to
  [releases/latest](https://github.com/KazukiKuriyama/maplibre-unity/releases/latest)
  so the version-specific URL no longer needs manual maintenance per
  tag.

### Internal
- `release.yml` regained a `release` job (gated on `startsWith(github.ref,
  'refs/tags/')`) that runs after `deploy-pages`. The CHANGELOG section
  for the tag is extracted via `awk` and concatenated with the install
  blocks before being published via `softprops/action-gh-release`.
  `workflow_dispatch` previews still skip Release creation -- they only
  redeploy Pages.

## [0.3.0] - 2026-05-08

### Added
- **WebGL Player support (experimental).** Raster basemaps, vector tile
  layers (Fill / Line / Circle / FillExtrusion / Heatmap), and Symbol /
  SDF text all render in WebGL builds verified on Chrome / Firefox /
  Safari. Notes:
  - Disk caches (`TileDiskCache`, `GlyphSource`) are no-ops on WebGL —
    `Application.persistentDataPath` is IDBFS-backed and synchronous
    `File.*` calls block on IndexedDB sync. The browser's HTTP cache
    covers the same ground.
  - `Task.Run` callsites flow through a new internal `BackgroundTask.Run`
    helper that forwards to the real `Task.Run` on threaded platforms and
    runs the body inline on WebGL Player (returning a pre-completed
    task so the existing polling pattern exits immediately).
  - `VectorTileParser.ParseLazy(byte[])` exposes the parser as
    `IEnumerable<VectorTileLayer>` for incremental consumption.
    `VectorTileSource` drives this through a coroutine with a 4 ms
    per-frame budget on WebGL Player so heavy tiles (~1-3 sec parse on
    planet z9) spread across multiple frames instead of freezing the
    main thread. Existing eager `Parse(byte[])` is unchanged on threaded
    platforms.
  - WebGL has no OS font enumeration: a new `SystemFontFallback` helper
    swaps `Font.CreateDynamicFontFromOSFont` for the bundled
    `LegacyRuntime.ttf` on WebGL Player. CJK / emoji rendering still
    requires assigning a TMP_FontAsset under `MapLibreMap > Text >
    Symbol Font`. README's "Supported platforms" was updated to mark
    WebGL as experimental rather than unsupported.
  - Heavy mesh-building inside renderers still runs synchronously inline
    on WebGL, so large tiles can introduce a frame stall on initial load.
    Pan / zoom between tile arrivals remains smooth.

## [0.2.1] - 2026-05-08

### Changed
- **Vector tile parse timeouts are now logged as warnings** instead of errors.
  Slow background parses that exceed the 90s budget are transient (high load
  / slow tiles) and don't indicate a real fault, so the Unity console no
  longer shows them as red errors. The `MapEventType.Error` event still
  fires so user code that subscribes to it keeps hearing about the failure.

### Internal
- `MapLibreMap.FireError` grew an `isTransient: bool` parameter that switches
  the Unity console log level between `LogWarning` and `LogError` while
  keeping the fired `MapEvent` shape unchanged. Vector tile load callbacks
  now flag timeouts via this parameter.
- Sample player builds: `Application.version` is now sourced from the tag at
  CI time. `ProjectSettings.bundleVersion` stays at the placeholder
  `0.0.0-dev` in-repo, and the release workflow `sed`-injects the tag
  version (`${GITHUB_REF_NAME#v}`) into PlayerSettings before unity-builder
  runs. Editor / dev builds keep showing `0.0.0-dev` so they don't pretend
  to be a real release.
- Test workflow: `EditMode` and `PlayMode` now run sequentially on a single
  runner (was: matrix with two parallel runners). Avoids occupying two
  Unity license seats simultaneously and shares the `Library/` cache. Job
  name is `Tests`; per-mode results are still surfaced as separate
  `EditMode Test Results` / `PlayMode Test Results` check annotations.

## [0.2.0] - 2026-05-08

### Changed
- **Initial view priority now matches MapLibre GL JS** (Inspector >
  style JSON > 0). Previously the order was reversed (style JSON >
  Inspector), which conflicted with the upstream `new Map({zoom: 10,
  style: ...})` convention where constructor options override style
  defaults. The Inspector fields (`Initial Longitude` / `Latitude` /
  `Zoom` / `Bearing` / `Pitch`) now serialize as `NaN` by default,
  meaning "use the style JSON value." When set to a non-NaN value
  they override the style. The `MapLibreMap` Inspector grew explicit
  **Override** / **Use style** buttons and shows the effective
  `style.<field>` value as a hint when no override is active. As part
  of the same fix, `bearing` and `pitch` from the style JSON are now
  applied (previously the runtime ignored `Style.Bearing` /
  `Style.Pitch` entirely). All bundled sample scenes were updated so
  the five `_initial*` fields are `.nan` — their initial view comes
  from the corresponding `*Style.json`.
- **WebGL marked as unsupported in README.** The previous claim that the
  package "works on every Unity build target including WebGL" was wrong:
  vector tile parsing / mesh building rely on `Task.Run` + `lock`, and
  tile / glyph caches use synchronous `System.IO.File`, neither of which
  works under the default WebGL single-threaded runtime. A "Supported
  platforms" subsection was added under Requirements; PMTiles / MBTiles
  / sample-table mentions of WebGL were removed accordingly. WebGL
  support may be revisited later behind `#if UNITY_WEBGL` guards.
- **Package source moved to `Packages/com.kazukikuriyama.maplibre-unity/`** (was
  `Assets/MapLibreUnity/`). The repository now follows the canonical
  Unity embedded-package layout — Unity loads the package
  automatically when this repo is opened in Unity Hub. UPM install URL
  is now
  `https://github.com/KazukiKuriyama/maplibre-unity.git?path=Packages/com.kazukikuriyama.maplibre-unity#<tag>`.
- **Sample scenes ship bundled with the package.** Located at
  `Packages/com.kazukikuriyama.maplibre-unity/Samples/` (no trailing `~`),
  they appear directly in the Project window — no Package Manager
  "Import" step is needed and the path no longer carries a `<version>`
  segment. The `samples` array was removed from `package.json`. End
  users open `Packages/MapLibre Unity/Samples/Home/HomeScene.unity`
  to launch the demo browser. Samples under `Packages/` are read-only;
  copy a demo into `Assets/` to modify it.
- **`MapLibre > Font Setup` default save path** changed from
  `Assets/MapLibreUnity/Fonts` to `Assets/MapLibre/Fonts` so the
  generated `TMP_FontAsset` lands in the user's project rather than
  inside the (read-only) UPM package.

### Internal
- `Tests/EditMode/EnsureSamplesImported.cs` no longer triggers a UPM
  sample import (the samples ship in-package now). It still registers
  every sample scene in `EditorBuildSettings` on Editor startup, and
  additionally prunes any stale `Assets/Samples/MapLibre Unity/<version>/...`
  entries left over from the previous opt-in flow. The script is
  excluded from the UPM tarball (`Tests/` is stripped by the release
  workflow), so it never runs in end-user projects.

## [0.1.0] - 2026-04-28

First public release.

### Breaking
- **Camera animation `Duration` is now in milliseconds** (was seconds). Affects
  `EaseToOptions.Duration`, `FlyToOptions.Duration`, `FitBoundsOptions.Duration`,
  and the optional `duration` parameter on `MapLibreMap.ZoomTo` / `PanBy` /
  `PanTo` / `ZoomIn` / `ZoomOut` / `RotateTo` / `ResetNorth` / `SnapToNorth`.
  Defaults moved from `0.3f` / `0.5f` to `300f` / `500f`. Matches MapLibre GL JS
  `easeTo({duration: 300})` exactly. Callers passing literal seconds (e.g.
  `Duration = 1.5f`) need to multiply by 1000 (`Duration = 1500f`).
- **Source / loader public APIs migrated from `IEnumerator` (coroutine) to
  `Awaitable` (async).** The library no longer exposes any IEnumerator-typed
  methods on its public surface. Specifically:
  - `ImageSource.LoadFromUrl()` → `LoadFromUrlAsync()` (`Awaitable`)
  - `GeoJsonSource.LoadData()` → `LoadDataAsync()` (`Awaitable`)
  - `SpriteLoader.Load(url, onSuccess, onError)` →
    `SpriteLoader.LoadAsync(url)` returning `Awaitable<SpriteAtlas>`; throws
    `SpriteLoadException` on fetch / parse failure.
  - `TileJSONFetcher.Fetch(url, onComplete, onError)` →
    `TileJSONFetcher.FetchAsync(url)` returning `Awaitable<TileJSONData>`;
    throws on HTTP / parse failure.
  - `PMTilesArchive.Open(onReady, onError)` → `OpenAsync()` (`Awaitable`);
    throws `PMTilesException` on failure.
  - `PMTilesArchive.LoadTileAsync(z, x, y, onSuccess, onMissing, onError)` →
    `LoadTileAsync(z, x, y)` returning `Awaitable<byte[]>` (`null` on
    sparse-archive miss; throws on I/O / decode failure).
  - `PMTilesRasterSource` / `PMTilesVectorSource` constructors now accept an
    optional `MonoBehaviour coroutineHost` (defaults to `null`); the archive
    no longer needs a coroutine host since it runs entirely on `Awaitable`.
  Sample code in `PMTilesDemo.cs` was updated accordingly.

### Added
- **Awaitable camera / lifecycle / asset API** under `MapLibreMap.*Async`:
  `LoadAsync`, `OnceAsync`, `EaseToAsync`, `FlyToAsync`, `ZoomToAsync`,
  `PanToAsync`, `RotateToAsync`, `FitBoundsAsync`, `SetStyleAsync`,
  `SetStyleJsonAsync`, `LoadImageAsync`. Each returns Unity 6's
  `Awaitable` / `Awaitable<T>` so callers can `await` directly. All methods
  accept an optional `CancellationToken` that throws
  `OperationCanceledException` when signalled. Existing fire-and-forget
  methods (`EaseTo`, `SetStyle`, `LoadImage`, …) are kept unchanged.
- **Custom layers** via the new `ICustomLayer` interface. User-supplied code
  can render into the map's render order alongside built-in layers (mirrors
  MapLibre GL JS `type: "custom"`). Includes `OnAdd` / `Render` / `OnRemove`
  lifecycle and an optional `Prerender` hook for off-screen passes.
- **Custom sources** via `ICustomRasterSource` and `ICustomVectorSource`.
  Serve tiles from any backend — procedural generation, local files, custom
  HTTP, IPC, render-to-texture, etc. Vector PBF bytes flow through the
  existing parser pipeline so all expression / filter / paint features work
  unchanged.
- **`feature-state` runtime API**: `SetFeatureState` / `GetFeatureState` /
  `RemoveFeatureState` / `ClearFeatureState`, with the corresponding
  `["feature-state", key]` expression usable in both paint and filter. State
  is read on the renderer's background mesh-build threads via a
  `ConcurrentDictionary`-backed store, and only the layers actually
  referencing feature-state are rebuilt — using cached tile data, no network
  refetch.
- **`interpolate-hcl` / `interpolate-lab`** color interpolation. Color
  gradients now run in CIELAB / CIELCH (D65) so transitions don't dip
  through grey. `interpolate-hcl` uses shortest-arc hue interpolation.
- **Per-feature pattern** for `fill-pattern` / `line-pattern` /
  `fill-extrusion-pattern`. Features within one layer are bucketed by their
  resolved pattern name and rendered as separate meshes, so
  `["match", ["get", "class"], "park", "trees", "water", "waves", "default"]`
  now produces the expected visual.
- **Glyph URL / SDF text rendering**. When a style declares `glyphs`, the
  library fetches glyph PBFs on demand, packs them into a runtime SDF
  atlas, and renders labels via the new `MapLibre/SdfText` URP shader —
  no `TMP_FontAsset` configuration needed. SymbolRenderer keeps the
  TextMeshPro path as a fall-back when no glyph URL is configured.
- **SDF line placement** (`symbol-placement: "line"`) for the glyph-URL
  path. Curved labels along roads / rivers now bake one quad per glyph
  through the SDF atlas via the new `SdfLineTextMeshBuilder`, dropping
  the TMP `ForceMeshUpdate` cost and keeping styling consistent with
  point-placement SDF labels. Falls back to the existing TextMeshPro
  warp pipeline when no glyph URL is configured.
- **DocFX documentation scaffold** (`Documentation~/docfx.json`,
  `index.md`, `toc.yml`, `filterConfig.yml`). Running `docfx
  Documentation~/docfx.json --serve` after a `dotnet tool install -g
  docfx` produces an API-reference site at `localhost:8080` from the
  Runtime / Editor XML doc-comments. Build artefacts (`_site/`, `api/`)
  are gitignored apart from a seed `api/index.md`.
- **`FullscreenControl`** + improved `GeolocateControl` (state colours for
  idle / waiting / active / error, plus optional `trackUserLocation`
  continuous recenter).
- **`PrerenderCustomLayers`** is invoked once per frame for every visible
  custom layer before the main `Render` pass — same two-phase contract as
  MapLibre GL JS `prerender` / `render`.
- **PlayMode smoke test suite** that loads every sample scene from
  EditorBuildSettings and asserts no exceptions or asserts during boot.
  Adds a new `MapLibre.Unity.Tests.PlayMode` assembly.
- **Sample scenes**: `FeatureStateHoverDemo`, `CustomLayerDemo`,
  `CustomSourceDemo`, `GlyphUrlDemo`. All registered in HomeScene.
- **Sample scenes (additional coverage)**: `CameraAnimationDemo`
  (EaseTo / FlyTo / ZoomTo / RotateTo / PanBy / JumpTo side-by-side),
  `SkyAndLightDemo` (time-of-day slider driving `SetSky` / `SetLight`),
  `FreeCameraDemo` (orbit rig driving `SetFreeCameraOptions`), and
  `PMTilesDemo` (HTTP Range PMTiles archive streamed at runtime). All
  four are registered in HomeScene and EditorBuildSettings.

### Changed
- `SetFeatureState` skips the global `RefreshTiles` when no layer reads
  feature-state from the mutated source. When at least one layer is
  affected, only that layer is rebuilt — and from cached tile data.
- `feature-state` storage is now a 3-level `ConcurrentDictionary` so the
  background mesh-build threads can read state safely while the main
  thread mutates it.
- Filter expressions now resolve `feature-state` correctly for every
  built-in renderer (`Vector` / `Circle` / `Heatmap` / `FillExtrusion` /
  `Symbol`). Previously the `EvaluationContext` for filter passes was
  built without the source identity, so `["==", ["feature-state", "x"],
  true]` always returned false.

## Notable additions before this changelog began

These were committed before CHANGELOG.md existed but are worth surfacing
because they shape the public API:

- `setSky` / `getSky` — sky gradient at high pitch.
- `setLight` / `getLight` — directional light for fill-extrusion shading.
- `SetTerrain` runtime API — toggle / swap DEM source at runtime.
- `GetFreeCameraOptions` / `SetFreeCameraOptions` — drive the camera from
  Cinemachine or another external rig.
- `ScaleControl` — bottom-left scale bar (m / km / ft / mi).
- `BoxZoom` (Shift + left drag) with a visual selection rectangle.
- HTTP disk cache (`Cache-Control` / `ETag` aware).
- GeoJSON clustering (`cluster` / `clusterRadius` / `clusterMaxZoom` /
  `clusterMinPoints`) and cluster query helpers
  (`GetClusterExpansionZoom` / `GetClusterChildren` / `GetClusterLeaves`).
- Raster parent-tile fallback while child tiles are loading
  (`raster-fade-duration` driven cross-fade).
- Symbol collision improvements: `text-padding` / `symbol-sort-key` /
  `text-allow-overlap` / `text-ignore-placement` /
  `icon-rotation-alignment` / `text-rotation-alignment` /
  `*-pitch-alignment` / `text-keep-upright`.
- HTTP exponential-backoff retry for tile / sprite / glyph fetches.
- Pattern fills: `fill-pattern` / `line-pattern` / `fill-extrusion-pattern`
  (initially uniform per layer; now per-feature, see Unreleased above).
- `text-font` font-stack fallback for multi-language labels.
- `loadImage` async URL helper, `addImage` / `updateImage` / `removeImage`
  runtime image API.
- `SetSprite` / `SetGlyphs` runtime style hot-swap.
- Camera convenience: `PanBy` / `ZoomIn` / `ZoomOut` / `RotateTo` / `Stop`
  / `SetFilter` / `Set*Zoom` / `MaxBounds` / `HasLayer`, etc.
- `fill-translate` / `line-translate` / `circle-translate`.

## Versioning

The project has not yet cut a tagged release — this changelog tracks the
`main` branch. Once 1.0 is published, every entry under `[Unreleased]`
will be moved into the appropriate dated section.
