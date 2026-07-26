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

                // Use eval() to reset the chart closure variable and call render().
                // Pass {payload: DashVisualContext} so render(msg) functions that
                // expect msg.payload work, AND render(source) functions that call
                // extract(source) which checks source.payload also work.
                var rendered = false;
                if (typeof win.render === 'function') {
                  try {
                    win.eval('try { chart = null; } catch(e) {}; render({payload: DashVisualContext, data: DashVisualContext.data || DashVisualContext});');
                    rendered = true;
                  } catch (e) {
                    // If eval fails (CSP), fall back to direct call
                    win.render({payload: visualData, data: visualData.data || visualData});
                    rendered = true;
                  }
                }

                // Also call receive/init/update/setData for visuals that expose them
                if (!rendered) {
                  if (typeof win.receive === 'function') { win.receive(visualData); rendered = true; }
                  if (typeof win.init === 'function') { win.init(visualData); rendered = true; }
                  if (typeof win.update === 'function') { win.update(visualData); rendered = true; }
                  if (typeof win.setData === 'function') { win.setData(visualData); rendered = true; }
                }

                // Only post via postMessage if we couldn't render directly.
                // Posting after render() would cause a second render() call
                // which re-creates the DOM and breaks the chart instance.
                if (!rendered) {
                  win.postMessage({
                    type: 'dashboard-custom-html:update',
                    payload: visualData,
                    data: visualData.data || visualData
                  }, '*');
                }

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
