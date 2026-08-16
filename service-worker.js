var CACHE = 'dashboards-v7';
var SHELL = [
  '/dashboards/',
  '/dashboards/index.html',
  '/dashboards/visuals.html',
  '/dashboards/manifest.json',
  '/dashboards/css/styles.css',
  '/dashboards/css/theme-vivid.css',
  '/dashboards/css/canvas-host.css',
  '/dashboards/css/executive-dashboard-suite.css',
  '/dashboards/js/api.js',
  '/dashboards/js/app.js',
  '/dashboards/js/charts.js',
  '/dashboards/js/dash-suite.js',
  '/dashboards/js/canvas-host.js',
  '/dashboards/js/theme-toggle.js',
  '/dashboards/js/visual-chat.js',
  '/dashboards/js/pull-to-refresh.js',
  '/dashboards/icons/icon-192.png',
  '/dashboards/icons/icon-512.png',
  '/dashboards/icons/apple-touch-icon.png'
];
self.addEventListener('install', function(e) {
  e.waitUntil(caches.open(CACHE).then(function(c) { return c.addAll(SHELL).catch(function() {}); }));
  self.skipWaiting();
});
self.addEventListener('activate', function(e) {
  e.waitUntil(caches.keys().then(function(names) {
    return Promise.all(names.filter(function(n) { return n !== CACHE; }).map(function(n) { return caches.delete(n); }));
  }));
  self.clients.claim();
});

// Pull-to-refresh: when the user pulls down at the top of the page,
// send a message to the client to reload data + re-render.
self.addEventListener('fetch', function(e) {
  var url = new URL(e.request.url);

  // JSON data files: always try network first (fresh data), fall back to cache
  if (url.pathname.indexOf('/data/') >= 0 || url.pathname.endsWith('.json')) {
    e.respondWith(fetch(e.request).then(function(r) { var c = r.clone(); caches.open(CACHE).then(function(cache) { cache.put(e.request, c); }); return r; }).catch(function() { return caches.match(e.request); }));
    return;
  }

  // Navigation requests (page loads): network-first so pull-to-refresh gets fresh HTML
  if (e.request.mode === 'navigate') {
    e.respondWith(
      fetch(e.request).then(function(r) {
        if (r.ok) { var c = r.clone(); caches.open(CACHE).then(function(cache) { cache.put(e.request, c); }); }
        return r;
      }).catch(function() {
        return caches.match(e.request).then(function(c) { return c || caches.match('/dashboards/index.html'); });
      })
    );
    return;
  }

  // Static assets: cache-first for speed, fall back to network
  e.respondWith(caches.match(e.request).then(function(c) {
    if (c) return c;
    return fetch(e.request).then(function(r) {
      if (r.ok && url.origin === self.location.origin) { var c2 = r.clone(); caches.open(CACHE).then(function(cache) { cache.put(e.request, c2); }); }
      return r;
    });
  }));
});

// Handle pull-to-refresh messages from the client
self.addEventListener('message', function(e) {
  if (e.data && e.data.type === 'SKIP_WAITING') self.skipWaiting();
  if (e.data && e.data.type === 'CLEAR_CACHE') {
    e.waitUntil(
      caches.keys().then(function(names) {
        return Promise.all(names.map(function(n) { return caches.delete(n); }));
      }).then(function() {
        e.source.postMessage({ type: 'CACHE_CLEARED' });
      })
    );
  }
});
