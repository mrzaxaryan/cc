// Caution! Be sure you understand the caveats before publishing an application with
// temporary caching enabled. See https://aka.ms/blazor-offline/pwas for details.

// Incrementing CACHE_VERSION will force a cache refresh for all clients.
const CACHE_VERSION = 1;
const CACHE_NAME = `cc-cache-v${CACHE_VERSION}`;

// List of URLs to cache during installation.
const PRECACHE_URLS = [];

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME).then(cache => cache.addAll(PRECACHE_URLS))
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

    // Don't cache API calls or external resources
    if (url.origin !== self.location.origin) return;

    event.respondWith(
        caches.open(CACHE_NAME).then(async cache => {
            const cachedResponse = await cache.match(event.request);

            // Network-first strategy: try network, fallback to cache
            try {
                const networkResponse = await fetch(event.request);
                if (networkResponse.ok) {
                    cache.put(event.request, networkResponse.clone());
                }
                return networkResponse;
            } catch {
                return cachedResponse || new Response('Offline', { status: 503 });
            }
        })
    );
});
