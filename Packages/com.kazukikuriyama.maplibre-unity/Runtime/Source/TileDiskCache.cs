using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace MapLibre.Unity.Source
{
    /// <summary>
    /// Persistent on-disk cache for HTTP responses (tile bytes, sprite JSON, TileJSON, etc.).
    /// Mirrors what a browser's disk cache gives MapLibre GL JS for free: cold-start the map
    /// without re-downloading every tile, save mobile bandwidth, and reduce tile-server costs.
    ///
    /// Each entry is stored as two files inside <see cref="CacheDirectory"/>:
    ///   &lt;sha1(url)&gt;.bin     raw response body
    ///   &lt;sha1(url)&gt;.meta    JSON metadata (etag, max-age, fetched-at)
    ///
    /// Honours Cache-Control: max-age and ETag for revalidation. When max-age is missing
    /// (server didn't send Cache-Control), entries fall back to <see cref="DefaultMaxAgeSeconds"/>
    /// so users still get offline-friendly behaviour.
    ///
    /// All public methods are safe to call from a single thread (typically the Unity main
    /// thread). Internal file I/O is wrapped in try/catch so a cache failure never breaks
    /// the request -- the source falls back to a normal HTTP fetch.
    /// </summary>
    public static class TileDiskCache
    {
        /// <summary>
        /// Master toggle. When false, all reads return miss and writes are no-ops.
        /// Enabled on every platform: native builds use File.* under
        /// <see cref="CacheDirectory"/>, WebGL Player builds route through the
        /// async <c>MapLibreCacheBridge</c> (.jslib) which talks to IndexedDB.
        /// The old WebGL-disabled default existed because synchronous File.*
        /// against IDBFS would block on IndexedDB sync; the bridge replaces
        /// that with proper async reads/writes so the toggle is now safe to
        /// leave on everywhere.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Where cache files live. Defaults to <c>persistentDataPath/MapLibreCache</c>.
        /// Reset to null to recompute from persistentDataPath next access.
        /// </summary>
        public static string CacheDirectory { get; set; }

        /// <summary>
        /// Soft cap on total cache size in bytes. Eviction runs on Put() when exceeded.
        /// Default: 100 MB. Set to 0 to disable size-based eviction. On WebGL
        /// the value is forwarded to the IndexedDB bridge which enforces the
        /// same soft cap via lastAccess-ordered eviction.
        /// </summary>
        public static long MaxBytes
        {
            get => _maxBytes;
            set
            {
                _maxBytes = value;
#if UNITY_WEBGL && !UNITY_EDITOR
                if (Enabled) MapLibreCacheBridge.Init(_maxBytes);
#endif
            }
        }
        private static long _maxBytes = 100L * 1024 * 1024;

        /// <summary>
        /// Default time-to-live (seconds) used when a server response lacks
        /// Cache-Control: max-age. Default: 24 hours.
        /// </summary>
        public static int DefaultMaxAgeSeconds { get; set; } = 24 * 60 * 60;

        public class CacheEntry
        {
            public string Url;
            public string ETag;
            public long FetchedAtUnixSeconds;
            public int MaxAgeSeconds;
            public long ByteLength;

            [JsonIgnore]
            public bool IsExpired
            {
                get
                {
                    if (MaxAgeSeconds <= 0) return true;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return (now - FetchedAtUnixSeconds) > MaxAgeSeconds;
                }
            }
        }

        private static string ResolveCacheDir()
        {
            if (string.IsNullOrEmpty(CacheDirectory))
                CacheDirectory = Path.Combine(Application.persistentDataPath, "MapLibreCache");
            try
            {
                if (!Directory.Exists(CacheDirectory))
                    Directory.CreateDirectory(CacheDirectory);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TileDiskCache] Failed to create cache dir: {e.Message}");
                Enabled = false;
            }
            return CacheDirectory;
        }

        private static string KeyFor(string url)
        {
            using var sha = SHA1.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
            var sb = new StringBuilder(40);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string DataPath(string key) => Path.Combine(ResolveCacheDir(), key + ".bin");
        private static string MetaPath(string key) => Path.Combine(ResolveCacheDir(), key + ".meta");

        /// <summary>
        /// Lookup result for <see cref="GetAsync"/>. Pre-allocated by the caller
        /// so the coroutine can mutate it in place without allocating closures.
        /// </summary>
        public class LookupResult
        {
            public bool Hit;
            public byte[] Data;
            public CacheEntry Meta;
            public bool IsFresh;

            public void Reset()
            {
                Hit = false;
                Data = null;
                Meta = null;
                IsFresh = false;
            }
        }

        /// <summary>
        /// Look up a URL. Returns true with fresh data when the cached copy is still
        /// within max-age. Even on a stale hit returns the metadata via <paramref name="meta"/>
        /// so callers can perform conditional revalidation (If-None-Match).
        ///
        /// Synchronous variant -- works on every platform except WebGL Player
        /// builds, where IndexedDB is async and this method always returns false.
        /// Use <see cref="GetAsync"/> for code paths that must work on WebGL too.
        /// </summary>
        public static bool TryGet(string url, out byte[] data, out CacheEntry meta, out bool isFresh)
        {
            data = null;
            meta = null;
            isFresh = false;
            if (!Enabled) return false;

#if UNITY_WEBGL && !UNITY_EDITOR
            // IndexedDB reads can't be exposed synchronously without blocking
            // the main thread on a JS event loop turn. Force callers onto the
            // async API instead.
            return false;
#else
            string key = KeyFor(url);
            string dataPath = DataPath(key);
            string metaPath = MetaPath(key);
            try
            {
                if (!File.Exists(dataPath) || !File.Exists(metaPath)) return false;
                string metaJson = File.ReadAllText(metaPath);
                meta = JsonConvert.DeserializeObject<CacheEntry>(metaJson);
                if (meta == null) return false;

                data = File.ReadAllBytes(dataPath);
                isFresh = !meta.IsExpired;
                // Bump access time so size-based eviction prefers older-touched files.
                try { File.SetLastAccessTimeUtc(dataPath, DateTime.UtcNow); } catch { }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TileDiskCache] Read failed for {url}: {e.Message}");
                return false;
            }
#endif
        }

        /// <summary>
        /// Coroutine-friendly lookup. On native platforms this completes in a
        /// single iteration via the synchronous <see cref="TryGet"/> path; on
        /// WebGL it polls <c>MapLibreCacheBridge</c> until the IndexedDB
        /// transaction settles (yielding null between polls so the frame
        /// budget isn't exceeded). The result is written into
        /// <paramref name="result"/> -- callers reuse the same instance across
        /// fetches to avoid allocating per-tile.
        /// </summary>
        public static IEnumerator GetAsync(string url, LookupResult result)
        {
            if (result == null) yield break;
            result.Reset();
            if (!Enabled || string.IsNullOrEmpty(url)) yield break;

#if UNITY_WEBGL && !UNITY_EDITOR
            int reqId = MapLibreCacheBridge.BeginGet(KeyFor(url));
            if (reqId <= 0) yield break;

            int poll;
            // Poll once per frame. IndexedDB resolves on microtask boundaries
            // so a hit is typically available within 1-2 frames; a miss the
            // same.
            while ((poll = MapLibreCacheBridge.PollGet(reqId)) == 0)
                yield return null;

            if (poll != 2)
            {
                MapLibreCacheBridge.ReleaseRequest(reqId);
                yield break;
            }

            byte[] data = MapLibreCacheBridge.CopyResultBytes(reqId);
            string metaJson = MapLibreCacheBridge.CopyResultMetaJson(reqId);
            MapLibreCacheBridge.ReleaseRequest(reqId);

            if (data == null || data.Length == 0) yield break;

            CacheEntry meta = null;
            if (!string.IsNullOrEmpty(metaJson))
            {
                try { meta = JsonConvert.DeserializeObject<CacheEntry>(metaJson); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[TileDiskCache] Bad meta JSON for {url}: {e.Message}");
                }
            }

            result.Hit = true;
            result.Data = data;
            result.Meta = meta;
            result.IsFresh = meta != null && !meta.IsExpired;
#else
            if (TryGet(url, out var data, out var meta, out var fresh))
            {
                result.Hit = true;
                result.Data = data;
                result.Meta = meta;
                result.IsFresh = fresh;
            }
            yield break;
#endif
        }

        /// <summary>
        /// Store a response body and its metadata. <paramref name="cacheControlHeader"/>
        /// is the raw value of the Cache-Control response header (may be null).
        /// </summary>
        public static void Put(string url, byte[] data, string etag, string cacheControlHeader)
        {
            if (!Enabled) return;
            if (string.IsNullOrEmpty(url) || data == null || data.Length == 0) return;

            int maxAge = ParseMaxAge(cacheControlHeader);
            // Respect Cache-Control: no-store / no-cache by skipping persistence entirely.
            if (cacheControlHeader != null &&
                (cacheControlHeader.IndexOf("no-store", StringComparison.OrdinalIgnoreCase) >= 0
                 || cacheControlHeader.IndexOf("no-cache", StringComparison.OrdinalIgnoreCase) >= 0))
                return;
            if (maxAge < 0) maxAge = DefaultMaxAgeSeconds;

            var meta = new CacheEntry
            {
                Url = url,
                ETag = etag,
                FetchedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MaxAgeSeconds = maxAge,
                ByteLength = data.LongLength,
            };
            string key = KeyFor(url);
            string metaJson = JsonConvert.SerializeObject(meta);

#if UNITY_WEBGL && !UNITY_EDITOR
            // Fire-and-forget: the JS bridge writes to IndexedDB asynchronously.
            // We can't await completion here because we're not in a coroutine,
            // but the caller already has the bytes in memory and a write
            // failure just means the next session re-fetches.
            MapLibreCacheBridge.Put(key, url, data, metaJson);
#else
            try
            {
                File.WriteAllBytes(DataPath(key), data);
                File.WriteAllText(MetaPath(key), metaJson);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TileDiskCache] Write failed for {url}: {e.Message}");
                return;
            }

            if (MaxBytes > 0) EvictIfOverCap();
#endif
        }

        /// <summary>
        /// Refresh the cached entry's freshness timestamp after a successful 304
        /// revalidation (server confirmed the cached body is still current).
        /// </summary>
        public static void Touch(string url, string cacheControlHeader)
        {
            if (!Enabled) return;
            int maxAge = ParseMaxAge(cacheControlHeader);
            if (maxAge < 0) maxAge = DefaultMaxAgeSeconds;
            string key = KeyFor(url);

#if UNITY_WEBGL && !UNITY_EDITOR
            // The JS bridge looks up the existing record by key and rewrites
            // its meta JSON. We pass a fresh meta envelope; the bridge merges
            // it onto the live row (preserving the stored body bytes).
            var meta = new CacheEntry
            {
                Url = url,
                FetchedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MaxAgeSeconds = maxAge,
            };
            MapLibreCacheBridge.Touch(key, JsonConvert.SerializeObject(meta));
#else
            string metaPath = MetaPath(key);
            try
            {
                if (!File.Exists(metaPath)) return;
                var meta = JsonConvert.DeserializeObject<CacheEntry>(File.ReadAllText(metaPath));
                if (meta == null) return;
                meta.FetchedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                meta.MaxAgeSeconds = maxAge;
                File.WriteAllText(metaPath, JsonConvert.SerializeObject(meta));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TileDiskCache] Touch failed for {url}: {e.Message}");
            }
