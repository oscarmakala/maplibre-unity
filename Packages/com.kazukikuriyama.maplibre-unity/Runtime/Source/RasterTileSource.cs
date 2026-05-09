using System;
using System.Collections;
using System.Collections.Generic;
using MapLibre.Unity.Style;
using UnityEngine;
using UnityEngine.Networking;

namespace MapLibre.Unity.Source
{
    public class RasterTileSource : ISource
    {
        public string Id { get; private set; }
        public SourceDefinition Definition { get; private set; }

        private List<string> _tileUrlTemplates = new();
        private TileCache _cache;
        private MonoBehaviour _coroutineHost;
        private int _maxConcurrentRequests = 6;
        private int _activeRequests;
        private RequestTransformFunction _transformRequest;
        private readonly Queue<TileRequest> _requestQueue = new();
        private readonly HashSet<CanonicalTileID> _pendingRequests = new();
        private readonly Dictionary<CanonicalTileID, UnityWebRequest> _activeWebRequests = new();
        // When non-null, ProcessQueue routes tile loads through this user-supplied
        // hook instead of the built-in URL template fetcher. Mirrors MapLibre GL JS
        // type:"custom" / dataType:"raster" sources.
        private ICustomRasterSource _customLoader;

        private struct TileRequest
        {
            public CanonicalTileID TileId;
            public Action<CanonicalTileID, Texture2D> OnComplete;
            public Action<CanonicalTileID, string> OnError;
        }

        public void Initialize(string id, SourceDefinition definition, MonoBehaviour coroutineHost,
            int cacheCapacity = 256, RequestTransformFunction transformRequest = null)
        {
            Id = id;
            Definition = definition;
            _coroutineHost = coroutineHost;
            _cache = new TileCache(cacheCapacity);
            _transformRequest = transformRequest;

            if (definition.Tiles != null)
                _tileUrlTemplates = new List<string>(definition.Tiles);
        }

        public void SetTileUrls(List<string> urls)
        {
            _tileUrlTemplates = new List<string>(urls);
        }

        public bool HasTileUrls => _tileUrlTemplates.Count > 0;

        /// <summary>
        /// Replace the URL fetcher with a user-supplied <see cref="ICustomRasterSource"/>.
        /// All subsequent tile requests are forwarded to <c>loader.LoadTile</c> instead
        /// of HTTP. Pass null to revert to URL template fetching.
        /// </summary>
        public void SetCustomLoader(ICustomRasterSource loader)
        {
            _customLoader = loader;
        }

        /// <summary>
        /// Walk parent tiles up the pyramid until a cached texture is found.
        /// Used by renderers for parent-tile fallback while a child is loading,
        /// matching MapLibre GL JS' behavior of showing a coarser tile until the
        /// finer one arrives. Returns false when no ancestor up to <paramref name="maxAncestorSteps"/>
        /// is in cache.
        /// </summary>
        public bool TryGetCachedAncestor(CanonicalTileID childTileId,
            out CanonicalTileID parentTileId, out Texture2D texture, int maxAncestorSteps = 5)
        {
            var current = childTileId;
            for (int step = 0; step < maxAncestorSteps; step++)
            {
                if (current.Z <= 0) break;
                current = current.Parent();
                if (current.Z < Definition.MinZoom) break;
                if (_cache.TryGet(current, out texture))
                {
                    parentTileId = current;
                    return true;
                }
            }
            parentTileId = default;
            texture = null;
            return false;
        }

        public void RequestTile(CanonicalTileID tileId,
            Action<CanonicalTileID, Texture2D> onComplete,
            Action<CanonicalTileID, string> onError)
        {
            // Check zoom bounds
            if (tileId.Z < Definition.MinZoom || tileId.Z > Definition.MaxZoom)
                return;

            // Skip tiles outside the source's declared bounds.
            // Matches MapLibre GL JS TileBounds filter.
            if (!Definition.ContainsTile(tileId))
                return;

            // Check cache
            if (_cache.TryGet(tileId, out var cached))
            {
                onComplete?.Invoke(tileId, cached);
                return;
            }

            // Deduplication
            if (_pendingRequests.Contains(tileId))
                return;

            _pendingRequests.Add(tileId);
            _requestQueue.Enqueue(new TileRequest
            {
                TileId = tileId,
                OnComplete = onComplete,
                OnError = onError
            });
            ProcessQueue();
        }

