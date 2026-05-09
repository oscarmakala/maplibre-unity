using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using MapLibre.Unity.Style;
using MapLibre.Unity.VectorTile;
using UnityEngine;
using UnityEngine.Networking;

namespace MapLibre.Unity.Source
{
    /// <summary>
    /// Fetches and caches Mapbox Vector Tiles (PBF format).
    /// PBF parsing and gzip decompression are performed on background threads.
    /// </summary>
    public class VectorTileSource : TileSourceBase<VectorTileData>
    {
        /// <summary>
        /// Read-only access to the tile cache for feature queries.
        /// </summary>
        public VectorTileCache Cache => _cache;

        private VectorTileCache _cache;
        private readonly Dictionary<CanonicalTileID, CancellationTokenSource> _activeParseTasks = new();
        // When non-null, ProcessQueue routes tile loads through this user-supplied
        // hook instead of HTTP. Mirrors MapLibre GL JS type:"custom" / dataType:"vector".
        private ICustomVectorSource _customLoader;

        /// <summary>
        /// Timeout in seconds for background PBF parsing per tile.
        /// If parsing exceeds this duration, the task is cancelled and an error is reported.
        /// Includes ThreadPool scheduling latency, so it must be generous enough to cover
        /// large planet tiles on player builds with constrained core counts. Low-zoom
        /// tiles over dense regions (e.g. openfreemap planet z8 over Asia) genuinely
        /// take tens of seconds to parse on a single thread; the Profiler being
        /// attached pushes this further. 90s leaves headroom for those cases without
        /// holding broken tiles forever.
        /// </summary>
        private const float ParseTimeoutSeconds = 90f;

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// Per-frame parse budget on WebGL Player, where the parser runs on
        /// the main thread (no real worker available). 4 ms keeps us well
        /// under a 16.6 ms / 60 fps frame even when other game work runs on
        /// the same frame; the cost is more yields and a longer wall-clock
        /// for the full parse (since each yield drops out to the scheduler).
        /// </summary>
        private const int WebGLParseBudgetMs = 4;
#endif

        public void Initialize(string id, SourceDefinition definition, MonoBehaviour coroutineHost,
            int cacheCapacity = 256, RequestTransformFunction transformRequest = null)
        {
            Id = id;
            Definition = definition;
            _coroutineHost = coroutineHost;
            _cache = new VectorTileCache(cacheCapacity);
            _transformRequest = transformRequest;

            if (definition.Tiles != null)
                _tileUrlTemplates = new List<string>(definition.Tiles);
        }

        /// <summary>
        /// Replace the URL fetcher with a user-supplied <see cref="ICustomVectorSource"/>.
        /// Subsequent tile requests are forwarded to <c>loader.LoadTile</c>; the
        /// returned bytes go through the same gzip + PBF parser used for HTTP tiles.
        /// Pass null to revert to URL template fetching.
        /// </summary>
        public void SetCustomLoader(ICustomVectorSource loader)
        {
            _customLoader = loader;
        }

        protected override bool HasCustomLoader => _customLoader != null;

        public void RequestTile(CanonicalTileID tileId,
            Action<CanonicalTileID, VectorTileData> onComplete,
            Action<CanonicalTileID, string> onError)
        {
            if (tileId.Z < Definition.MinZoom || tileId.Z > Definition.MaxZoom)
                return;

            // Tiles outside the source's declared bounds are served as empty
            // instead of being requested -- matches MapLibre GL JS behavior.
            if (!Definition.ContainsTile(tileId))
            {
                var empty = new VectorTileData();
                _cache.Put(tileId, empty);
                onComplete?.Invoke(tileId, empty);
                return;
            }

            if (_cache.TryGet(tileId, out var cached))
            {
                onComplete?.Invoke(tileId, cached);
                return;
            }

            EnqueueRequest(tileId, onComplete, onError);
        }

