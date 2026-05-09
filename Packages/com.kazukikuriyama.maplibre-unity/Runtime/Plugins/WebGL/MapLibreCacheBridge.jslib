// Async IndexedDB cache for MapLibre tile/sprite/glyph responses on WebGL.
//
// Why this exists: Application.persistentDataPath on WebGL is IDBFS-backed and
// every File.* call is synchronous. Loading 50 tiles × dozens of sync writes
// blocks the main thread on IndexedDB sync, so the C# TileDiskCache is disabled
// on WebGL by default. This bridge replaces that path with an honest async
// IndexedDB integration: C# kicks off requests, polls each frame in a coroutine,
// and reads results when ready. The browser's HTTP cache still works in
// parallel; this bridge persists across sessions independently of any
// Cache-Control headers the tile server may or may not send.
//
// Storage layout (one IndexedDB object store, key = sha1(url) computed in C#
// to match the on-disk cache key on other platforms):
//   { key, url, bytes: Uint8Array, etag, cacheControl,
//     fetchedAt: unix-seconds, maxAge: seconds, byteLength,
//     lastAccess: unix-seconds }
//
// Eviction is approximate: when total bytes exceeds a soft cap, oldest-by-
// lastAccess records are deleted until under cap. Eviction runs at most once
// per Put to keep cost amortised.

