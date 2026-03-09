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

    // List files/folders in a sub-path (relative to root) — sorting done in C#
    async listDirectory(subPath) {
        if (!_rootHandle) throw new Error('No directory selected');
        const dir = await navigateTo(subPath);
        const entries = [];
        for await (const [name, handle] of dir) {
            const entry = { name, kind: handle.kind };
            if (handle.kind === 'file') {
                try {
                    const file = await handle.getFile();
                    entry.size = file.size;
                    entry.lastModified = file.lastModified;
                } catch { }
            }
            entries.push(entry);
        }
        return entries;
    },

    // Write a file to cache (data is Uint8Array)
    async writeFile(subPath, fileName, data) {
        if (!_rootHandle) throw new Error('No directory selected');
        const dir = await navigateTo(subPath, true);
        const fileHandle = await dir.getFileHandle(fileName, { create: true });
        const writable = await fileHandle.createWritable();
        await writable.write(data);
        await writable.close();
        return true;
    },

    // Read a file from cache (returns Uint8Array as base64)
    async readFile(subPath, fileName) {
        if (!_rootHandle) throw new Error('No directory selected');
        const dir = await navigateTo(subPath);
        const fileHandle = await dir.getFileHandle(fileName);
        const file = await fileHandle.getFile();
        const buffer = await file.arrayBuffer();
        return btoa(String.fromCharCode(...new Uint8Array(buffer)));
    },

    // Check if file exists in cache
    async fileExists(subPath, fileName) {
        if (!_rootHandle) return false;
        try {
            const dir = await navigateTo(subPath);
            await dir.getFileHandle(fileName);
            return true;
        } catch {
            return false;
        }
    },

    // Delete a file from cache
    async deleteFile(subPath, fileName) {
        if (!_rootHandle) throw new Error('No directory selected');
        const dir = await navigateTo(subPath);
        await dir.removeEntry(fileName);
        return true;
    },

    // Delete a directory recursively
    async deleteDirectory(subPath, dirName) {
        if (!_rootHandle) throw new Error('No directory selected');
        const dir = await navigateTo(subPath);
        await dir.removeEntry(dirName, { recursive: true });
        return true;
    },

    // Create a subdirectory
    async createDirectory(subPath, dirName) {
        if (!_rootHandle) throw new Error('No directory selected');
        const dir = await navigateTo(subPath, true);
        await dir.getDirectoryHandle(dirName, { create: true });
        return true;
    },

    // Get total cache size (recursive)
    async getCacheSize(subPath) {
        if (!_rootHandle) return 0;
        const dir = await navigateTo(subPath || '');
        return await calcDirSize(dir);
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

// Navigate to a sub-path from root, optionally creating dirs
async function navigateTo(subPath, create = false) {
    let dir = _rootHandle;
    if (!subPath || subPath === '' || subPath === '/') return dir;
    const parts = subPath.split('/').filter(p => p.length > 0);
    for (const part of parts) {
        dir = await dir.getDirectoryHandle(part, { create });
    }
    return dir;
}

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
