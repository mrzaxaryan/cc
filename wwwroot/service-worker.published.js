// Blazor PWA service worker — uses the auto-generated asset manifest
// so cache versioning is automatic on every publish.
importScripts('service-worker-assets.js');

const CACHE_NAME = `cc-cache-${self.assetsManifest.version}`;

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

    event.respondWith(
        caches.match(event.request).then(cached => {
            // For navigation requests, always try network first
            if (event.request.mode === 'navigate') {
                return fetch(event.request).catch(() => cached || caches.match('index.html'));
            }
            // For assets, serve from cache (fast), fall back to network
            return cached || fetch(event.request);
        })
    );
});
