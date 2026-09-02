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
        temperature: 0.3,
        max_tokens: 1200,
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

  // ─── System prompt — honest structured analysis ──────────────────────────
  // Demands a 4-part brief (WHAT HAPPENED / WHY / WHAT TO EXPECT / WHAT TO DO)
  // but explicitly forbids invented confidence %s, fabricated forecasts, and
  // store-level attribution when the payload is aggregated. Only produces
  // statements that can be derived directly from the provided JSON.
  function buildSystemPrompt(visualContext) {
    return [
      'You are Dashboards Studio, an analytics assistant that answers questions about a specific dashboard.',
      'You have access to the dashboard\'s data payload below. Your job is to produce an honest, structured analysis.',
      '',
      'OUTPUT FORMAT — always use these 4 sections, each on its own line, with the section header in uppercase followed by a colon:',
      '  WHAT HAPPENED: <one or two sentences stating the factual change, citing actual metric labels and delta values from the data>',
      '  WHY: <one or two sentences explaining the driver, only if the data or notes explicitly support it. If the payload does not contain driver-level breakdowns, say "Driver not isolated in this payload." instead of guessing>',
      '  WHAT TO EXPECT: <one sentence describing the direction the trend points IF IT CONTINUES. Do NOT produce a specific forecast number. Do NOT attach a confidence percentage. Use phrases like "if the current trend continues" or "the trajectory suggests">',
      '  WHAT TO DO: <one or two sentences of actionable recommendation grounded in the data pattern. If the data lacks the granularity to recommend a specific action, say what additional breakdown would be needed>',
      '',
      'HARD RULES (these are non-negotiable):',
      '1. Every number you cite MUST come from the provided data. Do not invent metrics, percentages, or dollar figures.',
      '2. NEVER produce a calibrated confidence percentage (e.g., "82% confidence", "P=0.9"). You are not a statistical model. If you want to express certainty, use qualitative language ("strong signal", "weak signal", "mixed").',
      '3. NEVER produce a specific point forecast (e.g., "next-month revenue +3.9%"). You may describe direction only.',
      '4. NEVER attribute to specific stores, regions, or units unless the data payload actually contains that granularity. If asked, state that the payload is aggregated and store-level attribution is not available.',
      '5. NEVER compute "annualized opportunity" or "$X opportunity" unless the payload contains the full unit-economics chain (unit count × rate × frequency × time). If the chain is missing, say the figure cannot be derived.',
      '6. If the user\'s question cannot be answered from the data, say so directly. Do not extrapolate beyond what the JSON shows.',
      '7. Keep the entire response under 200 words. Tight prose, no filler, no preamble before WHAT HAPPENED.',
      '',
      'DASHBOARD DATA:',
      visualContext || '(no visual data loaded — if no data is shown, tell the user the dashboard payload could not be loaded)'
    ].join('\n');
  }

  // ─── Ask with visual context ──────────────────────────────────────────────
  async function ask(question, onToken) {
    var visualContext = buildVisualContext();

    var userPrompt = 'Question: ' + question + '\n\n' +
      'Produce the 4-part brief (WHAT HAPPENED / WHY / WHAT TO EXPECT / WHAT TO DO) based strictly on the dashboard data. ' +
      'Respect every hard rule in the system prompt.';

    if (CONFIG.provider === 'groq' && CONFIG.groqKey) {
      var messages = [
        { role: 'system', content: buildSystemPrompt(visualContext) },
        { role: 'user', content: userPrompt }
      ];
      return await groqChat(messages, onToken);
    } else {
      // Demo mode — honest structured fallback built from actual data
      var answer = buildDemoBrief(visualContext);
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

  // ─── Demo-mode brief — honest structured fallback when Groq key absent ────
  function buildDemoBrief(visualContext) {
    if (!state.visualData) {
      return 'WHAT HAPPENED: No dashboard payload loaded for this page.\n' +
        'WHY: Visual data could not be fetched.\n' +
        'WHAT TO EXPECT: Configure a Groq API key (data/groq-config.json) to enable live analysis.\n' +
        'WHAT TO DO: Add a Groq key to the site config, then reopen this chat.';
    }
    var d = state.visualData;
    var lines = [];
    // WHAT HAPPENED — real KPI deltas
    lines.push('WHAT HAPPENED: ' + (d.title || state.versionTitle) + ' as of ' + (d.asOfLabel || 'latest period') + '.');
    if (d.metrics && d.metrics.length) {
      var top = d.metrics.slice(0, 3).map(function(m) {
        var parts = [m.label + ' = ' + m.value + (m.format === 'currency' ? ' CAD' : m.format === 'percent' || m.format === 'percent2' ? '%' : '')];
        if (m.yoy != null) parts.push('YoY ' + (m.yoy > 0 ? '+' : '') + m.yoy + (m.deltaMode === 'points' ? ' pts' : '%'));
        if (m.mom != null) parts.push('MoM ' + (m.mom > 0 ? '+' : '') + m.mom + (m.deltaMode === 'points' ? ' pts' : '%'));
        return parts.join(', ');
      });
      lines[0] += ' ' + top.join('; ') + '.';
    }
    // WHY — only if notes exist
    if (d.notes && d.notes.length) {
      lines.push('WHY: ' + d.notes[0]);
    } else {
      lines.push('WHY: Driver not isolated in this payload.');
    }
    // WHAT TO EXPECT — direction only, no forecast number
    if (d.metrics && d.metrics.length) {
      var m0 = d.metrics[0];
      var dir = m0.yoy != null ? (m0.yoy > 0 ? 'up' : 'down') : (m0.mom != null ? (m0.mom > 0 ? 'up' : 'down') : 'flat');
      lines.push('WHAT TO EXPECT: If the current trend continues, ' + m0.label + ' trajectory points ' + dir + ' for the next period.');
    } else {
      lines.push('WHAT TO EXPECT: Trend direction requires time-series history in the payload.');
    }
    // WHAT TO DO — honest about granularity
    lines.push('WHAT TO DO: Review the charts on this dashboard for the segment-level breakdown that would inform a targeted action.');
    return lines.join('\n');
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
        addMessage('assistant', 'Hi! Ask about this dashboard and I\'ll give you a structured brief: WHAT HAPPENED, WHY, WHAT TO EXPECT, and WHAT TO DO — based strictly on the data on this page. No invented confidence %s or fabricated forecasts.');
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
