using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace MapLibre.Unity.Source
{
    /// <summary>
    /// Fetches glyph PBFs from a MapLibre style's <c>glyphs</c> URL template
    /// (e.g. <c>https://demotiles.maplibre.org/font/{fontstack}/{range}.pbf</c>),
    /// parses them, and packs the resulting glyphs into a <see cref="GlyphAtlas"/>.
    ///
    /// <para>
    /// Ranges are 256 codepoints wide (0-255, 256-511, ...). Each (fontstack,
    /// range) pair is requested at most once; subsequent <see cref="EnsureRange"/>
    /// calls return immediately if the range is already loaded or in flight.
    /// </para>
    /// </summary>
    public class GlyphSource
    {
        // MapLibre Spec uses fixed 256-codepoint ranges for glyph URLs.
        public const int RangeSize = 256;

        private string _urlTemplate;
        private MonoBehaviour _coroutineHost;
        private RequestTransformFunction _transformRequest;
        private readonly GlyphAtlas _atlas;

        // (fontstack, rangeStart) → state. We do not retry failed requests
        // automatically -- the user can call EnsureRange again after fixing
        // the underlying issue.
        private enum RangeState { Pending, Loaded, Failed }
        private readonly Dictionary<(string fontstack, int rangeStart), RangeState> _ranges = new();

        // Listeners notified when any new range finishes loading. SymbolRenderer
        // uses this to re-trigger a label re-layout once previously-missing
        // glyphs become available.
        public event Action OnAnyRangeLoaded;

        public GlyphAtlas Atlas => _atlas;
        public bool HasUrlTemplate => !string.IsNullOrEmpty(_urlTemplate);

        // Disk caching is delegated to TileDiskCache, which on WebGL routes
        // through the IndexedDB bridge (.jslib) and on every other platform
        // uses persistentDataPath/MapLibreCache via File.*. That unifies the
        // eviction policy and lets glyph PBFs survive page reloads on WebGL.
        public bool DiskCacheEnabled => TileDiskCache.Enabled;

        public GlyphSource(GlyphAtlas atlas = null)
        {
            _atlas = atlas ?? new GlyphAtlas();
        }

        public void Initialize(string urlTemplate, MonoBehaviour coroutineHost,
            RequestTransformFunction transformRequest = null)
        {
            _urlTemplate = urlTemplate;
            _coroutineHost = coroutineHost;
            _transformRequest = transformRequest;
        }

        public void SetUrlTemplate(string urlTemplate) => _urlTemplate = urlTemplate;

        /// <summary>
        /// Request the range that contains <paramref name="codepoint"/> for
        /// the supplied font stack. Returns true when the range is already in
        /// the atlas (caller can render immediately); false when a fetch was
        /// kicked off or has previously failed.
        /// </summary>
        public bool EnsureRange(string fontstack, uint codepoint)
        {
            if (string.IsNullOrEmpty(_urlTemplate) || _coroutineHost == null) return false;
            if (string.IsNullOrEmpty(fontstack)) return false;

            int rangeStart = (int)(codepoint / RangeSize) * RangeSize;
            var key = (fontstack, rangeStart);
            if (_ranges.TryGetValue(key, out var state))
            {
                return state == RangeState.Loaded;
            }
            _ranges[key] = RangeState.Pending;
            _coroutineHost.StartCoroutine(FetchRange(fontstack, rangeStart));
            return false;
        }

        private IEnumerator FetchRange(string fontstack, int rangeStart)
        {
            int rangeEnd = rangeStart + RangeSize - 1;

            // Use Uri.EscapeDataString rather than UnityWebRequest.EscapeURL.
            // EscapeURL is form-encoding (space → '+'), which is wrong for
            // URL path components -- MapLibre's glyph endpoint expects
            // %20 (e.g. demotiles 404s on "Noto+Sans+Regular" but 200s on
            // "Noto%20Sans%20Regular").
            string url = _urlTemplate
                .Replace("{fontstack}", Uri.EscapeDataString(fontstack))
                .Replace("{range}", $"{rangeStart}-{rangeEnd}");

            // Disk cache hit (TileDiskCache): skip the network entirely. The
            // stored body is raw PBF bytes, identical to what the HTTP path
            // returns, so the parser sees the same input either way. We treat
            // both fresh and stale hits as usable -- glyph endpoints rarely
            // change and we don't revalidate via ETag here.
            byte[] data = null;
            var lookup = new TileDiskCache.LookupResult();
            yield return TileDiskCache.GetAsync(url, lookup);
            if (lookup.Hit && lookup.Data != null && lookup.Data.Length > 0)
                data = lookup.Data;

            if (data == null)
            {
                var transformed = RequestTransformer.Apply(_transformRequest, url, ResourceKind.Glyphs);
                if (transformed.Abort)
                {
                    _ranges[(fontstack, rangeStart)] = RangeState.Failed;
                    yield break;
                }

                using var request = UnityWebRequest.Get(transformed.Url);
                request.SetRequestHeader("User-Agent", "MapLibre-Unity/0.1");
                request.SetRequestHeader("Accept", "application/x-protobuf, application/octet-stream");
                ApplyHeaders(request, transformed.Headers);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning(
                        $"[GlyphSource] Fetch failed for '{url}': {request.error} ({request.responseCode})");
                    _ranges[(fontstack, rangeStart)] = RangeState.Failed;
                    yield break;
                }

                data = request.downloadHandler.data;
                if (data == null || data.Length == 0)
                {
                    _ranges[(fontstack, rangeStart)] = RangeState.Failed;
                    yield break;
                }

                // Persist the freshly-fetched bytes for next time. Errors
                // are absorbed by TileDiskCache -- we already have the data
                // in memory and can re-fetch on cache failure.
                string etag = request.GetResponseHeader("ETag");
                string cc = request.GetResponseHeader("Cache-Control");
                TileDiskCache.Put(url, data, etag, cc);
            }

            // PBF parse runs on the main thread for now. Glyph PBFs are tiny
            // (a 256-codepoint range is typically < 50KB) so this stays cheap;
            // if profiling shows otherwise we can move it to Task.Run like the
            // vector tile path does.
            GlyphPbf parsed;
            try
            {
                parsed = GlyphPbfParser.Parse(data);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[GlyphSource] Parse failed for '{url}': {e.Message}");
                _ranges[(fontstack, rangeStart)] = RangeState.Failed;
                yield break;
            }

            // Pack every glyph into the atlas under the *requested* fontstack
            // name. The PBF's internal Stack.Name often differs from what the
            // style asked for (servers normalise / canonicalise), so keying
            // by the request makes SdfTextMeshBuilder.TryGet(fontstack, cp)
            // succeed with the same name the style used. Multi-stack PBFs
            // (rare -- comma-joined URL templates) merge their glyphs together
            // under the same key, matching MapLibre GL JS behaviour where the
            // first stack to provide a codepoint wins.
            foreach (var stack in parsed.Stacks)
            {
                foreach (var g in stack.Glyphs)
                    _atlas.Add(fontstack, g);
            }

            _ranges[(fontstack, rangeStart)] = RangeState.Loaded;
            OnAnyRangeLoaded?.Invoke();
        }

        private static void ApplyHeaders(UnityWebRequest request,
            IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null) return;
            foreach (var kv in headers)
                request.SetRequestHeader(kv.Key, kv.Value);
        }

        /// <summary>
        /// Wipe every persisted entry in <see cref="TileDiskCache"/>. Note that
        /// this clears tile bytes and other cached responses too -- the cache
        /// is shared across sources. The in-memory glyph atlas is not touched.
        /// </summary>
        public void ClearDiskCache() => TileDiskCache.Clear();
    }
}