        public void CancelRequest(CanonicalTileID tileId)
        {
            _pendingRequests.Remove(tileId);
            if (_activeWebRequests.TryGetValue(tileId, out var request))
            {
                request.Abort();
                request.Dispose();
                _activeWebRequests.Remove(tileId);
                _activeRequests--;
                ProcessQueue();
            }
        }

        private void ProcessQueue()
        {
            while (_activeRequests < _maxConcurrentRequests && _requestQueue.Count > 0)
            {
                var req = _requestQueue.Dequeue();

                // Skip if already cancelled
                if (!_pendingRequests.Contains(req.TileId))
                    continue;

                if (_customLoader != null)
                {
                    _activeRequests++;
                    DispatchCustomLoad(req);
                    continue;
                }

                if (_tileUrlTemplates.Count == 0)
                {
                    req.OnError?.Invoke(req.TileId, "No tile URL templates configured");
                    _pendingRequests.Remove(req.TileId);
                    continue;
                }

                _activeRequests++;
                string url = BuildTileUrl(req.TileId);
                _coroutineHost.StartCoroutine(FetchTile(req.TileId, url, req.OnComplete, req.OnError));
            }
        }

        private void DispatchCustomLoad(TileRequest req)
        {
            var loader = _customLoader;
            var tileId = req.TileId;
            var onComplete = req.OnComplete;
            var onError = req.OnError;
            // Guard against the loader invoking both callbacks (or invoking one
            // twice) -- release the request slot exactly once either way.
            bool finished = false;

            try
            {
                loader.LoadTile(tileId,
                    tex =>
                    {
                        if (finished) return;
                        finished = true;
                        _activeRequests--;
                        _pendingRequests.Remove(tileId);
                        if (tex != null)
                        {
                            _cache.Put(tileId, tex);
                            onComplete?.Invoke(tileId, tex);
                        }
                        else
                        {
                            onError?.Invoke(tileId, "Custom source returned null texture");
                        }
                        ProcessQueue();
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
                // Synchronous throw from the user loader -- release the slot and
                // surface the error so the renderer doesn't deadlock.
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

        private IEnumerator FetchTile(CanonicalTileID tileId, string url,
            Action<CanonicalTileID, Texture2D> onComplete,
            Action<CanonicalTileID, string> onError)
        {
            var fetch = new CachedHttpFetch
            {
                Url = url,
                TransformRequest = _transformRequest,
                Kind = ResourceKind.Tile,
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

            if (fetch.Bytes != null && fetch.Bytes.Length > 0)
            {
                // Decode bytes → Texture2D on the main thread (LoadImage is main-thread).
                // We download as raw bytes (not UnityWebRequestTexture) so the
                // body is available for disk caching; the decode happens here.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(fetch.Bytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    onError?.Invoke(tileId, $"{url}: image decode failed");
                }
                else
                {
                    texture.wrapMode = TextureWrapMode.Clamp;
                    // DEM tiles encode elevation packed across RGB byte channels
                    // (e.g. Terrarium: R*256 + G + B/256). Bilinear interpolation
                    // across byte boundaries produces oscillating decoded heights,
                    // visible as wave/banding artifacts when over-zoomed. Sample
                    // DEM tiles with point filtering; do bilinear on regular rasters.
                    texture.filterMode = Definition != null && Definition.Type == SourceType.RasterDem
                        ? FilterMode.Point
                        : FilterMode.Bilinear;
                    _cache.Put(tileId, texture);
                    onComplete?.Invoke(tileId, texture);
                }
            }
            else
            {
                onError?.Invoke(tileId, $"{url}: {fetch.ErrorMessage ?? "unknown error"}");
            }

            ProcessQueue();
        }

        private string BuildTileUrl(CanonicalTileID tileId)
        {
            int templateIndex = Math.Abs(tileId.GetHashCode()) % _tileUrlTemplates.Count;
            return tileId.ToUrl(_tileUrlTemplates[templateIndex]);
        }

        public void Dispose()
        {
            foreach (var kvp in _activeWebRequests)
            {
                kvp.Value.Abort();
                kvp.Value.Dispose();
            }
            _activeWebRequests.Clear();
            _requestQueue.Clear();
            _pendingRequests.Clear();
            _cache?.Clear();
        }
    }
}
