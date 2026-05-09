using System;
using System.Collections;
using System.Collections.Generic;
using MapLibre.Unity.Style;
using UnityEngine;
using UnityEngine.Networking;

namespace MapLibre.Unity.Source
{
    /// <summary>
    /// Common scaffolding shared by every tile-driven source (raster, vector,
    /// future PMTiles wrappers, etc.). Owns the request queue, concurrency
    /// limit, dedup set, and active <see cref="UnityWebRequest"/> tracking
    /// dictionary -- everything that's identical between sources, regardless
    /// of what payload type they ultimately deliver.
    ///
    /// The subclass plugs in the type-specific bits via the abstract members:
    ///   - <see cref="HasCustomLoader"/> / <see cref="DispatchCustomLoad"/>
    ///     for the user-supplied loader path,
    ///   - <see cref="FetchTile"/> for the HTTP+decode coroutine,
    ///   - <see cref="CallOnError"/> for surfacing scheduler-level failures
    ///     ("no tile URL templates configured") through the typed callback
    ///     the caller registered in <see cref="EnqueueRequest"/>.
    /// </summary>
    /// <typeparam name="TData">The decoded payload type the source delivers
    /// to its onComplete callback (Texture2D for raster, VectorTileData for
    /// vector, ...).</typeparam>
    public abstract class TileSourceBase<TData> : ISource
    {
        public string Id { get; protected set; }
        public SourceDefinition Definition { get; protected set; }

        protected MonoBehaviour _coroutineHost;
        protected RequestTransformFunction _transformRequest;
        protected List<string> _tileUrlTemplates = new();

        protected readonly Queue<TileRequest> _requestQueue = new();
        protected readonly HashSet<CanonicalTileID> _pendingRequests = new();
        protected readonly Dictionary<CanonicalTileID, UnityWebRequest> _activeWebRequests = new();
        protected int _activeRequests;
        protected int _maxConcurrentRequests = 6;

        protected struct TileRequest
        {
            public CanonicalTileID TileId;
            public Action<CanonicalTileID, TData> OnComplete;
            public Action<CanonicalTileID, string> OnError;
        }

        public void SetTileUrls(List<string> urls) => _tileUrlTemplates = new List<string>(urls);
        public bool HasTileUrls => _tileUrlTemplates.Count > 0;

        /// <summary>
        /// Round-robin pick over the configured URL templates. Math.Abs guards
        /// the modulo against negative-hash returns from <c>GetHashCode()</c>;
        /// a stable hash keeps the same tile pinned to the same template host
        /// across invocations, which helps any upstream HTTP cache that's
        /// keying on host name.
        /// </summary>
        protected string BuildTileUrl(CanonicalTileID tileId)
        {
            int templateIndex = Math.Abs(tileId.GetHashCode()) % _tileUrlTemplates.Count;
            return tileId.ToUrl(_tileUrlTemplates[templateIndex]);
        }

        /// <summary>
        /// Add a request to the queue and kick the scheduler. Caller must
        /// have already done their own "in cache?" / "out of bounds?" /
        /// "already pending?" checks; this method only handles the dedup +
        /// enqueue + scheduler tick.
        /// </summary>
        protected void EnqueueRequest(CanonicalTileID tileId,
            Action<CanonicalTileID, TData> onComplete,
            Action<CanonicalTileID, string> onError)
        {
            if (_pendingRequests.Contains(tileId)) return;
            _pendingRequests.Add(tileId);
            _requestQueue.Enqueue(new TileRequest
            {
                TileId = tileId,
                OnComplete = onComplete,
                OnError = onError,
            });
            ProcessQueue();
        }

        /// <summary>
        /// Drain the queue up to <see cref="_maxConcurrentRequests"/>. Routes
        /// each request through the user-supplied loader (if any) or the
        /// HTTP fetcher otherwise.
        /// </summary>
        protected void ProcessQueue()
        {
            while (_activeRequests < _maxConcurrentRequests && _requestQueue.Count > 0)
            {
                var req = _requestQueue.Dequeue();

                // Skip if cancelled while waiting in the queue.
                if (!_pendingRequests.Contains(req.TileId))
                    continue;

                if (HasCustomLoader)
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

        /// <summary>
        /// Cancel an in-flight or queued request. Subclasses with extra
        /// per-tile state (e.g. background parse tasks) override and forward
        /// to base after their own cleanup.
        /// </summary>
        public virtual void CancelRequest(CanonicalTileID tileId)
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

        /// <summary>True when the source has a user-supplied loader installed
        /// via SetCustomLoader. Implementations type the loader differently
        /// (ICustomRasterSource vs ICustomVectorSource), so we just expose
        /// the boolean and let the subclass dispatch.</summary>
        protected abstract bool HasCustomLoader { get; }

        /// <summary>Hand the request off to the user-supplied loader and wire
        /// up its bytes/error callbacks back into the cache + onComplete /
        /// onError contract. Subclass owns the loader-specific signature.</summary>
        protected abstract void DispatchCustomLoad(TileRequest req);

        /// <summary>HTTP fetch + payload decode coroutine. Subclass implements
        /// the source-type-specific decoding (image bytes → Texture2D, PBF →
        /// VectorTileData) and is responsible for the
        /// <c>_activeRequests--</c> / <c>_pendingRequests.Remove</c>
        /// bookkeeping after the coroutine settles.</summary>
        protected abstract IEnumerator FetchTile(CanonicalTileID tileId, string url,
            Action<CanonicalTileID, TData> onComplete,
            Action<CanonicalTileID, string> onError);

        public virtual void Dispose()
        {
            foreach (var kvp in _activeWebRequests)
            {
                kvp.Value.Abort();
                kvp.Value.Dispose();
            }
            _activeWebRequests.Clear();
            _requestQueue.Clear();
            _pendingRequests.Clear();
        }
    }
}