#endif
        }

        /// <summary>Wipe the entire cache directory.</summary>
        public static void Clear()
        {
            if (!Enabled) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            MapLibreCacheBridge.Clear();
#else
            try
            {
                string dir = ResolveCacheDir();
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.bin")) File.Delete(f);
                    foreach (var f in Directory.EnumerateFiles(dir, "*.meta")) File.Delete(f);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TileDiskCache] Clear failed: {e.Message}");
            }
#endif
        }

        /// <summary>
        /// Approximate total size of cached responses in bytes. On WebGL this
        /// returns 0 -- IndexedDB cursor scans are async; use
        /// <see cref="GetSizeAsync"/> there if you need an accurate number.
        /// </summary>
        public static long GetSize()
        {
            if (!Enabled) return 0;
#if UNITY_WEBGL && !UNITY_EDITOR
            return 0;
#else
            try
            {
                string dir = ResolveCacheDir();
                if (!Directory.Exists(dir)) return 0;
                long total = 0;
                foreach (var path in Directory.EnumerateFiles(dir, "*.bin"))
                    total += new FileInfo(path).Length;
                return total;
            }
            catch { return 0; }
#endif
        }

        /// <summary>
        /// Coroutine variant of <see cref="GetSize"/> that works on WebGL by
        /// awaiting an IndexedDB cursor scan via the bridge. <paramref name="onResult"/>
        /// receives the byte total once the scan completes.
        /// </summary>
        public static IEnumerator GetSizeAsync(Action<long> onResult)
        {
            if (!Enabled) { onResult?.Invoke(0); yield break; }
#if UNITY_WEBGL && !UNITY_EDITOR
            int reqId = MapLibreCacheBridge.BeginGetSize();
            if (reqId <= 0) { onResult?.Invoke(0); yield break; }
            int poll;
            while ((poll = MapLibreCacheBridge.PollGet(reqId)) == 0)
                yield return null;
            long size = (poll == 2) ? MapLibreCacheBridge.GetSizeResult(reqId) : 0;
            MapLibreCacheBridge.ReleaseRequest(reqId);
            onResult?.Invoke(size);
#else
            onResult?.Invoke(GetSize());
            yield break;
#endif
        }

        private static void EvictIfOverCap()
        {
            try
            {
                string dir = ResolveCacheDir();
                var files = new List<FileInfo>();
                long total = 0;
                foreach (var path in Directory.EnumerateFiles(dir, "*.bin"))
                {
                    var fi = new FileInfo(path);
                    files.Add(fi);
                    total += fi.Length;
                }
                if (total <= MaxBytes) return;

                // Evict oldest-accessed first until under cap.
                files.Sort((a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                foreach (var fi in files)
                {
                    if (total <= MaxBytes) break;
                    try
                    {
                        string metaPath = Path.ChangeExtension(fi.FullName, ".meta");
                        long size = fi.Length;
                        fi.Delete();
                        if (File.Exists(metaPath)) File.Delete(metaPath);
                        total -= size;
                    }
                    catch { /* ignore individual failures */ }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TileDiskCache] Eviction failed: {e.Message}");
            }
        }

        private static int ParseMaxAge(string cacheControl)
        {
            if (string.IsNullOrEmpty(cacheControl)) return -1;
            // Extract "max-age=NNN"
            const string token = "max-age=";
            int idx = cacheControl.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;
            int start = idx + token.Length;
            int end = start;
            while (end < cacheControl.Length && (char.IsDigit(cacheControl[end]) || cacheControl[end] == '-'))
                end++;
            if (end == start) return -1;
            return int.TryParse(cacheControl.Substring(start, end - start), out var seconds) ? seconds : -1;
        }
    }
}