        public override void CancelRequest(CanonicalTileID tileId)
        {
            // Cancel background parse task if running.
            if (_activeParseTasks.TryGetValue(tileId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _activeParseTasks.Remove(tileId);
            }
            base.CancelRequest(tileId);
        }

        protected override void DispatchCustomLoad(TileRequest req)
        {
            var loader = _customLoader;
            var tileId = req.TileId;
            var onComplete = req.OnComplete;
            var onError = req.OnError;
            // Guard against double-callback from the user loader.
            bool finished = false;

            try
            {
                loader.LoadTile(tileId,
                    bytes =>
                    {
                        if (finished) return;
                        finished = true;
                        if (bytes == null || bytes.Length == 0)
                        {
                            // Treat empty as "no data here" -- same behaviour as
                            // an HTTP 404 in the URL fetcher.
                            var empty = new VectorTileData();
                            _cache.Put(tileId, empty);
                            _activeRequests--;
                            _pendingRequests.Remove(tileId);
                            onComplete?.Invoke(tileId, empty);
                            ProcessQueue();
                            return;
                        }
                        _coroutineHost.StartCoroutine(
                            FinishCustomLoad(tileId, bytes, onComplete, onError));
                    },
                    err =>
                    {
                        if (finished) return;
                        finished = true;
                        _activeRequests--;
                        _pendingRequests.Remove(tileId);
                        onError?.Invoke(tileId, err ?? "Custom source error");
                        ProcessQueue();
                    });
            }
            catch (Exception e)
            {
                if (!finished)
                {
                    finished = true;
                    _activeRequests--;
                    _pendingRequests.Remove(tileId);
                    onError?.Invoke(tileId, $"Custom loader threw: {e.Message}");
                    ProcessQueue();
                }
            }
        }

        private IEnumerator FinishCustomLoad(CanonicalTileID tileId, byte[] bytes,
            Action<CanonicalTileID, VectorTileData> onComplete,
            Action<CanonicalTileID, string> onError)
        {
            // The HTTP path decrements + removes BEFORE handing off to
            // ParseAndDeliver (so the slot is freed regardless of how parsing
            // ends). Mirror that ordering for the custom-loader path.
            _activeRequests--;
            _pendingRequests.Remove(tileId);

            yield return ParseAndDeliver(tileId, bytes, "custom", onComplete, onError);
            ProcessQueue();
        }

        protected override IEnumerator FetchTile(CanonicalTileID tileId, string url,
            Action<CanonicalTileID, VectorTileData> onComplete,
            Action<CanonicalTileID, string> onError)
        {
            var fetch = new CachedHttpFetch
            {
                Url = url,
                TransformRequest = _transformRequest,
                Kind = ResourceKind.Tile,
                AcceptHeader = "application/x-protobuf, application/octet-stream",
                OnRequestStarted = req => _activeWebRequests[tileId] = req,
                OnRequestEnded = () => _activeWebRequests.Remove(tileId),
                IsStillPending = () => _pendingRequests.Contains(tileId),
            };
            yield return fetch.Run();

            _activeRequests--;
            _pendingRequests.Remove(tileId);

            if (fetch.Aborted)
            {
                ProcessQueue();
                yield break;
            }

            if (fetch.Bytes != null)
            {
                yield return ParseAndDeliver(tileId, fetch.Bytes, url, onComplete, onError);
                ProcessQueue();
                yield break;
            }

            // 404 = tile has no data. MapLibre GL JS treats this as an empty
            // tile (state=loaded, no features) rather than an error, since
            // sparse vector datasets commonly have gaps.
            if (fetch.StatusCode == 404)
            {
                var empty = new VectorTileData();
                _cache.Put(tileId, empty);
                onComplete?.Invoke(tileId, empty);
            }
            else
            {
                onError?.Invoke(tileId, $"{url}: {fetch.ErrorMessage ?? "no response"}");
            }
            ProcessQueue();
        }

        /// <summary>
        /// gzip-decompress + PBF parse for a tile body and dispatch the result
        /// to the caller. Shared between HTTP and custom-loader paths so both
        /// honour the same parse timeout / cancellation semantics.
        /// On native: the work runs on a worker via BackgroundTask.Run. On
        /// WebGL Player there's no real worker, so ParseLazy is driven from
        /// the main thread with a per-frame budget; otherwise a heavy planet
        /// tile would freeze the main loop for seconds.
        /// </summary>
        private IEnumerator ParseAndDeliver(CanonicalTileID tileId, byte[] rawData, string url,
            Action<CanonicalTileID, VectorTileData> onComplete,
            Action<CanonicalTileID, string> onError)
        {
            var cts = new CancellationTokenSource();
            _activeParseTasks[tileId] = cts;
            var token = cts.Token;

#if UNITY_WEBGL && !UNITY_EDITOR
            VectorTileData parsed = null;
            string failMessage = null;
            bool timedOut = false;
            yield return ParseTileBytesIncremental(rawData, token,
                onParsed: t => parsed = t,
                onTimedOut: msg => { failMessage = msg; timedOut = true; },
                onFaulted: msg => failMessage = msg);

            _activeParseTasks.Remove(tileId);
            cts.Dispose();

            if (parsed != null)
            {
                _cache.Put(tileId, parsed);
                onComplete?.Invoke(tileId, parsed);
            }
            else if (timedOut)
            {
                onError?.Invoke(tileId, $"Parse timeout for {url}: {failMessage}");
            }
            else
            {
                onError?.Invoke(tileId,
                    $"Parse error for {url}: {failMessage ?? "Unknown parse error"}");
            }
#else
            Task<VectorTileData> parseTask = BackgroundTask.Run(() =>
            {
                byte[] data = rawData;
                if (data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b)
                {
                    data = DecompressGzip(data);
                }
                token.ThrowIfCancellationRequested();
                var result = VectorTileParser.Parse(data);
                token.ThrowIfCancellationRequested();
                return result;
            }, token);

            // Wait for background completion with a wall-clock timeout.
            // Stopwatch is used (not Time.deltaTime) so the timer is unaffected
            // by Time.timeScale or Time.maximumDeltaTime clamping during low
            // frame rates.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!parseTask.IsCompleted)
            {
                if (watch.Elapsed.TotalSeconds > ParseTimeoutSeconds)
                {
                    cts.Cancel();
                    while (!parseTask.IsCompleted)
                        yield return null;
                    break;
                }
                yield return null;
            }

            _activeParseTasks.Remove(tileId);
            cts.Dispose();

            if (parseTask.Status == TaskStatus.RanToCompletion && parseTask.Result != null)
            {
                _cache.Put(tileId, parseTask.Result);
                onComplete?.Invoke(tileId, parseTask.Result);
            }
            else if (parseTask.IsCanceled)
            {
                onError?.Invoke(tileId,
                    $"Parse timeout for {url}: exceeded {ParseTimeoutSeconds}s");
            }
            else if (parseTask.IsFaulted)
            {
                string errorMsg = parseTask.Exception?.InnerException?.Message
                                  ?? "Unknown parse error";
                onError?.Invoke(tileId, $"Parse error for {url}: {errorMsg}");
            }
            else
            {
                onError?.Invoke(tileId, $"Parse returned null for {url}");
            }
#endif
        }

        private static byte[] DecompressGzip(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// WebGL-only incremental parser. Decompresses gzip and feeds the raw
        /// bytes through <see cref="VectorTileParser.ParseLazy"/>, accumulating
        /// layers into a <see cref="VectorTileData"/>. Yields back to the
        /// coroutine scheduler every <see cref="WebGLParseBudgetMs"/> ms so a
        /// heavy planet tile (~1-3 sec total parse on a single thread) shows
        /// up as a string of small frame budget overruns rather than a single
        /// multi-second freeze. The non-WebGL path keeps using
        /// <c>BackgroundTask.Run</c> + eager Parse, since a worker thread can
        /// finish in one go without affecting the main loop.
        /// </summary>
        private IEnumerator ParseTileBytesIncremental(byte[] bytes, CancellationToken token,
            Action<VectorTileData> onParsed,
            Action<string> onTimedOut,
            Action<string> onFaulted)
        {
            byte[] data = bytes;
            // gzip step is fast (~1-5 ms even for planet z9); skip the yield.
            string preError = null;
            try
            {
                if (data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b)
                    data = DecompressGzip(data);
            }
            catch (Exception e) { preError = $"Gzip decompress failed: {e.Message}"; }
            if (preError != null) { onFaulted?.Invoke(preError); yield break; }

            IEnumerator<VectorTileLayer> layerStream = null;
            string initError = null;
            try { layerStream = VectorTileParser.ParseLazy(data).GetEnumerator(); }
            catch (Exception e) { initError = e.Message; }
            if (initError != null) { onFaulted?.Invoke(initError); yield break; }

            var tile = new VectorTileData();
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();
            var budgetWatch = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                if (token.IsCancellationRequested ||
                    totalWatch.Elapsed.TotalSeconds > ParseTimeoutSeconds)
                {
                    layerStream.Dispose();
                    onTimedOut?.Invoke($"exceeded {ParseTimeoutSeconds}s");
                    yield break;
                }

                bool hasNext;
                string stepError = null;
                try { hasNext = layerStream.MoveNext(); }
                catch (Exception e) { hasNext = false; stepError = e.Message; }
                if (stepError != null)
                {
                    layerStream.Dispose();
                    onFaulted?.Invoke(stepError);
                    yield break;
                }
                if (!hasNext) break;

                tile._layers.Add(layerStream.Current);
                if (budgetWatch.ElapsedMilliseconds > WebGLParseBudgetMs)
                {
                    yield return null;
                    budgetWatch.Restart();
                }
            }

            layerStream.Dispose();
            onParsed?.Invoke(tile);
        }
#endif

        public override void Dispose()
        {
            foreach (var kvp in _activeParseTasks)
            {
                kvp.Value.Cancel();
                kvp.Value.Dispose();
            }
            _activeParseTasks.Clear();

            base.Dispose();
            _cache?.Clear();
        }
    }
}
