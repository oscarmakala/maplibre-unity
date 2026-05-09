#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace MapLibre.Unity.Source
{
    /// <summary>
    /// Thin wrapper around the WebGL .jslib that backs <see cref="TileDiskCache"/>
    /// with IndexedDB. The native side is async (IndexedDB transactions resolve
    /// on microtask boundaries), so callers issue a request, poll until the
    /// JS side flips the slot's state, then copy bytes/metadata out.
    ///
    /// Off WebGL the methods compile to no-ops returning sentinel values, which
    /// lets <c>TileDiskCache</c> branch on platform without ifdef noise at every
    /// call site.
    ///
    /// Slot lifecycle:
    ///   <c>BeginGet</c> / <c>BeginGetSize</c> allocate a slot id.
    ///   <c>Poll</c> returns 0 (pending), 1 (miss), 2 (hit), or -1 (released/unknown).
    ///   <c>CopyResultBytes</c> / <c>CopyResultMetaJson</c> are valid only on hit.
    ///   The caller MUST <c>ReleaseRequest</c> exactly once -- otherwise the JS
    ///   side leaks the entry's reference to the byte buffer.
    /// </summary>
    internal static class MapLibreCacheBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void MapLibreCache_Init(long maxBytes);

        [DllImport("__Internal")]
        private static extern int MapLibreCache_IsAvailable();

        [DllImport("__Internal")]
        private static extern int MapLibreCache_BeginGet(string key);

        [DllImport("__Internal")]
        private static extern int MapLibreCache_PollGet(int id);

        [DllImport("__Internal")]
        private static extern int MapLibreCache_GetResultByteLength(int id);

        [DllImport("__Internal")]
        private static extern void MapLibreCache_CopyResultBytes(int id, byte[] dst);

        [DllImport("__Internal")]
        private static extern int MapLibreCache_GetResultMetaJsonLength(int id);

        [DllImport("__Internal")]
        private static extern void MapLibreCache_CopyResultMetaJson(int id, byte[] dst, int dstCapacity);

        [DllImport("__Internal")]
        private static extern void MapLibreCache_ReleaseRequest(int id);

        [DllImport("__Internal")]
        private static extern void MapLibreCache_Put(string key, string url, byte[] bytes, int byteLen, string metaJson);

        [DllImport("__Internal")]
        private static extern void MapLibreCache_Touch(string key, string metaJson);

        [DllImport("__Internal")]
        private static extern void MapLibreCache_Clear();

        [DllImport("__Internal")]
        private static extern int MapLibreCache_BeginGetSize();

        [DllImport("__Internal")]
        private static extern long MapLibreCache_GetSizeResult(int id);

        public static bool IsSupported => true;

        public static void Init(long maxBytes) => MapLibreCache_Init(maxBytes);

        // The DB open happens lazily in JS; this getter only flips to false
        // once IndexedDB has actually rejected (private mode, blocked, etc.).
        public static bool IsAvailable => MapLibreCache_IsAvailable() != 0;

        public static int BeginGet(string key) => MapLibreCache_BeginGet(key);
        public static int PollGet(int id) => MapLibreCache_PollGet(id);
        public static int GetResultByteLength(int id) => MapLibreCache_GetResultByteLength(id);

        public static byte[] CopyResultBytes(int id)
        {
            int len = MapLibreCache_GetResultByteLength(id);
            if (len <= 0) return null;
            var buf = new byte[len];
            MapLibreCache_CopyResultBytes(id, buf);
            return buf;
        }

        public static string CopyResultMetaJson(int id)
        {
            int len = MapLibreCache_GetResultMetaJsonLength(id);
            if (len <= 0) return null;
            var buf = new byte[len];
            MapLibreCache_CopyResultMetaJson(id, buf, len);
            return System.Text.Encoding.UTF8.GetString(buf);
        }

        public static void ReleaseRequest(int id) => MapLibreCache_ReleaseRequest(id);

        public static void Put(string key, string url, byte[] bytes, string metaJson)
        {
            if (bytes == null || bytes.Length == 0) return;
            MapLibreCache_Put(key, url ?? string.Empty, bytes, bytes.Length, metaJson ?? string.Empty);
        }

        public static void Touch(string key, string metaJson)
            => MapLibreCache_Touch(key, metaJson ?? string.Empty);

        public static void Clear() => MapLibreCache_Clear();

        public static int BeginGetSize() => MapLibreCache_BeginGetSize();
        public static long GetSizeResult(int id) => MapLibreCache_GetSizeResult(id);
#else
        // Non-WebGL stubs. TileDiskCache uses File.* directly on these platforms,
        // so the bridge is never consulted; these constants just keep the call
        // sites compiling without ifdefs everywhere.
        public static bool IsSupported => false;
        public static bool IsAvailable => false;
        public static void Init(long maxBytes) { }
        public static int BeginGet(string key) => -1;
        public static int PollGet(int id) => -1;
        public static int GetResultByteLength(int id) => 0;
        public static byte[] CopyResultBytes(int id) => null;
        public static string CopyResultMetaJson(int id) => null;
        public static void ReleaseRequest(int id) { }
        public static void Put(string key, string url, byte[] bytes, string metaJson) { }
        public static void Touch(string key, string metaJson) { }
        public static void Clear() { }
        public static int BeginGetSize() => -1;
        public static long GetSizeResult(int id) => 0;
#endif
    }
}
