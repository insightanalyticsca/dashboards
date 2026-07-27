/* ════════════════════════════════════════════════════════════════════════════
   visual-chat.js — Floating chat widget for version canvas pages
   - Detects which version is open (executive / CSR / ITS)
   - Fetches the corresponding JSON payload
   - Adds visual data to the Groq system prompt as context
   - Streams answers from Groq with visual data citations
   ════════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  // ─── Config (same as api.js) ──────────────────────────────────────────────
  var CONFIG = {
    provider: localStorage.getItem('docchat.provider') || 'demo',
    groqKey: localStorage.getItem('docchat.groq.key') || '',
    groqModel: localStorage.getItem('docchat.groq.model') || 'llama-3.3-70b-versatile'
  };

  // Auto-load Groq config from data/groq-config.json
  (function autoLoad() {
    fetch('../data/groq-config.json', { cache: 'no-store' })
      .then(function(r) { return r.ok ? r.json() : null; })
      .then(function(cfg) {
        if (!cfg) return;
        if (cfg.provider && !localStorage.getItem('docchat.provider'))
          CONFIG.provider = cfg.provider;
        if (cfg.groqKeyEnc && !localStorage.getItem('docchat.groq.key'))
          CONFIG.groqKey = atob(cfg.groqKeyEnc);
        if (cfg.keyParts && !localStorage.getItem('docchat.groq.key'))
          CONFIG.groqKey = cfg.keyParts.map(function(p) { return p.split('').reverse().join(''); }).join('');
        if (cfg.groqModel) CONFIG.groqModel = cfg.groqModel;
      })
      .catch(function() {});
  })();

  // ─── State ────────────────────────────────────────────────────────────────
  var state = {
    visualData: null,       // the JSON payload for this version
    versionKey: '',         // e.g. 'ar', 'ebill', 'csr-aging-overview'
    versionTitle: '',       // e.g. 'AR Portfolio Executive Summary'
    isOpen: false,
    isStreaming: false,
    messages: []
  };

  // ─── Detect which version we're on ────────────────────────────────────────
  function detectVersion() {
    var suite = document.body.dataset.suite || '';
    var path = window.location.pathname;
    var filename = path.split('/').pop().replace('.html', '');

    // Executive versions: data-suite="ar" → data/executive/ar.json
    var execKeys = ['ar', 'payments', 'disconnects', 'ebill', 'finalbill'];
    if (execKeys.indexOf(suite) >= 0) {
      return { key: suite, title: suite.toUpperCase() + ' Executive', jsonPath: '../data/executive/' + suite + '.json' };
    }

    // CSR + ITS canvas versions: data-suite="csr-aging-overview"
    if (suite.indexOf('csr-') === 0 || suite.indexOf('its-') === 0) {
      return { key: suite, title: suite, jsonPath: '../data/versions/' + suite + '.json' };
    }

    // Executive by filename
    if (filename.indexOf('executive-') === 0) {
      var key = filename.replace('executive-', '').replace('-portfolio', '').replace('-payments', '')
        .replace('-bankruptcies', '').replace('-performance', '').replace('-recovery', '');
      // Map back to exec keys
      var keyMap = { 'ar': 'ar', 'customer': 'payments', 'disconnects': 'disconnects', 'ebill': 'ebill', 'final-bill': 'finalbill' };
      var mapped = keyMap[key] || key;
      return { key: mapped, title: filename, jsonPath: '../data/executive/' + mapped + '.json' };
    }

    return null;
  }

  // ─── Load visual data for context ────────────────────────────────────────
  async function loadVisualData() {
    var info = detectVersion();
    if (!info) return;

    state.versionKey = info.key;
    state.versionTitle = info.title;

    try {
      var res = await fetch(info.jsonPath, { cache: 'no-store' });
      if (!res.ok) return;
      state.visualData = await res.json();
    } catch (e) {
      console.warn('Visual chat: could not load visual data', e);
    }
  }

  // ─── Build context from visual data ──────────────────────────────────────
  function buildVisualContext() {
    if (!state.visualData) return '';

    var d = state.visualData;
    var parts = [];

    // Title + version
    parts.push('Dashboard: ' + (d.title || state.versionTitle));
    parts.push('Version: ' + (d.key || state.versionKey));
    if (d.asOfLabel) parts.push('Period: ' + d.asOfLabel);
    parts.push('');

    // Metrics (KPIs)
    if (d.metrics && d.metrics.length) {
      parts.push('KPIs:');
      d.metrics.forEach(function(m, i) {
        parts.push('  [' + (i + 1) + '] ' + m.label + ': ' + m.value +
          (m.format === 'currency' ? ' CAD' : '') +
          (m.format === 'percent' || m.format === 'percent2' ? '%' : '') +
          (m.mom != null ? ' (MoM: ' + (m.mom > 0 ? '+' : '') + m.mom + (m.deltaMode === 'points' ? ' pts' : '%') + ')' : '') +
          (m.yoy != null ? ' (YoY: ' + (m.yoy > 0 ? '+' : '') + m.yoy + (m.deltaMode === 'points' ? ' pts' : '%') + ')' : ''));
      });
      parts.push('');
    }

    // Charts summary
    if (d.charts && d.charts.length) {
      parts.push('Charts:');
      d.charts.forEach(function(c, i) {
        parts.push('  [' + (i + 1) + '] ' + c.title + ' (' + c.kind + ')');
        if (c.categories && c.categories.length) {
          parts.push('      Categories: ' + c.categories.join(', '));
        }
        if (c.series && c.series.length) {
          c.series.forEach(function(s, j) {
            var dataStr = (s.data || []).map(function(v) {
              if (v === null || v === undefined) return '—';
              return typeof v === 'number' ? v.toLocaleString() : v;
            }).join(', ');
            parts.push('      ' + s.name + ': [' + dataStr + ']');
          });
        }
      });
      parts.push('');
    }

    // Tables summary
    if (d.tables && d.tables.length) {
      parts.push('Tables:');
      d.tables.forEach(function(t, i) {
        parts.push('  [' + (i + 1) + '] ' + t.title);
        if (t.columns && t.columns.length) {
          parts.push('      Columns: ' + t.columns.join(', '));
        }
        if (t.rows && t.rows.length) {
          parts.push('      Rows: ' + t.rows.length);
          // Include first 3 rows as sample
          t.rows.slice(0, 3).forEach(function(r, j) {
            var rowStr = t.columns.map(function(col) {
              return col + '=' + (r[col] != null ? r[col] : '—');
            }).join(', ');
            parts.push('      Row ' + (j + 1) + ': ' + rowStr);
          });
          if (t.rows.length > 3) parts.push('      ... (' + (t.rows.length - 3) + ' more rows)');
        }
      });
      parts.push('');
    }

    // Notes
    if (d.notes && d.notes.length) {
      parts.push('Notes:');
      d.notes.forEach(function(n) { parts.push('  - ' + n); });
    }

    // For canvas versions (CSR + ITS), also include individual visual data
    if (state.visualData._meta || (state.visualData && !state.visualData.metrics)) {
      // This is a canvas payload — data is keyed by visual ID
      var visualKeys = Object.keys(state.visualData).filter(function(k) { return k !== '_meta'; });
      if (visualKeys.length && !d.metrics) {
        parts = ['Dashboard: ' + state.versionTitle, 'Version: ' + state.versionKey, ''];
        parts.push('Visuals on this canvas:');
        visualKeys.forEach(function(vk, i) {
          var vd = state.visualData[vk];
          var rows = vd.data || vd.rows || [];
          if (rows.length) {
            parts.push('  [' + (i + 1) + '] ' + vk + ' (' + rows.length + ' rows)');
            // Include first 2 rows as sample
            rows.slice(0, 2).forEach(function(r, j) {
              var rowStr = Object.keys(r).map(function(k) {
                return k + '=' + r[k];
              }).join(', ');
              parts.push('      Row ' + (j + 1) + ': ' + rowStr);
            });
            if (rows.length > 2) parts.push('      ... (' + (rows.length - 2) + ' more rows)');
          }
        });
      }
    }

    return parts.join('\n');
  }

  // ─── Groq streaming chat ──────────────────────────────────────────────────
  async function groqChat(messages, onToken) {
    if (!CONFIG.groqKey) throw new Error('Groq API key not configured');

    var res = await fetch('https://api.groq.com/openai/v1/chat/completions', {
      method: 'POST',
      headers: {
        'Authorization': 'Bearer ' + CONFIG.groqKey,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        model: CONFIG.groqModel,
        messages: messages,
        temperature: 0.4,
        max_tokens: 800,
        stream: true
      })
    });

    if (!res.ok) {
      var err = await res.text();
      throw new Error('Groq API error (' + res.status + '): ' + err.slice(0, 200));
    }

    var reader = res.body.getReader();
    var decoder = new TextDecoder();
    var buffer = '';
    var fullText = '';

    while (true) {
      var chunk = await reader.read();
      if (chunk.done) break;
      buffer += decoder.decode(chunk.value, { stream: true });

      var lines = buffer.split('\n');
      buffer = lines.pop() || '';

      for (var i = 0; i < lines.length; i++) {
        var trimmed = lines[i].trim();
        if (trimmed.indexOf('data:') !== 0) continue;
        var data = trimmed.slice(5).trim();
        if (data === '[DONE]') continue;
        try {
          var evt = JSON.parse(data);
          if (evt.choices && evt.choices[0] && evt.choices[0].delta && evt.choices[0].delta.content) {
            fullText += evt.choices[0].delta.content;
            if (onToken) onToken(evt.choices[0].delta.content);
          }
        } catch (_) {}
      }
    }

    return fullText;
  }

  // ─── Ask with visual context ──────────────────────────────────────────────
  async function ask(question, onToken) {
    var visualContext = buildVisualContext();

    var systemPrompt = 'You are DocChat, an assistant that answers questions about dashboard data.\n' +
      'You have access to the visual data from the current dashboard version.\n' +
      'Answer based on the provided dashboard data. Cite metrics by their label.\n' +
      'If the data does not contain the answer, say so honestly.\n' +
      'Keep responses concise (2-4 sentences) unless asked for more detail.\n\n' +
      'DASHBOARD DATA:\n' + (visualContext || '(no visual data loaded)');

    var userPrompt = 'Question: ' + question;

    if (CONFIG.provider === 'groq' && CONFIG.groqKey) {
      var messages = [
        { role: 'system', content: systemPrompt },
        { role: 'user', content: userPrompt }
      ];
      return await groqChat(messages, onToken);
    } else {
      // Demo mode
      var answer = 'Based on the dashboard data:\n\n';
      if (state.visualData && state.visualData.metrics) {
        answer += state.visualData.metrics.map(function(m) {
          return m.label + ': ' + m.value;
        }).join('\n');
      } else if (visualContext) {
        answer += visualContext.slice(0, 500);
      } else {
        answer = 'No visual data available for this page.';
      }
      if (onToken) {
        var tokens = answer.match(/\S+\s*/g) || [answer];
        for (var i = 0; i < tokens.length; i++) {
          await new Promise(function(r) { setTimeout(r, 24); });
          onToken(tokens[i]);
        }
      }
      return answer;
    }
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  UI — floating chat widget
  // ══════════════════════════════════════════════════════════════════════════

  function createWidget() {
    // Launcher button (bottom-left, above theme toggle)
    var launcher = document.createElement('button');
    launcher.id = 'visualChatLauncher';
    launcher.type = 'button';
    launcher.setAttribute('aria-label', 'Open visual chat');
    launcher.title = 'Ask about this dashboard';
    launcher.style.cssText = [
      'position:fixed', 'bottom:14px', 'left:14px', 'z-index:9998',
      'width:30px', 'height:30px', 'border-radius:8px',
      'border:1px solid var(--toggle-border, rgba(99,102,241,0.25))',
      'background:var(--toggle-bg, rgba(99,102,241,0.15))',
      'color:var(--toggle-color, #6366f1)',
      'font-size:12px', 'cursor:pointer',
      'display:grid', 'place-items:center',
      'transition:all 220ms cubic-bezier(.22,1,.36,1)',
      'backdrop-filter:blur(10px) saturate(160%)',
      '-webkit-backdrop-filter:blur(10px) saturate(160%)',
      'box-shadow:0 4px 14px rgba(0,0,0,0.18)'
    ].join(';');

    launcher.innerHTML = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>';

    launcher.addEventListener('mouseenter', function() {
      launcher.style.transform = 'translateY(-2px) scale(1.05)';
      launcher.style.boxShadow = '0 8px 24px rgba(99,102,241,0.35)';
    });
    launcher.addEventListener('mouseleave', function() {
      launcher.style.transform = '';
      launcher.style.boxShadow = '0 4px 14px rgba(0,0,0,0.18)';
    });
    launcher.addEventListener('click', toggleChat);

    document.body.appendChild(launcher);

    // Chat panel
    var panel = document.createElement('div');
    panel.id = 'visualChatPanel';
    panel.style.cssText = [
      'position:fixed', 'bottom:52px', 'left:14px', 'z-index:9998',
      'width:380px', 'max-width:calc(100vw - 28px)',
      'height:480px', 'max-height:calc(100vh - 80px)',
      'border-radius:14px',
      'border:1px solid var(--theme-border, rgba(99,102,241,0.20))',
      'background:var(--theme-panel, rgba(255,255,255,0.95))',
      'backdrop-filter:blur(18px) saturate(160%)',
      '-webkit-backdrop-filter:blur(18px) saturate(160%)',
      'box-shadow:0 24px 64px rgba(0,0,0,0.25), 0 8px 24px rgba(0,0,0,0.15)',
      'display:none', 'flex-direction:column',
      'overflow:hidden',
      'transition:all 220ms cubic-bezier(.22,1,.36,1)'
    ].join(';');

    panel.innerHTML = '' +
      '<div style="padding:8px 12px;border-bottom:1px solid var(--theme-border,rgba(0,0,0,0.08));display:flex;align-items:center;gap:6px;">' +
        '<span style="font-size:11px;font-weight:700;color:var(--theme-text,#171777);flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' +
          '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="vertical-align:-2px;margin-right:4px;"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>' +
          'Visual Chat' +
        '</span>' +
        '<button id="visualChatClose" style="width:20px;height:20px;border:0;border-radius:5px;background:transparent;color:var(--theme-muted,#94a3b8);cursor:pointer;font-size:12px;display:grid;place-items:center;">✕</button>' +
      '</div>' +
      '<div id="visualChatMessages" style="flex:1;overflow-y:auto;padding:10px 12px;display:flex;flex-direction:column;gap:8px;"></div>' +
      '<div style="padding:8px 12px;border-top:1px solid var(--theme-border,rgba(0,0,0,0.08));display:flex;gap:6px;">' +
        '<input id="visualChatInput" type="text" placeholder="Ask about this dashboard…" style="flex:1;background:var(--theme-panel,rgba(255,255,255,0.04));border:1px solid var(--theme-border,rgba(0,0,0,0.10));border-radius:8px;padding:6px 10px;font-size:11px;color:var(--theme-text,#171777);outline:none;">' +
        '<button id="visualChatSend" style="width:30px;height:30px;border:0;border-radius:8px;background:linear-gradient(135deg,var(--theme-primary,#6366f1),var(--theme-accent,#06b6d4));color:#fff;cursor:pointer;display:grid;place-items:center;font-size:11px;flex-shrink:0;">' +
          '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/></svg>' +
        '</button>' +
      '</div>';

    document.body.appendChild(panel);

    // Wire events
    document.getElementById('visualChatClose').addEventListener('click', toggleChat);
    document.getElementById('visualChatSend').addEventListener('click', handleSend);
    var input = document.getElementById('visualChatInput');
    input.addEventListener('keydown', function(e) {
      if (e.key === 'Enter') handleSend();
    });
  }

  function toggleChat() {
    state.isOpen = !state.isOpen;
    var panel = document.getElementById('visualChatPanel');
    var launcher = document.getElementById('visualChatLauncher');
    if (state.isOpen) {
      panel.style.display = 'flex';
      launcher.style.background = 'var(--theme-primary, #6366f1)';
      launcher.style.color = '#fff';
      // Add welcome message if empty
      var msgs = document.getElementById('visualChatMessages');
      if (msgs.children.length === 0) {
        addMessage('assistant', 'Hi! I can answer questions about the data on this dashboard. What would you like to know?');
      }
      setTimeout(function() { document.getElementById('visualChatInput').focus(); }, 100);
    } else {
      panel.style.display = 'none';
      launcher.style.background = '';
      launcher.style.color = '';
    }
  }

  function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, function(c) {
      return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];
    });
  }

  function addMessage(role, text) {
    var msgs = document.getElementById('visualChatMessages');
    var div = document.createElement('div');
    div.style.cssText = 'max-width:90%;padding:8px 10px;border-radius:8px;font-size:11px;line-height:1.5;' +
      (role === 'user'
        ? 'align-self:flex-end;background:linear-gradient(135deg,var(--theme-primary,#6366f1),var(--theme-accent,#06b6d4));color:#fff;'
        : 'align-self:flex-start;background:var(--theme-panel-hover,rgba(0,0,0,0.04));border:1px solid var(--theme-border,rgba(0,0,0,0.06));color:var(--theme-text,#171777);');
    div.textContent = text;
    msgs.appendChild(div);
    msgs.scrollTop = msgs.scrollHeight;
    return div;
  }

  async function handleSend() {
    var input = document.getElementById('visualChatInput');
    var text = input.value.trim();
    if (!text || state.isStreaming) return;
    input.value = '';

    addMessage('user', text);
    var assistantDiv = addMessage('assistant', '…');
    state.isStreaming = true;

    try {
      var firstToken = true;
      await ask(text, function(token) {
        if (firstToken) { assistantDiv.textContent = ''; firstToken = false; }
        assistantDiv.textContent += token;
        var msgs = document.getElementById('visualChatMessages');
        msgs.scrollTop = msgs.scrollHeight;
      });
    } catch (e) {
      assistantDiv.textContent = '⚠ ' + e.message;
      assistantDiv.style.color = 'var(--theme-danger, #ef4444)';
    } finally {
      state.isStreaming = false;
    }
  }

  // ─── Init ─────────────────────────────────────────────────────────────────
  function init() {
    // Only show on version canvas pages (executive, CSR, ITS)
    var suite = document.body.dataset.suite;
    if (!suite) return;

    createWidget();
    loadVisualData();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

})();
