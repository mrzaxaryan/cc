// File System Access API + IndexedDB handle persistence
const DB_NAME = 'cc-cache';
const STORE_NAME = 'handles';
const ROOT_KEY = 'root-dir';

function openDb() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = () => req.result.createObjectStore(STORE_NAME);
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

async function saveHandle(key, handle) {
    const db = await openDb();
    const tx = db.transaction(STORE_NAME, 'readwrite');
    tx.objectStore(STORE_NAME).put(handle, key);
    return new Promise((resolve, reject) => {
        tx.oncomplete = resolve;
        tx.onerror = () => reject(tx.error);
    });
}

async function loadHandle(key) {
    const db = await openDb();
    const tx = db.transaction(STORE_NAME, 'readonly');
    const req = tx.objectStore(STORE_NAME).get(key);
    return new Promise((resolve, reject) => {
        req.onsuccess = () => resolve(req.result || null);
        req.onerror = () => reject(req.error);
    });
}

let _rootHandle = null;

window.ccFileSystem = {
    // Check if File System Access API is supported
    isSupported() {
        return typeof window.showDirectoryPicker === 'function';
    },

    // Check if we already have a persisted directory handle
    async hasPersistedHandle() {
        const handle = await loadHandle(ROOT_KEY);
        return handle !== null;
    },

    // Try to restore persisted handle (returns true if permission already granted)
    // Does NOT call requestPermission — that requires a user gesture.
    async restoreHandle() {
        const handle = await loadHandle(ROOT_KEY);
        if (!handle) return false;
        const perm = await handle.queryPermission({ mode: 'readwrite' });
        if (perm === 'granted') {
            _rootHandle = handle;
            return true;
        }
        // Permission not granted — needs user gesture to re-request
        return false;
    },

    // Re-request permission on a persisted handle (must be called from user gesture)
    async reRequestPermission() {
        const handle = await loadHandle(ROOT_KEY);
        if (!handle) return false;
        const perm = await handle.requestPermission({ mode: 'readwrite' });
        if (perm === 'granted') {
            _rootHandle = handle;
            return true;
        }
        return false;
    },

    // Show directory picker and persist the handle
    async pickDirectory() {
        try {
            const handle = await window.showDirectoryPicker({ mode: 'readwrite' });
            _rootHandle = handle;
            await saveHandle(ROOT_KEY, handle);
            return handle.name;
        } catch (e) {
            if (e.name === 'AbortError') return null; // user cancelled
            throw e;
        }
    },

    // Get root directory name
    getRootName() {
        return _rootHandle ? _rootHandle.name : null;
    },

    // List blobs in .fs directory (for cache size / maintenance)
    async listBlobs() {
        if (!_rootHandle) throw new Error('No directory selected');
        try {
            const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: false });
            const entries = [];
            for await (const [name, handle] of fsDir) {
                if (handle.kind === 'file') {
                    try {
                        const file = await handle.getFile();
                        entries.push({ name, size: file.size });
                    } catch { }
                }
            }
            return entries;
        } catch {
            return [];
        }
    },

    // --- Flat blob storage: all files stored as .fs/{fileId} ---

    // Write a blob by GUID into .fs/{fileId}
    async writeBlobById(fileId, data) {
        if (!_rootHandle) throw new Error('No directory selected');
        const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: true });
        const fileHandle = await fsDir.getFileHandle(fileId, { create: true });
        const writable = await fileHandle.createWritable();
        await writable.write(data);
        await writable.close();
        return true;
    },

    // Streaming write to .fs/{fileId}
    _activeWritable: null,
    _activeFileId: null,

    async beginWriteById(fileId) {
        if (!_rootHandle) throw new Error('No directory selected');
        const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: true });
        const fileHandle = await fsDir.getFileHandle(fileId, { create: true });
        this._activeWritable = await fileHandle.createWritable();
        this._activeFileId = fileId;
        return true;
    },

    // Resume writing: keep existing data and seek to offset
    async beginResumeWriteById(fileId, offset) {
        if (!_rootHandle) throw new Error('No directory selected');
        const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: true });
        const fileHandle = await fsDir.getFileHandle(fileId, { create: true });
        this._activeWritable = await fileHandle.createWritable({ keepExistingData: true });
        await this._activeWritable.seek(offset);
        this._activeFileId = fileId;
        return true;
    },

    async writeChunk(data) {
        if (!this._activeWritable) throw new Error('No active writable stream');
        await this._activeWritable.write(data);
        return true;
    },

    async endWrite() {
        if (!this._activeWritable) return false;
        await this._activeWritable.close();
        this._activeWritable = null;
        this._activeFileId = null;
        return true;
    },

    async abortWrite() {
        if (!this._activeWritable) return false;
        try { await this._activeWritable.abort(); } catch { }
        const fileId = this._activeFileId;
        this._activeWritable = null;
        this._activeFileId = null;
        // Clean up partial file
        if (fileId) {
            try {
                const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: false });
                await fsDir.removeEntry(fileId);
            } catch { }
        }
        return true;
    },

    // Read a blob by GUID (returns base64)
    async readBlobById(fileId) {
        if (!_rootHandle) throw new Error('No directory selected');
        const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: false });
        const fileHandle = await fsDir.getFileHandle(fileId);
        const file = await fileHandle.getFile();
        const buffer = await file.arrayBuffer();
        return btoa(String.fromCharCode(...new Uint8Array(buffer)));
    },

    // Check if a blob exists by GUID
    async blobExists(fileId) {
        if (!_rootHandle) return false;
        try {
            const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: false });
            await fsDir.getFileHandle(fileId);
            return true;
        } catch {
            return false;
        }
    },

    // Delete a blob by GUID
    async deleteBlobById(fileId) {
        if (!_rootHandle) throw new Error('No directory selected');
        const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: false });
        await fsDir.removeEntry(fileId);
        return true;
    },

    // Get total size of .fs directory
    async getCacheSize() {
        if (!_rootHandle) return 0;
        try {
            const fsDir = await _rootHandle.getDirectoryHandle('.fs', { create: false });
            return await calcDirSize(fsDir);
        } catch {
            return 0;
        }
    },

    // Clear the persisted handle (reset)
    async clearHandle() {
        _rootHandle = null;
        const db = await openDb();
        const tx = db.transaction(STORE_NAME, 'readwrite');
        tx.objectStore(STORE_NAME).delete(ROOT_KEY);
        return new Promise((resolve, reject) => {
            tx.oncomplete = resolve;
            tx.onerror = () => reject(tx.error);
        });
    }
};

async function calcDirSize(dirHandle) {
    let total = 0;
    for await (const [, handle] of dirHandle) {
        if (handle.kind === 'file') {
            try {
                const file = await handle.getFile();
                total += file.size;
            } catch { }
        } else {
            total += await calcDirSize(handle);
        }
    }
    return total;
}
