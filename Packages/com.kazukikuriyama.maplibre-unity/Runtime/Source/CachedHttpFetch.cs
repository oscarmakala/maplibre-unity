using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace MapLibre.Unity.Source
{
    /// <summary>
    /// Shared "look up disk cache, conditionally HTTP GET, retry with backoff,
    /// honour 304" routine used by every tile-style source. Centralising it
    /// here keeps <see cref="RasterTileSource"/> and <see cref="VectorTileSource"/>
    /// from drifting on cache semantics (ETag forwarding, 304 promotion,
    /// no-store handling) -- the helper is the single source of truth.
    ///
    /// Lifecycle:
    ///   1. Caller fills the input fields (Url, TransformRequest, hooks, ...).
    ///   2. Caller starts the <see cref="Run"/> coroutine on its host MonoBehaviour.
    ///   3. Caller inspects the output fields once Run yields-break.
    ///
    /// Output disposition:
    ///   - <c>Aborted = true</c>: transformer requested abort, or the request
    ///     was cancelled / externally disposed mid-flight. Caller should yield
    ///     break without invoking its onComplete/onError.
    ///   - <c>Bytes != null</c>: success (fresh cache hit, 304 promotion from
    ///     cache, or 200 fetch). <c>StatusCode</c> reflects the source.
    ///   - <c>Bytes == null &amp;&amp; !Aborted</c>: terminal failure. Caller
    ///     consults <c>StatusCode</c> (e.g. 404 → empty-tile path) and
    ///     <c>ErrorMessage</c>.
    ///
    /// In-flight cancellation: the host installs callbacks that track the
    /// active <see cref="UnityWebRequest"/> in its own per-tile dict so an
    /// external CancelRequest can <c>Abort()</c> it. The helper detects that
    /// disposal via a guarded property probe.
    /// </summary>
    internal sealed class CachedHttpFetch
    {
        // ---------- inputs (set by caller before Run) ----------

        public string Url;
        public RequestTransformFunction TransformRequest;
        public ResourceKind Kind = ResourceKind.Tile;
        public string UserAgent = "MapLibre-Unity/0.1";
        public string AcceptHeader; // null = none

        /// <summary>Notified when a fresh <see cref="UnityWebRequest"/> is created.
        /// Host typically stores it in its tile-id keyed dict so external Cancel
        /// can Abort+Dispose it.</summary>
        public Action<UnityWebRequest> OnRequestStarted;

        /// <summary>Notified once per attempt after Dispose, regardless of
        /// outcome. Host removes the entry it stashed in OnRequestStarted.</summary>
        public Action OnRequestEnded;

        /// <summary>Returns false if the host has cancelled this fetch since the
        /// last yield (e.g. tile id removed from <c>_pendingRequests</c>).
        /// Checked between retries and after the SendWebRequest yield.
        /// Optional; null = always-pending.</summary>
        public Func<bool> IsStillPending;

        // ---------- outputs (read by caller after Run yields break) ----------

        public byte[] Bytes;
        public long StatusCode;     // 200, 304, 404, ... or 0 if no response
        public string ErrorMessage; // null on success
        public bool Aborted;

        public IEnumerator Run()
        {
            Bytes = null;
            StatusCode = 0;
            ErrorMessage = null;
            Aborted = false;

            var transformed = RequestTransformer.Apply(TransformRequest, Url, Kind);
            if (transformed.Abort) { Aborted = true; yield break; }

            // Disk cache lookup. A fresh hit short-circuits HTTP. A stale hit
            // forwards the ETag in If-None-Match for cheap revalidation. On
            // WebGL this yields one IndexedDB round-trip; on native it
            // resolves synchronously.
            byte[] cachedBytes = null;
            TileDiskCache.CacheEntry cacheMeta = null;
            var lookup = new TileDiskCache.LookupResult();
            yield return TileDiskCache.GetAsync(transformed.Url, lookup);
            if (lookup.Hit)
            {
                cachedBytes = lookup.Data;
                cacheMeta = lookup.Meta;
                if (lookup.IsFresh)
                {
                    Bytes = cachedBytes;
                    StatusCode = 200;
                    yield break;
                }
            }

            int attempt = 0;
            string lastError = null;

            while (attempt < HttpRetryPolicy.DefaultMaxAttempts)
            {
                var request = UnityWebRequest.Get(transformed.Url);
                request.SetRequestHeader("User-Agent", UserAgent);
                if (!string.IsNullOrEmpty(AcceptHeader))
                    request.SetRequestHeader("Accept", AcceptHeader);
                ApplyHeaders(request, transformed.Headers);
                if (cacheMeta != null && !string.IsNullOrEmpty(cacheMeta.ETag))
                    request.SetRequestHeader("If-None-Match", cacheMeta.ETag);
                OnRequestStarted?.Invoke(request);

                yield return request.SendWebRequest();

                // Unity-internal cleanup (PlayMode scene teardown, etc.) can
                // invalidate the UnityWebRequest's native handle without going
                // through CancelRequest -- the managed ref stays non-null but
                // any property access throws NRE. Probe under try/catch so
                // we can bail cleanly instead of crashing.
                bool externallyDisposed = false;
                try { _ = request.isDone; }
                catch (NullReferenceException) { externallyDisposed = true; }

                if (externallyDisposed || (IsStillPending != null && !IsStillPending()))
                {
                    Aborted = true;
                    OnRequestEnded?.Invoke();
                    yield break;
                }

                long status = request.responseCode;

                // 304 Not Modified: server confirmed the cached body is current.
                // Refresh the cached freshness window (and any new ETag the
                // server may have rotated to).
                if (status == 304 && cachedBytes != null)
                {
                    StatusCode = 304;
                    Bytes = cachedBytes;
                    TileDiskCache.Touch(transformed.Url,
                        request.GetResponseHeader("ETag"),
                        request.GetResponseHeader("Cache-Control"));
                    request.Dispose();
                    OnRequestEnded?.Invoke();
                    yield break;
                }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    StatusCode = status;
                    Bytes = request.downloadHandler.data;
                    string cc = request.GetResponseHeader("Cache-Control");
                    string etag = request.GetResponseHeader("ETag");
                    request.Dispose();
                    OnRequestEnded?.Invoke();
                    if (Bytes != null && Bytes.Length > 0)
                        TileDiskCache.Put(transformed.Url, Bytes, etag, cc);
                    yield break;
                }

                // Failure on this attempt. 404 is terminal -- the tile/resource
                // genuinely doesn't exist, no point retrying. ShouldRetry
                // covers transient transport errors (HTTP 5xx, ConnectionError).
                StatusCode = status;
                lastError = request.error;
                bool retry = status != 404 && HttpRetryPolicy.ShouldRetry(request);
                request.Dispose();
                OnRequestEnded?.Invoke();

                if (!retry) break;
                attempt++;
                if (attempt >= HttpRetryPolicy.DefaultMaxAttempts) break;

                yield return new WaitForSeconds(HttpRetryPolicy.BackoffSeconds(attempt));
                if (IsStillPending != null && !IsStillPending())
                {
                    Aborted = true;
                    yield break;
                }
            }

            // Out of retries with no usable bytes.
            ErrorMessage = lastError ?? "no response";
        }

        private static void ApplyHeaders(UnityWebRequest request, IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null) return;
            foreach (var kvp in headers)
                if (!string.IsNullOrEmpty(kvp.Key))
                    request.SetRequestHeader(kvp.Key, kvp.Value ?? string.Empty);
        }
    }
}
