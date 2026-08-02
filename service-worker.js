var CACHE = 'dashboards-v1';
var SHELL = [
  '/dashboards/',
  '/dashboards/index.html',
  '/dashboards/visuals.html',
  '/dashboards/manifest.json',
  '/dashboards/css/styles.css',
  '/dashboards/css/theme-vivid.css',
  '/dashboards/css/canvas-host.css',
  '/dashboards/js/api.js',
  '/dashboards/js/app.js',
  '/dashboards/js/charts.js',
  '/dashboards/js/dash-suite.js',
  '/dashboards/js/canvas-host.js',
  '/dashboards/js/theme-toggle.js',
  '/dashboards/js/visual-chat.js',
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
self.addEventListener('fetch', function(e) {
  var url = new URL(e.request.url);
  if (url.pathname.indexOf('/data/') >= 0 || url.pathname.endsWith('.json')) {
    e.respondWith(fetch(e.request).then(function(r) { var c = r.clone(); caches.open(CACHE).then(function(cache) { cache.put(e.request, c); }); return r; }).catch(function() { return caches.match(e.request); }));
    return;
  }
  e.respondWith(caches.match(e.request).then(function(c) {
    if (c) return c;
    return fetch(e.request).then(function(r) {
      if (r.ok && url.origin === self.location.origin) { var c2 = r.clone(); caches.open(CACHE).then(function(cache) { cache.put(e.request, c2); }); }
      return r;
    }).catch(function() { if (e.request.mode === 'navigate') return caches.match('/dashboards/index.html'); });
  }));
});
