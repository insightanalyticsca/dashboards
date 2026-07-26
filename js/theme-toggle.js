/* ════════════════════════════════════════════════════════════════════════════
   theme-toggle.js — Site-wide light/dark theme controller
   - Tiny icon (sun/moon) fixed in top-right corner
   - Persists to localStorage ('docchat-theme')
   - Broadcasts to all iframes via postMessage (csr-dashboard-theme:apply)
   - Swaps ECharts registered theme on all chart instances
   - Applies CSS variables via [data-theme] on <html>
   ════════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  var STORAGE_KEY = 'docchat-theme';
  var THEME_APPLY = 'csr-dashboard-theme:apply';

  function resolveTheme() {
    try {
      var saved = localStorage.getItem(STORAGE_KEY);
      if (saved === 'light' || saved === 'dark') return saved;
    } catch (_) {}
    try {
      return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    } catch (_) {
      return 'light';
    }
  }

  function applyTheme(theme) {
    var t = theme === 'dark' ? 'dark' : 'light';
    document.documentElement.setAttribute('data-theme', t);
    document.documentElement.style.colorScheme = t;
    if (document.body) document.body.setAttribute('data-theme', t);

    // Update toggle icon
    var icon = document.getElementById('themeToggleIcon');
    if (icon) {
      icon.className = t === 'dark' ? 'fa-solid fa-sun' : 'fa-solid fa-moon';
    }

    // Broadcast to all iframes (CSR runtime listens for csr-dashboard-theme:apply)
    document.querySelectorAll('iframe').forEach(function (frame) {
      if (frame.contentWindow) {
        try {
          frame.contentWindow.postMessage({ type: THEME_APPLY, theme: t }, '*');
        } catch (_) {}
      }
    });

    // Also set data-csr-theme on <html> for CSR visuals that check it
    document.documentElement.setAttribute('data-csr-theme', t);

    // Re-apply ECharts theme to all chart instances
    if (typeof echarts !== 'undefined') {
      document.querySelectorAll('canvas').forEach(function (canvas) {
        var container = canvas.parentElement || canvas;
        var inst = echarts.getInstanceByDom(container);
        if (inst) {
          // Dispose and re-init with new theme
          var option = inst.getOption();
          inst.dispose();
          echarts.init(container, t === 'dark' ? 'vivid-dark' : 'vivid').setOption(option);
        }
      });
    }

    try { localStorage.setItem(STORAGE_KEY, t); } catch (_) {}
    return t;
  }

  function init() {
    // Create toggle button if it doesn't exist
    var existing = document.getElementById('themeToggle');
    if (existing) return;

    var btn = document.createElement('button');
    btn.id = 'themeToggle';
    btn.type = 'button';
    btn.setAttribute('aria-label', 'Toggle dark/light theme');
    btn.title = 'Toggle theme';
    btn.style.cssText = [
      'position:fixed',
      'bottom:14px',
      'right:14px',
      'z-index:9999',
      'width:30px',
      'height:30px',
      'border-radius:8px',
      'border:1px solid var(--toggle-border, rgba(255,255,255,0.18))',
      'background:var(--toggle-bg, rgba(99,102,241,0.15))',
      'color:var(--toggle-color, #fff)',
      'font-size:12px',
      'cursor:pointer',
      'display:grid',
      'place-items:center',
      'transition:all 220ms cubic-bezier(.22,1,.36,1)',
      'backdrop-filter:blur(10px) saturate(160%)',
      '-webkit-backdrop-filter:blur(10px) saturate(160%)',
      'box-shadow:0 4px 14px rgba(0,0,0,0.18)'
    ].join(';');

    var icon = document.createElement('i');
    icon.id = 'themeToggleIcon';
    icon.className = 'fa-solid fa-moon';
    btn.appendChild(icon);

    btn.addEventListener('mouseenter', function () {
      btn.style.transform = 'translateY(-2px) scale(1.05)';
      btn.style.boxShadow = '0 8px 24px rgba(99,102,241,0.35)';
    });
    btn.addEventListener('mouseleave', function () {
      btn.style.transform = '';
      btn.style.boxShadow = '0 4px 14px rgba(0,0,0,0.18)';
    });

    btn.addEventListener('click', function () {
      var current = document.documentElement.getAttribute('data-theme') || 'light';
      applyTheme(current === 'dark' ? 'light' : 'dark');
    });

    document.body.appendChild(btn);

    // Apply initial theme
    applyTheme(resolveTheme());

    // Listen for cross-tab sync
    window.addEventListener('storage', function (e) {
      if (e.key === STORAGE_KEY && (e.newValue === 'light' || e.newValue === 'dark')) {
        applyTheme(e.newValue);
      }
    });

    // Listen for iframe theme requests (CSR runtime sends csr-dashboard-theme:request)
    window.addEventListener('message', function (event) {
      var msg = event.data || {};
      if (msg.type === 'csr-dashboard-theme:request' && event.source) {
        try {
          var currentTheme = document.documentElement.getAttribute('data-theme') || resolveTheme();
          event.source.postMessage({ type: THEME_APPLY, theme: currentTheme }, '*');
        } catch (_) {}
      }
    });
  }

  // Expose for programmatic use
  window.DocChatTheme = {
    get: function () { return document.documentElement.getAttribute('data-theme') || resolveTheme(); },
    set: applyTheme,
    toggle: function () {
      var current = document.documentElement.getAttribute('data-theme') || 'light';
      return applyTheme(current === 'dark' ? 'light' : 'dark');
    }
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
