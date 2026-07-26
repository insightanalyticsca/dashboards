/* ════════════════════════════════════════════════════════════════════════════
   canvas-host.js — Shared version canvas renderer
   Mirrors the .NET Multi.cshtml pattern:
   1. Creates iframes for each visual in the layout
   2. Fetches one JSON payload containing data for all visuals
   3. Posts the appropriate data slice to each iframe via postMessage
   4. The visual's existing message listener picks it up and renders

   Each canvas page defines:
   window.CANVAS_CONFIG = {
     version: "csr-aging-overview",
     title: "Aging & Collections Overview",
     visuals: [
       { id: "aging-bankruptcies", file: "aging-bankruptcies.html", w: 6, h: 300 },
       { id: "ar-buckets-stacked", file: "ar-buckets-stacked.html", w: 6, h: 300 },
       ...
     ]
   }
   ════════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  function init() {
    const config = window.CANVAS_CONFIG;
    if (!config) {
      console.error('CANVAS_CONFIG not defined');
      return;
    }

    const app = document.getElementById('app');
    if (!app) {
      console.error('#app element not found');
      return;
    }

    // Set title
    document.title = config.title;
    const titleEl = app.querySelector('.canvas-title');
    if (titleEl) titleEl.textContent = config.title;

    const asOfEl = app.querySelector('.canvas-asof');
    if (asOfEl) asOfEl.textContent = config.asOfLabel || '';

    const canvas = app.querySelector('.canvas-grid');
    if (!canvas) {
      console.error('.canvas-grid not found');
      return;
    }

    // Fetch the JSON payload containing data for all visuals
    const dataUrl = `../data/versions/${config.version}.json`;

    fetch(dataUrl, { cache: 'no-store' })
      .then(res => {
        if (!res.ok) throw new Error(`Failed to load ${dataUrl}: HTTP ${res.status}`);
        return res.json();
      })
      .then(payload => {
        // Render notes if present
        if (config.notes && Array.isArray(config.notes)) {
          const notesEl = app.querySelector('.canvas-notes');
          if (notesEl) {
            notesEl.innerHTML = config.notes.map(n => `<div>${n}</div>`).join('');
          }
        }

        // Create iframe for each visual
        config.visuals.forEach((vis, idx) => {
          const tile = document.createElement('div');
          tile.className = 'canvas-tile';
          tile.style.gridColumn = `span ${vis.w || 6}`;
          tile.style.minHeight = (vis.h || 300) + 'px';

          const tileHead = document.createElement('div');
          tileHead.className = 'canvas-tile-head';
          tileHead.innerHTML = `<span class="canvas-tile-title">${vis.title || vis.id}</span>`;
          tile.appendChild(tileHead);

          const frameHost = document.createElement('div');
          frameHost.className = 'canvas-tile-body';
          tile.appendChild(frameHost);

          canvas.appendChild(tile);

          // Create iframe
          const frame = document.createElement('iframe');
          frame.className = 'custom-html-frame';
          frame.style.width = '100%';
          frame.style.height = '100%';
          frame.style.border = '0';
          frame.style.background = 'transparent';
          frame.setAttribute('loading', 'eager');
          frame.src = `./${vis.file}`;

          frameHost.appendChild(frame);

          // Wait for iframe to load, then post data
          frame.addEventListener('load', () => {
            // Small delay to ensure the visual's message listener is registered
            setTimeout(() => {
              const visualData = payload[vis.id] || payload.data?.[vis.id] || payload;

              // Post the data to the iframe via postMessage
              // The visual's existing window.addEventListener('message', ...) picks it up
              frame.contentWindow.postMessage({
                type: 'dashboard-custom-html:update',
                payload: visualData,
                data: visualData.data || visualData
              }, '*');

              // Also post as 'init' type (some visuals listen for :init)
              frame.contentWindow.postMessage({
                type: 'dashboard-custom-html:init',
                payload: visualData,
                data: visualData.data || visualData
              }, '*');

              // Try calling global entry points (some CSR visuals expose window.receive)
              try {
                if (typeof frame.contentWindow.receive === 'function') {
                  frame.contentWindow.receive(visualData);
                }
                if (typeof frame.contentWindow.init === 'function') {
                  frame.contentWindow.init(visualData);
                }
                if (typeof frame.contentWindow.update === 'function') {
                  frame.contentWindow.update(visualData);
                }
                if (typeof frame.contentWindow.setData === 'function') {
                  frame.contentWindow.setData(visualData);
                }
                // CSR visuals with ITS_LIVE pattern
                if (typeof frame.contentWindow.render === 'function') {
                  frame.contentWindow.render(visualData);
                }
              } catch (e) {
                // Cross-origin or not ready — postMessage already handled above
              }
            }, 200);
          });

          // Handle iframe load errors
          frame.addEventListener('error', () => {
            frameHost.innerHTML = `<div style="padding:20px;color:#9b1c1c;text-align:center">Failed to load ${vis.file}</div>`;
          });
        });
      })
      .catch(err => {
        canvas.innerHTML = `<div style="grid-column:span 12;padding:40px;text-align:center;color:#9b1c1c;font-weight:600">⚠ ${err.message}</div>`;
        console.error('Canvas load failed:', err);
      });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