mergeInto(LibraryManager.library, {
    $MLCache: {
        DB_NAME: 'MapLibreCache',
        STORE: 'responses',
        VERSION: 1,
        db: null,
        // Promise resolved when the IndexedDB open() completes (or rejects).
        // We never await it from C# directly; pending Get/Put requests are
        // queued on this promise so they fire in order once the DB is ready.
        opening: null,
        // Disable flag: flips to true if IndexedDB is unavailable (private
        // browsing in Firefox, blocked storage, etc.). Subsequent calls are
        // no-ops so the C# layer transparently falls back to network-only.
        unavailable: false,
        nextRequestId: 1,
        // requestId -> { state: 'pending'|'miss'|'hit', bytes, meta }
        // meta is the JSON string serialized by C# (see CacheEntry).
        requests: {},
        maxBytes: 100 * 1024 * 1024,
        // Track approximate total. Re-summed on first open from a cursor scan.
        totalBytes: 0,
        bytesKnown: false,
        // Throttle eviction: only rescan once per N puts.
        putsSinceEvict: 0,
        evictPutInterval: 50,

        ensureOpen: function () {
            if (MLCache.opening) return MLCache.opening;
            if (MLCache.unavailable) return Promise.reject(new Error('idb-unavailable'));
            if (typeof indexedDB === 'undefined') {
                MLCache.unavailable = true;
                return Promise.reject(new Error('idb-unavailable'));
            }
            MLCache.opening = new Promise(function (resolve, reject) {
                var req;
                try {
                    req = indexedDB.open(MLCache.DB_NAME, MLCache.VERSION);
                } catch (e) {
                    MLCache.unavailable = true;
                    reject(e);
                    return;
                }
                req.onupgradeneeded = function (ev) {
                    var db = ev.target.result;
                    if (!db.objectStoreNames.contains(MLCache.STORE)) {
                        var os = db.createObjectStore(MLCache.STORE, { keyPath: 'key' });
                        os.createIndex('lastAccess', 'lastAccess', { unique: false });
                    }
                };
                req.onsuccess = function () {
                    MLCache.db = req.result;
                    MLCache.db.onversionchange = function () {
                        try { MLCache.db.close(); } catch (_) { }
                        MLCache.db = null;
                        MLCache.opening = null;
                    };
                    resolve(MLCache.db);
                };
                req.onerror = function () {
                    MLCache.unavailable = true;
                    reject(req.error || new Error('idb-open-failed'));
                };
                req.onblocked = function () {
                    // Another tab holds an older version. The user's IDB is
                    // alive, just in use; mark unavailable so we don't hang
                    // forever waiting for that tab to close.
                    MLCache.unavailable = true;
                    reject(new Error('idb-blocked'));
                };
            });
            return MLCache.opening;
        },

        finalizeMiss: function (id) {
            if (!MLCache.requests[id]) return;
            MLCache.requests[id].state = 'miss';
        },

        // Trigger a recursive scan that sums byteLength on the first eviction
        // pass so subsequent put calls can update totalBytes incrementally.
        recomputeTotalBytes: function (db) {
            return new Promise(function (resolve) {
                var tx = db.transaction(MLCache.STORE, 'readonly');
                var store = tx.objectStore(MLCache.STORE);
                var total = 0;
                var cursorReq = store.openCursor();
                cursorReq.onsuccess = function (ev) {
                    var cur = ev.target.result;
                    if (cur) {
                        total += (cur.value.byteLength || 0);
                        cur.continue();
                    } else {
                        MLCache.totalBytes = total;
                        MLCache.bytesKnown = true;
                        resolve(total);
                    }
                };
                cursorReq.onerror = function () { resolve(0); };
            });
        },

        evictIfOverCap: function (db) {
            if (MLCache.maxBytes <= 0) return Promise.resolve();
            if (MLCache.totalBytes <= MLCache.maxBytes) return Promise.resolve();
            return new Promise(function (resolve) {
                var tx = db.transaction(MLCache.STORE, 'readwrite');
                var store = tx.objectStore(MLCache.STORE);
                var idx = store.index('lastAccess');
                var cursorReq = idx.openCursor(); // ascending = oldest first
                cursorReq.onsuccess = function (ev) {
                    var cur = ev.target.result;
                    if (!cur) { resolve(); return; }
                    if (MLCache.totalBytes <= MLCache.maxBytes) { resolve(); return; }
                    var size = cur.value.byteLength || 0;
                    var delReq = cur.delete();
                    delReq.onsuccess = function () {
                        MLCache.totalBytes -= size;
                        cur.continue();
                    };
                    delReq.onerror = function () { cur.continue(); };
                };
                cursorReq.onerror = function () { resolve(); };
                tx.onabort = function () { resolve(); };
            });
        },
    },

    // ---- exported API ----

    MapLibreCache_Init__deps: ['$MLCache'],
    MapLibreCache_Init: function (maxBytes) {
        // maxBytes <= 0 → unbounded (still subject to IDB quota).
        MLCache.maxBytes = (maxBytes && maxBytes > 0) ? maxBytes : 0;
        MLCache.ensureOpen().catch(function () { /* logged via Available poll */ });
    },

    MapLibreCache_IsAvailable__deps: ['$MLCache'],
    MapLibreCache_IsAvailable: function () {
        return MLCache.unavailable ? 0 : 1;
    },

    MapLibreCache_BeginGet__deps: ['$MLCache'],
    MapLibreCache_BeginGet: function (keyPtr) {
        var key = UTF8ToString(keyPtr);
        var id = MLCache.nextRequestId++;
        MLCache.requests[id] = { state: 'pending' };
        MLCache.ensureOpen().then(function (db) {
            var tx = db.transaction(MLCache.STORE, 'readwrite');
            var store = tx.objectStore(MLCache.STORE);
            var getReq = store.get(key);
            getReq.onsuccess = function () {
                var rec = getReq.result;
                if (!rec) { MLCache.finalizeMiss(id); return; }
                // Touch lastAccess so eviction prefers older-touched entries.
                // Failure here is non-fatal; we still serve the cached body.
                rec.lastAccess = Math.floor(Date.now() / 1000);
                try { store.put(rec); } catch (_) { }

                if (!MLCache.requests[id]) return; // C# released early
                MLCache.requests[id].bytes = rec.bytes;
                MLCache.requests[id].meta = rec.meta || '';
                MLCache.requests[id].state = 'hit';
            };
            getReq.onerror = function () { MLCache.finalizeMiss(id); };
            tx.onabort = function () { MLCache.finalizeMiss(id); };
        }).catch(function () { MLCache.finalizeMiss(id); });
        return id;
    },

    MapLibreCache_PollGet__deps: ['$MLCache'],
    MapLibreCache_PollGet: function (id) {
        var req = MLCache.requests[id];
        if (!req) return -1;             // unknown / released
        if (req.state === 'pending') return 0;
        if (req.state === 'miss') return 1;
        return 2;                        // hit
    },

    MapLibreCache_GetResultByteLength__deps: ['$MLCache'],
    MapLibreCache_GetResultByteLength: function (id) {
        var req = MLCache.requests[id];
        return (req && req.bytes) ? req.bytes.length : 0;
    },

    MapLibreCache_CopyResultBytes__deps: ['$MLCache'],
    MapLibreCache_CopyResultBytes: function (id, dstPtr) {
        var req = MLCache.requests[id];
        if (!req || !req.bytes) return;
        HEAPU8.set(req.bytes, dstPtr);
    },

    MapLibreCache_GetResultMetaJsonLength__deps: ['$MLCache'],
    MapLibreCache_GetResultMetaJsonLength: function (id) {
        var req = MLCache.requests[id];
        if (!req || !req.meta) return 0;
        return lengthBytesUTF8(req.meta);
    },

    MapLibreCache_CopyResultMetaJson__deps: ['$MLCache'],
    MapLibreCache_CopyResultMetaJson: function (id, dstPtr, dstCapacity) {
        var req = MLCache.requests[id];
        if (!req || !req.meta) return;
        // +1 for trailing NUL inside stringToUTF8.
        stringToUTF8(req.meta, dstPtr, dstCapacity + 1);
    },

    MapLibreCache_ReleaseRequest__deps: ['$MLCache'],
    MapLibreCache_ReleaseRequest: function (id) {
        delete MLCache.requests[id];
    },

    MapLibreCache_Put__deps: ['$MLCache'],
    MapLibreCache_Put: function (keyPtr, urlPtr, bytesPtr, byteLen, metaPtr) {
        if (byteLen <= 0) return;
        var key = UTF8ToString(keyPtr);
        var url = UTF8ToString(urlPtr);
        var meta = UTF8ToString(metaPtr);
        // Copy out of HEAPU8 because IndexedDB needs a stable, owned buffer
        // (the WASM heap can move on growth and HEAPU8 subarrays would alias).
        var bytes = new Uint8Array(byteLen);
        bytes.set(HEAPU8.subarray(bytesPtr, bytesPtr + byteLen));
        var record = {
            key: key,
            url: url,
            bytes: bytes,
            meta: meta,
            byteLength: byteLen,
            lastAccess: Math.floor(Date.now() / 1000),
        };
        MLCache.ensureOpen().then(function (db) {
            // First put after open: cold-scan total bytes so eviction has a
            // baseline. The cost is one cursor walk per session.
            var pre = MLCache.bytesKnown ? Promise.resolve() : MLCache.recomputeTotalBytes(db);
            pre.then(function () {
                var tx = db.transaction(MLCache.STORE, 'readwrite');
                var store = tx.objectStore(MLCache.STORE);
                // Subtract previous size if overwriting so totals stay accurate.
                var prevReq = store.get(key);
                prevReq.onsuccess = function () {
                    if (prevReq.result) {
                        MLCache.totalBytes -= (prevReq.result.byteLength || 0);
                    }
                    var putReq = store.put(record);
                    putReq.onsuccess = function () {
                        MLCache.totalBytes += byteLen;
                        MLCache.putsSinceEvict++;
                        if (MLCache.putsSinceEvict >= MLCache.evictPutInterval) {
                            MLCache.putsSinceEvict = 0;
                            MLCache.evictIfOverCap(db);
                        }
                    };
                };
            });
        }).catch(function () { /* swallow: caller already has bytes in memory */ });
    },

    MapLibreCache_Touch__deps: ['$MLCache'],
    MapLibreCache_Touch: function (keyPtr, metaPtr) {
        var key = UTF8ToString(keyPtr);
        var meta = UTF8ToString(metaPtr);
        MLCache.ensureOpen().then(function (db) {
            var tx = db.transaction(MLCache.STORE, 'readwrite');
            var store = tx.objectStore(MLCache.STORE);
            var getReq = store.get(key);
            getReq.onsuccess = function () {
                var rec = getReq.result;
                if (!rec) return;
                rec.meta = meta;
                rec.lastAccess = Math.floor(Date.now() / 1000);
                store.put(rec);
            };
        }).catch(function () { });
    },

    MapLibreCache_Clear__deps: ['$MLCache'],
    MapLibreCache_Clear: function () {
        MLCache.ensureOpen().then(function (db) {
            var tx = db.transaction(MLCache.STORE, 'readwrite');
            tx.objectStore(MLCache.STORE).clear();
            MLCache.totalBytes = 0;
            MLCache.bytesKnown = true;
        }).catch(function () { });
    },

    MapLibreCache_BeginGetSize__deps: ['$MLCache'],
    MapLibreCache_BeginGetSize: function () {
        var id = MLCache.nextRequestId++;
        MLCache.requests[id] = { state: 'pending' };
        MLCache.ensureOpen().then(function (db) {
            return MLCache.recomputeTotalBytes(db);
        }).then(function (total) {
            if (!MLCache.requests[id]) return;
            MLCache.requests[id].size = total;
            MLCache.requests[id].state = 'hit';
        }).catch(function () { MLCache.finalizeMiss(id); });
        return id;
    },

    MapLibreCache_GetSizeResult__deps: ['$MLCache'],
    MapLibreCache_GetSizeResult: function (id) {
        var req = MLCache.requests[id];
        if (!req || req.state !== 'hit') return -1;
        return req.size || 0;
    },
});
