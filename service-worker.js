var CACHE = 'dashboards-v21';
var SHELL = [
  '/dashboards/',
  '/dashboards/index.html',
  '/dashboards/visuals.html',
  '/dashboards/css/universal-dashboard.css',
  '/dashboards/js/universal-dashboard.js',
  '/dashboards/js/theme-toggle.js',
  '/dashboards/js/exec-ai-brief.js'
];
self.addEventListener('install', function(e) {
  self.skipWaiting();
});
self.addEventListener('activate', function(e) {
  e.waitUntil(
    caches.keys().then(function(names) {
      return Promise.all(names.map(function(n) { return caches.delete(n); }));
    }).then(function() {
      return self.clients.claim();
    })
  );
});
self.addEventListener('fetch', function(e) {
  var url = new URL(e.request.url);
  if (url.origin !== self.location.origin) return;
  e.respondWith(
    fetch(e.request).then(function(r) {
      if (r.ok) {
        var c = r.clone();
        caches.open(CACHE).then(function(cache) { cache.put(e.request, c); });
      }
      return r;
    }).catch(function() {
      return caches.match(e.request);
    })
  );
});
self.addEventListener('message', function(e) {
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
