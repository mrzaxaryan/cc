// Raw IndexedDB CRUD — all business logic lives in C# AgentStore
const DB_NAME = 'cc-agents';
const DB_VERSION = 1;
const STORE_NAME = 'agents';

function openDb() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, DB_VERSION);
        req.onupgradeneeded = () => {
            const db = req.result;
            if (!db.objectStoreNames.contains(STORE_NAME)) {
                const store = db.createObjectStore(STORE_NAME, { keyPath: 'uuid' });
                store.createIndex('agentId', 'agentId', { unique: false });
                store.createIndex('lastSeen', 'lastSeen', { unique: false });
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

function run(mode, fn) {
    return openDb().then(db => {
        const t = db.transaction(STORE_NAME, mode);
        const result = fn(t.objectStore(STORE_NAME));
        return new Promise((resolve, reject) => {
            t.oncomplete = () => resolve(result.result);
            t.onerror = () => reject(t.error);
        });
    });
}

window.ccAgentDb = {
    getAll: () => run('readonly', s => s.getAll()),
    get: uuid => run('readonly', s => s.get(uuid)),
    put: record => run('readwrite', s => s.put(record)),
    remove: uuid => run('readwrite', s => s.delete(uuid)),
    clear: () => run('readwrite', s => s.clear()),
    count: () => run('readonly', s => s.count())
};
