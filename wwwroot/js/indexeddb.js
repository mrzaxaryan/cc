// IndexedDB CRUD for agents, relays, and virtual filesystem
(function () {
    const DB_NAME = 'cc-agents';
    const DB_VERSION = 5;
    const AGENTS_STORE = 'agents';
    const RELAYS_STORE = 'relays';
    const DOWNLOADS_STORE = 'downloads';
    const DIRECTORIES_STORE = 'directories';
    const FILES_STORE = 'files';
    const NOTIFICATIONS_STORE = 'notifications';

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const db = req.result;
                if (!db.objectStoreNames.contains(AGENTS_STORE)) {
                    const store = db.createObjectStore(AGENTS_STORE, { keyPath: 'uuid' });
                    store.createIndex('agentId', 'agentId', { unique: false });
                    store.createIndex('lastSeen', 'lastSeen', { unique: false });
                }
                if (!db.objectStoreNames.contains(RELAYS_STORE)) {
                    db.createObjectStore(RELAYS_STORE, { keyPath: 'url' });
                }
                if (!db.objectStoreNames.contains(DOWNLOADS_STORE)) {
                    const dlStore = db.createObjectStore(DOWNLOADS_STORE, { keyPath: 'id', autoIncrement: true });
                    dlStore.createIndex('agentUuid', 'agentUuid', { unique: false });
                    dlStore.createIndex('status', 'status', { unique: false });
                }
                if (!db.objectStoreNames.contains(DIRECTORIES_STORE)) {
                    const dirStore = db.createObjectStore(DIRECTORIES_STORE, { keyPath: 'id' });
                    dirStore.createIndex('parentId', 'parentId', { unique: false });
                    dirStore.createIndex('agentUuid', 'agentUuid', { unique: false });
                    dirStore.createIndex('agentUuid_parentId', ['agentUuid', 'parentId'], { unique: false });
                }
                if (!db.objectStoreNames.contains(FILES_STORE)) {
                    const fileStore = db.createObjectStore(FILES_STORE, { keyPath: 'id' });
                    fileStore.createIndex('directoryId', 'directoryId', { unique: false });
                    fileStore.createIndex('agentUuid', 'agentUuid', { unique: false });
                    fileStore.createIndex('agentUuid_directoryId', ['agentUuid', 'directoryId'], { unique: false });
                }
                if (!db.objectStoreNames.contains(NOTIFICATIONS_STORE)) {
                    const notifStore = db.createObjectStore(NOTIFICATIONS_STORE, { keyPath: 'id', autoIncrement: true });
                    notifStore.createIndex('created', 'created', { unique: false });
                }
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function run(storeName, mode, fn) {
        return openDb().then(db => {
            const t = db.transaction(storeName, mode);
            const result = fn(t.objectStore(storeName));
            return new Promise((resolve, reject) => {
                t.oncomplete = () => resolve(result.result);
                t.onerror = () => reject(t.error);
            });
        });
    }

    window.ccAgentDb = {
        getAll: () => run(AGENTS_STORE, 'readonly', s => s.getAll()),
        get: uuid => run(AGENTS_STORE, 'readonly', s => s.get(uuid)),
        put: record => run(AGENTS_STORE, 'readwrite', s => s.put(record)),
        remove: uuid => run(AGENTS_STORE, 'readwrite', s => s.delete(uuid)),
        clear: () => run(AGENTS_STORE, 'readwrite', s => s.clear()),
        count: () => run(AGENTS_STORE, 'readonly', s => s.count())
    };

    window.ccRelayDb = {
        getAll: () => run(RELAYS_STORE, 'readonly', s => s.getAll()),
        get: url => run(RELAYS_STORE, 'readonly', s => s.get(url)),
        put: record => run(RELAYS_STORE, 'readwrite', s => s.put(record)),
        remove: url => run(RELAYS_STORE, 'readwrite', s => s.delete(url)),
        clear: () => run(RELAYS_STORE, 'readwrite', s => s.clear())
    };

    window.ccDownloadDb = {
        getAll: () => run(DOWNLOADS_STORE, 'readonly', s => s.getAll()),
        get: id => run(DOWNLOADS_STORE, 'readonly', s => s.get(id)),
        put: record => run(DOWNLOADS_STORE, 'readwrite', s => s.put(record)),
        remove: id => run(DOWNLOADS_STORE, 'readwrite', s => s.delete(id)),
        clear: () => run(DOWNLOADS_STORE, 'readwrite', s => s.clear()),
        getByAgent: agentUuid => {
            return openDb().then(db => {
                const t = db.transaction(DOWNLOADS_STORE, 'readonly');
                const store = t.objectStore(DOWNLOADS_STORE);
                const idx = store.index('agentUuid');
                const req = idx.getAll(agentUuid);
                return new Promise((resolve, reject) => {
                    t.oncomplete = () => resolve(req.result);
                    t.onerror = () => reject(t.error);
                });
            });
        }
    };

    function runIndex(storeName, mode, fn) {
        return openDb().then(db => {
            const t = db.transaction(storeName, mode);
            const result = fn(t.objectStore(storeName));
            return new Promise((resolve, reject) => {
                t.oncomplete = () => resolve(result.result);
                t.onerror = () => reject(t.error);
            });
        });
    }

    window.ccDirectoryDb = {
        getAll: () => run(DIRECTORIES_STORE, 'readonly', s => s.getAll()),
        get: id => run(DIRECTORIES_STORE, 'readonly', s => s.get(id)),
        put: record => run(DIRECTORIES_STORE, 'readwrite', s => s.put(record)),
        remove: id => run(DIRECTORIES_STORE, 'readwrite', s => s.delete(id)),
        clear: () => run(DIRECTORIES_STORE, 'readwrite', s => s.clear()),
        getByParent: (agentUuid, parentId) => {
            return openDb().then(db => {
                const t = db.transaction(DIRECTORIES_STORE, 'readonly');
                const store = t.objectStore(DIRECTORIES_STORE);
                const idx = store.index('agentUuid_parentId');
                const req = idx.getAll([agentUuid, parentId]);
                return new Promise((resolve, reject) => {
                    t.oncomplete = () => resolve(req.result);
                    t.onerror = () => reject(t.error);
                });
            });
        },
        getByAgent: agentUuid => {
            return runIndex(DIRECTORIES_STORE, 'readonly', s => s.index('agentUuid').getAll(agentUuid));
        },
        removeByAgent: agentUuid => {
            return openDb().then(db => {
                const t = db.transaction(DIRECTORIES_STORE, 'readwrite');
                const store = t.objectStore(DIRECTORIES_STORE);
                const idx = store.index('agentUuid');
                const req = idx.getAll(agentUuid);
                req.onsuccess = () => {
                    for (const rec of req.result) store.delete(rec.id);
                };
                return new Promise((resolve, reject) => {
                    t.oncomplete = () => resolve();
                    t.onerror = () => reject(t.error);
                });
            });
        }
    };

    window.ccFileDb = {
        getAll: () => run(FILES_STORE, 'readonly', s => s.getAll()),
        get: id => run(FILES_STORE, 'readonly', s => s.get(id)),
        put: record => run(FILES_STORE, 'readwrite', s => s.put(record)),
        remove: id => run(FILES_STORE, 'readwrite', s => s.delete(id)),
        clear: () => run(FILES_STORE, 'readwrite', s => s.clear()),
        getByDirectory: (agentUuid, directoryId) => {
            return openDb().then(db => {
                const t = db.transaction(FILES_STORE, 'readonly');
                const store = t.objectStore(FILES_STORE);
                const idx = store.index('agentUuid_directoryId');
                const req = idx.getAll([agentUuid, directoryId]);
                return new Promise((resolve, reject) => {
                    t.oncomplete = () => resolve(req.result);
                    t.onerror = () => reject(t.error);
                });
            });
        },
        getByAgent: agentUuid => {
            return runIndex(FILES_STORE, 'readonly', s => s.index('agentUuid').getAll(agentUuid));
        },
        removeByAgent: agentUuid => {
            return openDb().then(db => {
                const t = db.transaction(FILES_STORE, 'readwrite');
                const store = t.objectStore(FILES_STORE);
                const idx = store.index('agentUuid');
                const req = idx.getAll(agentUuid);
                req.onsuccess = () => {
                    for (const rec of req.result) store.delete(rec.id);
                };
                return new Promise((resolve, reject) => {
                    t.oncomplete = () => resolve();
                    t.onerror = () => reject(t.error);
                });
            });
        }
    };

    window.ccNotificationDb = {
        getAll: () => run(NOTIFICATIONS_STORE, 'readonly', s => s.getAll()),
        put: record => run(NOTIFICATIONS_STORE, 'readwrite', s => s.put(record)),
        remove: id => run(NOTIFICATIONS_STORE, 'readwrite', s => s.delete(id)),
        clear: () => run(NOTIFICATIONS_STORE, 'readwrite', s => s.clear()),
        count: () => run(NOTIFICATIONS_STORE, 'readonly', s => s.count())
    };

    window.ccDbInfo = {
        getTableStats: () => {
            return openDb().then(db => {
                const names = Array.from(db.objectStoreNames);
                const t = db.transaction(names, 'readonly');
                const promises = names.map(name => {
                    const store = t.objectStore(name);
                    const req = store.count();
                    return new Promise(resolve => {
                        req.onsuccess = () => resolve({ name, count: req.result });
                    });
                });
                return Promise.all(promises);
            });
        }
    };
})();
