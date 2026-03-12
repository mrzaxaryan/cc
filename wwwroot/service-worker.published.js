// Blazor PWA service worker — uses the auto-generated asset manifest
// so cache versioning is automatic on every publish.
importScripts('service-worker-assets.js');

const CACHE_NAME = `c2-cache-${self.assetsManifest.version}`;

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME).then(cache =>
            cache.addAll(self.assetsManifest.assets.map(a => a.url))
        )
    );
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))
        )
    );
    self.clients.claim();
});

self.addEventListener('fetch', event => {
    if (event.request.method !== 'GET') return;

    const url = new URL(event.request.url);
    if (url.origin !== self.location.origin) return;

    // Network-first for manifest and icons so PWA updates pick up new assets
    const networkFirst = event.request.mode === 'navigate'
        || url.pathname.endsWith('.webmanifest')
        || url.pathname.match(/icon-.*\.png$/);

    if (networkFirst) {
        event.respondWith(
            fetch(event.request)
                .then(response => {
                    const clone = response.clone();
                    caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
                    return response;
                })
                .catch(() => caches.match(event.request).then(cached => cached || caches.match('index.html')))
        );
        return;
    }

    event.respondWith(
        caches.match(event.request).then(cached => {
            // For assets, serve from cache (fast), fall back to network
            return cached || fetch(event.request);
        })
    );
});
