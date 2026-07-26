/* ════════════════════════════════════════════════════════════════════════════
   canvas-host.js — Shared version canvas renderer
   Mirrors the .NET Multi.cshtml pattern:
   1. Creates iframes for each visual (using frame.src)
   2. Fetches one JSON payload containing data for all visuals
   3. After iframe load: sets window.DashVisualContext + uses eval() to reset
      chart closure + calls render({payload: data})
   4. Falls back to postMessage if render not found

   Uses eval() to access the visual's closure variable `chart` and reset it
   to null before calling render(). This is necessary because the visual's
   onDashMessages() calls handle({}) at init time (before DashVisualContext
   is set), which may leave the chart in a broken/disposed state.
   ════════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  function init() {
    var config = window.CANVAS_CONFIG;
    if (!config) { console.error('CANVAS_CONFIG not defined'); return; }

    var app = document.getElementById('app');
    if (!app) return;

    document.title = config.title;
    var titleEl = app.querySelector('.canvas-title');
    if (titleEl) titleEl.textContent = config.title;
    var asOfEl = app.querySelector('.canvas-asof');
    if (asOfEl) asOfEl.textContent = config.asOfLabel || '';

    var canvas = app.querySelector('.canvas-grid');
    if (!canvas) return;

    if (config.notes && Array.isArray(config.notes)) {
      var notesEl = app.querySelector('.canvas-notes');
      if (notesEl) notesEl.innerHTML = config.notes.map(function(n) { return '<div>' + n + '</div>'; }).join('');
    }

    var dataUrl = '../data/versions/' + config.version + '.json';

    fetch(dataUrl, { cache: 'no-store' })
      .then(function(res) {
        if (!res.ok) throw new Error('HTTP ' + res.status + ' on ' + dataUrl);
        return res.json();
      })
      .then(function(payload) {
        config.visuals.forEach(function(vis) {
          var tile = document.createElement('div');
          tile.className = 'canvas-tile';
          tile.style.gridColumn = 'span ' + (vis.w || 6);
          tile.style.minHeight = (vis.h || 300) + 'px';

          var tileHead = document.createElement('div');
          tileHead.className = 'canvas-tile-head';
          tileHead.innerHTML = '<span class="canvas-tile-title">' + (vis.title || vis.id) + '</span>';
          tile.appendChild(tileHead);

          var frameHost = document.createElement('div');
          frameHost.className = 'canvas-tile-body';
          tile.appendChild(frameHost);
          canvas.appendChild(tile);

          var visualData = payload[vis.id] || (payload.data && payload.data[vis.id]) || payload;
          var visualUrl = './' + vis.file;

          var frame = document.createElement('iframe');
          frame.className = 'custom-html-frame';
          frame.style.width = '100%';
          frame.style.height = '100%';
          frame.style.border = '0';
          frame.style.background = 'transparent';
          frame.setAttribute('loading', 'eager');
          frame.src = visualUrl;
          frameHost.appendChild(frame);

          frame.addEventListener('load', function() {
            setTimeout(function() {
              try {
                var win = frame.contentWindow;

                // Set DashVisualContext so the visual can access data
                win.DashVisualContext = visualData;

                // Reset the chart closure variable via eval (needed because the
                // visual's initial render({}) may have left the chart in a broken state).
                // eval() runs in the window's scope, giving access to let-declared variables.
                try {
                  win.eval('try { chart = null; } catch(e) {};');
                } catch (e) {}

                // Send data via postMessage. This works for ALL visual patterns:
                // - ITS_LIVE visuals: render(e.data.payload || e.data) → gets data directly
                // - Other ITS visuals: render(e.data) → msg.payload = data
                // - CSR visuals: extract(source) → source.payload.data = rows
                // Each visual's message listener properly unwraps the payload.
                win.postMessage({
                  type: 'dashboard-custom-html:update',
                  payload: visualData,
                  data: visualData.data || visualData
                }, '*');

                // Resize charts after a short delay
                setTimeout(function() {
                  try { win.dispatchEvent(new Event('resize')); } catch (e) {}
                }, 200);
              } catch (e) {
                console.warn('Canvas host: could not call render on', vis.file, e);
              }
            }, 300);
          });

          frame.addEventListener('error', function() {
            frameHost.innerHTML = '<div style="padding:20px;color:#9b1c1c;text-align:center">Failed to load ' + vis.file + '</div>';
          });
        });
      })
      .catch(function(err) {
        canvas.innerHTML = '<div style="grid-column:span 12;padding:40px;text-align:center;color:#9b1c1c;font-weight:600">⚠ ' + err.message + '</div>';
        console.error('Canvas load failed:', err);
      });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
