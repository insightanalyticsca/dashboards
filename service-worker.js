var CACHE = 'dashboards-v20';
var SHELL = [
  '/dashboards/',
  '/dashboards/index.html',
  '/dashboards/visuals.html',
  '/dashboards/manifest.json',
  '/dashboards/css/styles.css',
  '/dashboards/css/theme-vivid.css',
  '/dashboards/css/canvas-host.css',
  '/dashboards/css/executive-dashboard-suite.css',
  '/dashboards/css/universal-dashboard.css',
  '/dashboards/js/api.js',
  '/dashboards/js/app.js',
  '/dashboards/js/charts.js',
  '/dashboards/js/dash-suite.js',
  '/dashboards/js/canvas-host.js',
  '/dashboards/js/theme-toggle.js',
  '/dashboards/js/visual-chat.js',
  '/dashboards/js/pull-to-refresh.js',
  '/dashboards/js/universal-dashboard.js',
  '/dashboards/js/exec-ai-brief.js',
  '/dashboards/icons/icon-192.png',
  '/dashboards/icons/icon-512.png',
  '/dashboards/icons/apple-touch-icon.png'
];
self.addEventListener('install', function(e) {
  e.waitUntil(caches.open(CACHE).then(function(c) { return c.addAll(SHELL).catch(function() {}); }));
  self.skipWaiting();
});
self.addEventListener('activate', function(e) {
  // Delete ALL old caches — not just ones that don't match CACHE name.
  // This is more aggressive but ensures stale assets from v18/v19 are gone.
  e.waitUntil(caches.keys().then(function(names) {
    return Promise.all(names.map(function(n) { return caches.delete(n); }));
  }).then(function() {
    // Pre-cache the fresh shell after clearing
    return caches.open(CACHE).then(function(c) { return c.addAll(SHELL).catch(function() {}); });
  }));
  self.clients.claim();
});

// Pull-to-refresh: when the user pulls down at the top of the page,
// send a message to the client to reload data + re-render.
self.addEventListener('fetch', function(e) {
  var url = new URL(e.request.url);

  // Only handle same-origin requests
  if (url.origin !== self.location.origin) return;

  // JSON data files: network-first (fresh data), fall back to cache
  if (url.pathname.indexOf('/data/') >= 0 || url.pathname.endsWith('.json')) {
    e.respondWith(
      fetch(e.request)
        .then(function(r) { var c = r.clone(); caches.open(CACHE).then(function(cache) { cache.put(e.request, c); }); return r; })
        .catch(function() { return caches.match(e.request); })
    );
    return;
  }

  // ALL OTHER REQUESTS (HTML, CSS, JS, images): NETWORK-FIRST
  // Always try network first. Only fall back to cache if offline.
  // This ensures users always get the latest CSS/JS after a deploy.
  // The old cache-first strategy was serving stale CSS that caused
  // the "ugly gray topbar" bug in PWA mode.
  e.respondWith(
    fetch(e.request)
      .then(function(r) {
        // Cache the fresh response for offline use
        if (r.ok) {
          var c = r.clone();
          caches.open(CACHE).then(function(cache) { cache.put(e.request, c); });
        }
        return r;
      })
      .catch(function() {
        // Network failed — fall back to cache
        return caches.match(e.request).then(function(c) {
          return c || caches.match('/dashboards/index.html');
        });
      })
  );
});

// Handle pull-to-refresh messages from the client
self.addEventListener('message', function(e) {
  if (e.data && e.data.type === 'SKIP_WAITING') self.skipWaiting();
  if (e.data && e.data.type === 'CLEAR_CACHE') {
    e.waitUntil(
      caches.keys().then(function(names) {
        return Promise.all(names.map(function(n) { return caches.delete(n); }));
      }).then(function() {
        // Re-populate shell after clearing
        return caches.open(CACHE).then(function(c) { return c.addAll(SHELL).catch(function() {}); });
      }).then(function() {
        e.source.postMessage({ type: 'CACHE_CLEARED' });
      })
    );
  }
});
