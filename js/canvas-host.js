/* ════════════════════════════════════════════════════════════════════════════
   canvas-host.js — Shared version canvas renderer with drag + resize
   Mirrors the .NET Multi.cshtml + executive-dashboard-suite.js pattern:
   1. Creates iframes for each visual (absolute positioned, not grid)
   2. Fetches one JSON payload containing data for all visuals
   3. After iframe load: resets chart via eval + posts data via postMessage
   4. Drag handle (⠿) + resize handle (corner) on each tile
   5. Double-click drag handle to reset position
   ════════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  function clampNumber(value, min, max) {
    var n = Number(value);
    return Math.min(max, Math.max(min, Number.isFinite(n) ? n : min));
  }

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

    // Layout state — mirrors executive-dashboard-suite.js
    var state = {
      defaultLayout: {},
      overrides: {},
      layoutZ: 100
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  PERSISTENCE — save/load layout to localStorage (per browser, per version)
    // ══════════════════════════════════════════════════════════════════════════
    var LAYOUT_KEY = 'canvas-layout-' + config.version;

    function saveLayout() {
      try {
        localStorage.setItem(LAYOUT_KEY, JSON.stringify(state.overrides));
      } catch (e) {
        console.warn('Canvas host: could not save layout', e);
      }
    }

    function loadLayout() {
      try {
        var saved = localStorage.getItem(LAYOUT_KEY);
        if (saved) {
          var parsed = JSON.parse(saved);
          if (parsed && typeof parsed === 'object') {
            state.overrides = parsed;
          }
        }
      } catch (e) {
        console.warn('Canvas host: could not load layout', e);
      }
    }

    function clearLayout() {
      try {
        localStorage.removeItem(LAYOUT_KEY);
      } catch (e) {}
      state.overrides = {};
    }

    // Load saved layout before computing defaults
    loadLayout();

    // Compute default layout from config (grid → absolute positions)
    function computeDefaultLayout() {
      var colCount = 12;
      var x = 0, y = 0, rowH = 0;
      var gapX = 0.4, gapY = 0.5;
      var totalUnits = 0;
      var placements = [];

      config.visuals.forEach(function(vis, idx) {
        var span = vis.w || 6;
        var h = vis.h || 320;
        // Convert pixel height to "units" (rough: 1 unit ≈ 80px)
        var hUnits = h / 80;

        if (x > 0 && x + span > colCount) {
          y += rowH + gapY;
          x = 0;
          rowH = 0;
        }
        placements.push({
          id: vis.id,
          x: (x / colCount) * 100 + gapX / 2,
          y: 0, // will compute after totalUnits
          w: (span / colCount) * 100 - gapX,
          h: 0, // will compute after totalUnits
          hUnits: hUnits
        });
        x += span;
        rowH = Math.max(rowH, hUnits);
      });

      totalUnits = Math.max(6, y + rowH);
      placements.forEach(function(p) {
        // Recompute y from stored row info
      });

      // Recompute y positions
      x = 0; y = 0; rowH = 0;
      placements.forEach(function(p, idx) {
        var vis = config.visuals[idx];
        var span = vis.w || 6;
        if (x > 0 && x + span > colCount) {
          y += rowH + gapY;
          x = 0;
          rowH = 0;
        }
        p.y = (y / totalUnits) * 100 + gapY / 2;
        p.h = (vis.h || 320) / (totalUnits * 80) * 100 - gapY;
        p.x = (x / colCount) * 100 + gapX / 2;
        p.w = (span / colCount) * 100 - gapX;
        p.z = 100 + idx;
        x += span;
        rowH = Math.max(rowH, (vis.h || 320) / 80);
      });

      // Set canvas min-height
      canvas.style.minHeight = Math.max(720, totalUnits * 80) + 'px';
      return placements;
    }

    var placements = computeDefaultLayout();
    placements.forEach(function(p) {
      state.defaultLayout[p.id] = { x: p.x, y: p.y, w: p.w, h: p.h, z: p.z };
    });

    function visualGeometry(id) {
      var base = state.defaultLayout[id] || { x: 0, y: 0, w: 50, h: 25, z: 0 };
      var override = state.overrides[id] || {};
      var w = clampNumber(override.w != null ? override.w : base.w, 8, 100);
      var h = clampNumber(override.h != null ? override.h : base.h, 8, 100);
      return {
        x: clampNumber(override.x != null ? override.x : base.x, 0, Math.max(0, 100 - w)),
        y: clampNumber(override.y != null ? override.y : base.y, 0, Math.max(0, 100 - h)),
        w: w, h: h,
        z: Number(override.z != null ? override.z : base.z) || 0
      };
    }

    function applyVisualGeometry(element, geometry) {
      if (!element || !geometry) return;
      element.style.left = geometry.x + '%';
      element.style.top = geometry.y + '%';
      element.style.width = geometry.w + '%';
      element.style.height = geometry.h + '%';
      element.style.zIndex = String(geometry.z || 0);
    }

    function resizeIframeCharts(iframe) {
      try {
        iframe.contentWindow.dispatchEvent(new Event('resize'));
      } catch (e) {}
    }

    function enableDragResize(canvasEl, tileEl, visId, iframe) {
      // Move handle (⠿)
      var moveHandle = document.createElement('button');
      moveHandle.type = 'button';
      moveHandle.className = 'canvas-layout-move';
      moveHandle.setAttribute('aria-label', 'Move ' + visId);
      moveHandle.title = 'Drag visual. Double-click to reset position.';
      moveHandle.innerHTML = '<span aria-hidden="true">⠿</span>';

      // Resize handle (corner)
      var resizeHandle = document.createElement('span');
      resizeHandle.className = 'canvas-layout-resize';
      resizeHandle.title = 'Resize visual';

      tileEl.appendChild(moveHandle);
      tileEl.appendChild(resizeHandle);

      var begin = function(event, mode) {
        if (event.button !== 0) return;
        event.preventDefault();
        event.stopPropagation();
        var canvasRect = canvasEl.getBoundingClientRect();
        if (!canvasRect.width || !canvasRect.height) return;
        var start = visualGeometry(visId);
        var pointerX = event.clientX;
        var pointerY = event.clientY;
        state.layoutZ = Math.max(state.layoutZ, start.z || 0) + 1;
        start.z = state.layoutZ;
        applyVisualGeometry(tileEl, start);
        tileEl.classList.add('canvas-layout-active');
        canvasEl.classList.add('canvas-layout-changing');
        try { event.currentTarget.setPointerCapture(event.pointerId); } catch (_) {}

        var onMove = function(moveEvent) {
          var dx = ((moveEvent.clientX - pointerX) / canvasRect.width) * 100;
          var dy = ((moveEvent.clientY - pointerY) / canvasRect.height) * 100;
          var next = Object.assign({}, start);
          if (mode === 'move') {
            next.x = clampNumber(start.x + dx, 0, Math.max(0, 100 - start.w));
            next.y = clampNumber(start.y + dy, 0, Math.max(0, 100 - start.h));
          } else {
            next.w = clampNumber(start.w + dx, 8, Math.max(8, 100 - start.x));
            next.h = clampNumber(start.h + dy, 8, Math.max(8, 100 - start.y));
          }
          applyVisualGeometry(tileEl, next);
          resizeIframeCharts(iframe);
        };

        var finish = function() {
          window.removeEventListener('pointermove', onMove, true);
          window.removeEventListener('pointerup', finish, true);
          window.removeEventListener('pointercancel', finish, true);
          tileEl.classList.remove('canvas-layout-active');
          canvasEl.classList.remove('canvas-layout-changing');

          var canvasBox = canvasEl.getBoundingClientRect();
          var box = tileEl.getBoundingClientRect();
          var geometry = {
            x: clampNumber(((box.left - canvasBox.left) / canvasBox.width) * 100, 0, 100),
            y: clampNumber(((box.top - canvasBox.top) / canvasBox.height) * 100, 0, 100),
            w: clampNumber((box.width / canvasBox.width) * 100, 8, 100),
            h: clampNumber((box.height / canvasBox.height) * 100, 8, 100),
            z: Number(tileEl.style.zIndex || start.z || 0)
          };
          geometry.x = clampNumber(geometry.x, 0, Math.max(0, 100 - geometry.w));
          geometry.y = clampNumber(geometry.y, 0, Math.max(0, 100 - geometry.h));
          state.overrides[visId] = geometry;
          saveLayout();  // persist to localStorage
          resizeIframeCharts(iframe);
        };

        window.addEventListener('pointermove', onMove, true);
        window.addEventListener('pointerup', finish, true);
        window.addEventListener('pointercancel', finish, true);
      };

      moveHandle.addEventListener('pointerdown', function(e) { begin(e, 'move'); });
      resizeHandle.addEventListener('pointerdown', function(e) { begin(e, 'resize'); });
      moveHandle.addEventListener('dblclick', function(e) {
        e.preventDefault();
        e.stopPropagation();
        delete state.overrides[visId];
        saveLayout();  // persist reset to localStorage
        applyVisualGeometry(tileEl, visualGeometry(visId));
        resizeIframeCharts(iframe);
      });
    }

    // Add Reset Layout button to header
    var resetBtn = document.createElement('button');
    resetBtn.className = 'canvas-reset-btn';
    resetBtn.type = 'button';
    resetBtn.innerHTML = '<i class="fa-solid fa-rotate-left"></i> Reset Layout';
    resetBtn.title = 'Reset all visuals to default positions';
    resetBtn.addEventListener('click', function() {
      if (!confirm('Reset all visuals to their default positions?')) return;
      clearLayout();
      // Reapply default geometry to all tiles
      config.visuals.forEach(function(vis) {
        var tile = canvas.querySelector('[data-visual-id="' + vis.id + '"]');
        if (tile) {
          applyVisualGeometry(tile, visualGeometry(vis.id));
          var iframe = tile.querySelector('iframe');
          if (iframe) resizeIframeCharts(iframe);
        }
      });
    });
    var header = app.querySelector('.canvas-header');
    if (header) header.appendChild(resetBtn);

    // ══════════════════════════════════════════════════════════════════════════
    //  Fetch data + create tiles
    // ══════════════════════════════════════════════════════════════════════════
    var dataUrl = '../data/versions/' + config.version + '.json';

    fetch(dataUrl, { cache: 'no-store' })
      .then(function(res) {
        if (!res.ok) throw new Error('HTTP ' + res.status);
        return res.json();
      })
      .then(function(payload) {
        config.visuals.forEach(function(vis) {
          var visualData = payload[vis.id] || (payload.data && payload.data[vis.id]) || payload;

          // Create tile (absolute positioned, NOT grid)
          var tile = document.createElement('div');
          tile.className = 'canvas-tile';
          tile.dataset.visualId = vis.id;

          var tileHead = document.createElement('div');
          tileHead.className = 'canvas-tile-head';
          tileHead.innerHTML = '<span class="canvas-tile-title">' + (vis.title || vis.id) + '</span>';
          tile.appendChild(tileHead);

          var frameHost = document.createElement('div');
          frameHost.className = 'canvas-tile-body';
          tile.appendChild(frameHost);
          canvas.appendChild(tile);

          // Apply initial geometry
          applyVisualGeometry(tile, visualGeometry(vis.id));

          // Create iframe
          var frame = document.createElement('iframe');
          frame.className = 'custom-html-frame';
          frame.style.width = '100%';
          frame.style.height = '100%';
          frame.style.border = '0';
          frame.style.background = 'transparent';
          frame.setAttribute('loading', 'eager');
          frame.src = './' + vis.file;
          frameHost.appendChild(frame);

          // Enable drag/resize
          enableDragResize(canvas, tile, vis.id, frame);

          // After iframe loads: reset chart + post data
          frame.addEventListener('load', function() {
            setTimeout(function() {
              try {
                var win = frame.contentWindow;
                win.DashVisualContext = visualData;

                // Reset chart closure variable via eval
                try {
                  win.eval('try { chart = null; } catch(e) {};');
                } catch (e) {}

                // Post data via postMessage (works for all visual patterns)
                win.postMessage({
                  type: 'dashboard-custom-html:update',
                  payload: visualData,
                  data: visualData.data || visualData
                }, '*');

                // Resize charts after delay
                setTimeout(function() {
                  try { win.dispatchEvent(new Event('resize')); } catch (e) {}
                }, 200);
              } catch (e) {
                console.warn('Canvas host: error on', vis.file, e);
              }
            }, 300);
          });
        });
      })
      .catch(function(err) {
        canvas.innerHTML = '<div style="padding:40px;text-align:center;color:#9b1c1c;font-weight:600">⚠ ' + err.message + '</div>';
      });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
