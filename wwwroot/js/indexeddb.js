// IndexedDB CRUD for agents and relays
const DB_NAME = 'cc-agents';
const DB_VERSION = 2;
const AGENTS_STORE = 'agents';
const RELAYS_STORE = 'relays';

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
