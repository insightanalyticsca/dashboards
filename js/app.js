/* ════════════════════════════════════════════════════════════════════════════
   app.js — DocChat demo UI logic
   - Loads JSON
   - Renders library, chat, analytics
   - Wires Settings modal (Groq / Ollama / GitHub)
   - Toast notifications + animated KPI counters
   ════════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  const $ = sel => document.querySelector(sel);
  const $$ = sel => Array.from(document.querySelectorAll(sel));
  const api = window.DocChatAPI;
  const charts = window.DocChatCharts;

  // ─── State ────────────────────────────────────────────────────────────────
  const state = {
    documents: [],
    selectedDocId: null,
    messages: [],
    isStreaming: false,
    analytics: null
  };

  // ─── Helpers ─────────────────────────────────────────────────────────────
  function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, c => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;'
    }[c]));
  }

  function formatBytes(n) {
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    if (n < 1024 * 1024 * 1024) return (n / (1024 * 1024)).toFixed(1) + ' MB';
    return (n / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
  }

  function formatDate(iso) {
    const d = new Date(iso);
    const now = new Date();
    const diff = (now - d) / 1000;
    if (diff < 60) return 'just now';
    if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
    if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
    if (diff < 86400 * 7) return Math.floor(diff / 86400) + 'd ago';
    return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
  }

  function animateCount(el, end, duration = 1100, suffix = '', decimals = 0) {
    const start = 0;
    const startT = performance.now();
    function step(now) {
      const t = Math.min(1, (now - startT) / duration);
      const eased = 1 - Math.pow(1 - t, 3);
      const val = start + (end - start) * eased;
      el.textContent = (decimals ? val.toFixed(decimals) : Math.round(val).toLocaleString()) + suffix;
      if (t < 1) requestAnimationFrame(step);
    }
    requestAnimationFrame(step);
  }

  function toast(message, kind = 'ok') {
    const stack = $('#toastStack');
    const el = document.createElement('div');
    el.className = 'toast ' + (kind === 'ok' ? '' : kind);
    const icon = kind === 'err' ? 'fa-circle-xmark' : kind === 'warn' ? 'fa-triangle-exclamation' : 'fa-circle-check';
    el.innerHTML = `<i class="fa-solid ${icon} toast-icon"></i><span>${escapeHtml(message)}</span>`;
    stack.appendChild(el);
    setTimeout(() => {
      el.classList.add('is-out');
      setTimeout(() => el.remove(), 300);
    }, 3200);
  }

  // ─── Topbar status pill ───────────────────────────────────────────────────
  function updateProviderPill() {
    const cfg = api.Config.get();
    const pill = $('#providerPill');
    const label = $('#providerLabel');
    let cls = 'pill', txt = 'Demo';
    if (cfg.provider === 'groq' && cfg.groqKey) { cls = 'pill pill--ok'; txt = 'Groq · live'; }
    else if (cfg.provider === 'ollama') { cls = 'pill pill--warn'; txt = 'Ollama · offline'; }
    pill.className = cls;
    label.textContent = txt;
  }

  // ─── Document library ───────────────────────────────────────────────────
  async function loadDocuments() {
    state.documents = await api.Documents.list();
    renderLibrary();
    if (state.analytics) renderHeroStats();
  }

  function renderLibrary() {
    const list = $('#docList');
    if (state.documents.length === 0) {
      list.innerHTML = `<div style="padding:30px;text-align:center;color:var(--muted);font-size:12px">No documents yet. Upload one to begin.</div>`;
      return;
    }
    list.innerHTML = state.documents.map(doc => {
      const typeClass = 'type-' + (doc.type || 'pdf').toLowerCase();
      const icon = (doc.type || 'PDF').slice(0, 3).toUpperCase();
      const selected = doc.id === state.selectedDocId ? 'is-selected' : '';
      return `
        <li class="doc-item ${selected}" data-id="${doc.id}">
          <div class="doc-icon ${typeClass}">${icon}</div>
          <div class="doc-info">
            <div class="doc-title">${escapeHtml(doc.title)}</div>
            <div class="doc-meta">
              <span>${formatBytes(doc.size)}</span>
              <span>·</span>
              <span>${doc.chunkCount} chunks</span>
              <span>·</span>
              <span>${formatDate(doc.uploadedAt)}</span>
            </div>
          </div>
          <span class="doc-chunks">${(doc.tags || []).slice(0, 1).join('') || doc.type}</span>
          <div class="doc-actions">
            <button class="icon-btn" data-act="view" title="View chunks"><i class="fa-solid fa-eye"></i></button>
            <button class="icon-btn" data-act="edit" title="Edit tags"><i class="fa-solid fa-tag"></i></button>
            <button class="icon-btn danger" data-act="del" title="Remove"><i class="fa-solid fa-trash"></i></button>
          </div>
        </li>
      `;
    }).join('');

    $$('#docList .doc-item').forEach(el => {
      const id = el.dataset.id;
      el.addEventListener('click', e => {
        if (e.target.closest('.icon-btn')) return;
        state.selectedDocId = id;
        renderLibrary();
      });
      el.querySelector('[data-act="del"]').addEventListener('click', async e => {
        e.stopPropagation();
        if (!confirm('Remove this document? (Demo: removes from in-memory list)')) return;
        await api.Documents.remove(id);
        toast('Document removed', 'ok');
        loadDocuments();
      });
      el.querySelector('[data-act="view"]').addEventListener('click', async e => {
        e.stopPropagation();
        const chunks = await api.Chunks.forDoc(id);
        const doc = await api.Documents.get(id);
        const msg = `${chunks.length} chunks in "${doc.title}":\n\n` +
          chunks.slice(0, 3).map((c, i) => `#${i + 1} (p.${c.page}): ${c.text.slice(0, 120)}…`).join('\n\n');
        appendMessage('assistant', msg, [], { model: 'demo', provider: 'demo' });
      });
      el.querySelector('[data-act="edit"]').addEventListener('click', async e => {
        e.stopPropagation();
        const doc = await api.Documents.get(id);
        const newTags = prompt('Tags (comma-separated):', doc.tags.join(','));
        if (newTags !== null) {
          api.Documents.update(id, { tags: newTags.split(',').map(s => s.trim()).filter(Boolean) });
          toast('Tags updated');
          loadDocuments();
        }
      });
    });
  }

  // ─── Upload dropzone ────────────────────────────────────────────────────
  function wireDropzone() {
    const dz = $('#dropzone');
    const input = $('#dropzoneInput');

    dz.addEventListener('click', () => input.click());
    input.addEventListener('change', e => handleFiles(e.target.files));

    ['dragenter', 'dragover'].forEach(ev => dz.addEventListener(ev, e => {
      e.preventDefault();
      dz.classList.add('is-drag');
    }));
    ['dragleave', 'drop'].forEach(ev => dz.addEventListener(ev, e => {
      e.preventDefault();
      dz.classList.remove('is-drag');
    }));
    dz.addEventListener('drop', e => handleFiles(e.dataTransfer.files));
  }

  async function handleFiles(files) {
    if (!files || !files.length) return;
    for (const file of files) {
      const ext = (file.name.split('.').pop() || 'pdf').toLowerCase();
      const typeMap = { pdf: 'pdf', docx: 'docx', doc: 'docx', xlsx: 'xlsx', xls: 'xlsx', txt: 'docx' };
      const type = typeMap[ext] || 'pdf';
      const doc = await api.Documents.create({
        title: file.name,
        type,
        size: file.size,
        tags: ['upload'],
        summary: 'Uploaded via demo dropzone.',
        pages: 0
      });
      // Simulate chunking: create 5-15 fake chunks
      const chunkCount = 5 + Math.floor(Math.random() * 10);
      const baseText = `This is a sample chunk from ${file.name}. `;
      for (let i = 0; i < chunkCount; i++) {
        await api.Chunks.create({
          documentId: doc.id,
          index: i,
          text: baseText + `Chunk ${i + 1} of ${chunkCount}. Generated for demo purposes to populate the vector store.`,
          tokens: 28 + Math.floor(Math.random() * 20),
          page: i + 1
        });
      }
      await api.Documents.update(doc.id, { chunkCount, status: 'indexed', pages: Math.ceil(chunkCount / 2) });
      toast(`Indexed "${file.name}" — ${chunkCount} chunks`, 'ok');
    }
    loadDocuments();
  }

  // ─── Chat ────────────────────────────────────────────────────────────────
  function appendMessage(role, text, sources = [], meta = {}) {
    const wrap = $('#chatMessages');
    const msg = document.createElement('div');
    msg.className = 'msg ' + role;
    msg.dataset.msgId = 'msg-' + Date.now() + '-' + Math.random().toString(36).slice(2, 6);
    const avatar = role === 'user' ? '<i class="fa-solid fa-user"></i>' : '<i class="fa-solid fa-robot"></i>';
    const sourcesHtml = sources.length ? `
      <div class="msg-sources">
        ${sources.map((s, i) => `<span class="source-chip" title="${escapeHtml(s.text)}">[${i + 1}] ${s.documentId} · ${s.score.toFixed(2)}</span>`).join('')}
      </div>
    ` : '';
    const metaHtml = meta.model ? `
      <div class="msg-meta">
        <span>${meta.provider}/${meta.model}</span>
        ${meta.latencyMs ? `<span>· ${meta.latencyMs} ms</span>` : ''}
        ${meta.tokens?.total ? `<span>· ${meta.tokens.total} tok</span>` : ''}
      </div>
    ` : '';

    // Read status indicator (appears under every message)
    const isUser = role === 'user';
    const statusHtml = isUser
      ? `<div class="msg-status" data-status="sent" title="Sent · ${new Date().toLocaleTimeString()}">
           <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>
           <span class="msg-status-text">Sent</span>
         </div>`
      : `<div class="msg-status" data-status="delivered" title="Delivered · ${new Date().toLocaleTimeString()}">
           <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/><polyline points="24 6 13 17 10 14"/></svg>
           <span class="msg-status-text">Delivered</span>
         </div>`;

    // Action buttons (edit + delete) — tiny, appear on hover
    const actionsHtml = `
      <div class="msg-actions">
        ${isUser ? `<button class="msg-action-btn" data-act="edit" title="Edit message"><svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"/></svg></button>` : ''}
        <button class="msg-action-btn" data-act="copy" title="Copy text"><svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg></button>
        <button class="msg-action-btn danger" data-act="delete" title="Delete"><svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg></button>
      </div>`;

    msg.innerHTML = `
      <div class="msg-avatar">${avatar}</div>
      <div class="msg-content">
        <div class="msg-bubble">${escapeHtml(text)}</div>
        ${sourcesHtml}
        ${metaHtml}
        <div class="msg-footer">
          ${statusHtml}
          ${actionsHtml}
        </div>
      </div>
    `;
    wrap.appendChild(msg);
    wrap.scrollTop = wrap.scrollHeight;

    // Wire action buttons
    msg.querySelectorAll('.msg-action-btn').forEach(btn => {
      btn.addEventListener('click', function(e) {
        e.stopPropagation();
        const act = this.dataset.act;
        if (act === 'delete') {
          msg.style.opacity = '0';
          msg.style.transform = 'translateX(20px)';
          setTimeout(() => msg.remove(), 280);
        } else if (act === 'copy') {
          navigator.clipboard.writeText(text).then(() => {
            toast('Copied to clipboard');
          });
        } else if (act === 'edit') {
          const input = $('#chatInput');
          input.value = text;
          autoResize(input);
          updateSendButton();
          input.focus();
          msg.style.opacity = '0';
          msg.style.transform = 'translateX(20px)';
          setTimeout(() => msg.remove(), 280);
        }
      });
    });

    // Simulate read receipt — user messages get "read" after 1s
    if (isUser) {
      setTimeout(() => {
        const status = msg.querySelector('.msg-status');
        if (status) {
          status.dataset.status = 'read';
          status.innerHTML = `
            <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/><polyline points="24 6 13 17 10 14"/></svg>
            <span class="msg-status-text">Read</span>
          `;
          status.style.color = 'var(--accent)';
          status.title = 'Read · ' + new Date().toLocaleTimeString();
        }
      }, 1000);
    }

    state.messages.push({ role, text, sources, meta });
    return msg;
  }

  async function handleSend() {
    const input = $('#chatInput');
    const text = input.value.trim();
    if (!text || state.isStreaming) return;
    input.value = '';
    autoResize(input);

    appendMessage('user', text);
    const assistantMsg = appendMessage('assistant', '', [], {});
    const bubble = assistantMsg.querySelector('.msg-bubble');
    bubble.innerHTML = `<div class="typing-dots"><span></span><span></span><span></span></div>`;
    state.isStreaming = true;

    try {
      const result = await api.ask(text, token => {
        // Replace typing dots with stream
        if (bubble.querySelector('.typing-dots')) bubble.innerHTML = '';
        bubble.innerHTML += escapeHtml(token);
        const wrap = $('#chatMessages');
        wrap.scrollTop = wrap.scrollHeight;
      });

      // Final render (clean — re-escape full text)
      bubble.innerHTML = escapeHtml(result.answer);

      // Sources & meta
      let sourcesEl = assistantMsg.querySelector('.msg-sources');
      if (sourcesEl) sourcesEl.remove();
      if (result.sources.length) {
        const div = document.createElement('div');
        div.className = 'msg-sources';
        div.innerHTML = result.sources.map((s, i) =>
          `<span class="source-chip" title="${escapeHtml(s.text)}">[${i + 1}] ${s.documentId} · ${s.score.toFixed(2)}</span>`
        ).join('');
        // Insert before footer
        const footer = assistantMsg.querySelector('.msg-footer');
        if (footer) footer.before(div); else bubble.after(div);
      }
      // Remove old meta if exists
      const oldMeta = assistantMsg.querySelector('.msg-meta');
      if (oldMeta) oldMeta.remove();
      const metaDiv = document.createElement('div');
      metaDiv.className = 'msg-meta';
      metaDiv.innerHTML = `
        <span>${result.provider}/${result.model}</span>
        <span>· ${result.latencyMs} ms</span>
        <span>· ${result.tokens.total} tok</span>
      `;
      // Insert before footer
      const footer2 = assistantMsg.querySelector('.msg-footer');
      if (footer2) footer2.before(metaDiv); else bubble.after(metaDiv);

      // Persist query
      await api.Queries.record({
        question: text,
        answer: result.answer,
        sources: result.sources.map(s => s.id),
        model: result.model,
        provider: result.provider,
        tokens: result.tokens,
        latencyMs: result.latencyMs,
        confidence: result.sources[0]?.score || 0
      });
    } catch (err) {
      bubble.innerHTML = `<span style="color:var(--danger)">⚠ ${escapeHtml(err.message)}</span>`;
      toast(err.message, 'err');
    } finally {
      state.isStreaming = false;
      updateSendButton();
    }
  }

  function updateSendButton() {
    $('#chatSend').disabled = state.isStreaming || !$('#chatInput').value.trim();
  }

  function autoResize(el) {
    el.style.height = 'auto';
    el.style.height = Math.min(140, el.scrollHeight) + 'px';
  }

  function wireChat() {
    const input = $('#chatInput');
    const send = $('#chatSend');

    input.addEventListener('input', () => {
      autoResize(input);
      updateSendButton();
    });
    input.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        handleSend();
      }
    });
    send.addEventListener('click', handleSend);

    // Quick-prompt chips
    $$('.chat-prompt-chip').forEach(chip => {
      chip.addEventListener('click', () => {
        input.value = chip.dataset.q;
        autoResize(input);
        updateSendButton();
        input.focus();
      });
    });
  }

  // ─── Hero stats ──────────────────────────────────────────────────────────
  function renderHeroStats() {
    if (!state.analytics) return;
    const a = state.analytics.kpiSummary;
    animateCount($('#statDocs'), a.totalDocuments);
    animateCount($('#statChunks'), a.totalChunks);
    animateCount($('#statQueries'), a.totalQueries);
    animateCount($('#statCost'), a.estimatedCostUsd, 1100, '', 2);
  }

  // ─── Analytics charts ────────────────────────────────────────────────────
  async function renderAnalytics() {
    state.analytics = await api.Analytics.get();
    renderHeroStats();

    charts.renderHealthGauge($('#chartHealth'), 0.93);
    charts.renderDocTypesRose($('#chartDocTypes'), state.analytics.docTypes);
    charts.renderUploadsTrend($('#chartUploads'), state.analytics.uploadsTrend);
    charts.renderQueryHeatmap($('#chartHeatmap'), state.analytics.queryActivity);
    charts.renderChunksRadial($('#chartChunks'), state.analytics.chunksPerDoc);
    charts.renderWeeklyTrend($('#chartWeekly'), state.analytics.weeklyTrend);
    charts.renderConfidenceGauge($('#chartConfidence'), state.analytics.kpiSummary.avgConfidence);

    // Model usage legend
    const usage = state.analytics.modelUsage;
    const totalReq = usage.reduce((s, m) => s + m.requests, 0);
    const legend = $('#modelUsageLegend');
    legend.innerHTML = usage.map(m => `
      <div style="display:flex;align-items:center;gap:6px;font-size:11px;color:var(--text-soft)">
        <span style="width:8px;height:8px;border-radius:50%;background:${m.color};box-shadow:0 0 6px ${m.color}"></span>
        ${m.model} <span style="color:var(--muted)">· ${m.requests} req · ${Math.round(m.requests / totalReq * 100)}%</span>
      </div>
    `).join('');
  }

  // ─── Settings modal ─────────────────────────────────────────────────────
  function wireSettings() {
    const backdrop = $('#settingsModal');
    const openBtn = $('#btnSettings');
    const closeBtn = $('#settingsClose');
    const saveBtn = $('#settingsSave');

    function open() {
      const cfg = api.Config.get();
      $('#setProviderDemo').classList.toggle('is-active', cfg.provider === 'demo');
      $('#setProviderGroq').classList.toggle('is-active', cfg.provider === 'groq');
      $('#setProviderOllama').classList.toggle('is-active', cfg.provider === 'ollama');
      $('#setGroqKey').value = cfg.groqKey;
      $('#setGroqModel').value = cfg.groqModel;
      $('#setOllamaBase').value = cfg.ollamaBase;
      $('#setOllamaModel').value = cfg.ollamaModel;
      $('#setGhRepo').value = cfg.ghRepo;
      $('#setGhBranch').value = cfg.ghBranch;
      $('#setGhToken').value = cfg.ghToken;
      backdrop.classList.add('is-open');
    }
    function close() { backdrop.classList.remove('is-open'); }

    openBtn.addEventListener('click', open);
    closeBtn.addEventListener('click', close);
    backdrop.addEventListener('click', e => { if (e.target === backdrop) close(); });

    $$('.provider-toggle button').forEach(btn => {
      btn.addEventListener('click', () => {
        $$('.provider-toggle button').forEach(b => b.classList.remove('is-active'));
        btn.classList.add('is-active');
      });
    });

    saveBtn.addEventListener('click', () => {
      const provider = $('.provider-toggle button.is-active')?.dataset.provider || 'demo';
      api.Config.set({
        provider,
        groqKey: $('#setGroqKey').value.trim(),
        groqModel: $('#setGroqModel').value,
        ollamaBase: $('#setOllamaBase').value.trim(),
        ollamaModel: $('#setOllamaModel').value.trim(),
        ghRepo: $('#setGhRepo').value.trim(),
        ghBranch: $('#setGhBranch').value.trim() || 'main',
        ghToken: $('#setGhToken').value.trim()
      });
      updateProviderPill();
      close();
      toast('Settings saved');
    });
  }

  // ─── Boot ─────────────────────────────────────────────────────────────────
  async function boot() {
    updateProviderPill();
    charts.startHeroNetwork($('#heroCanvas'));

    try {
      await loadDocuments();
      await renderAnalytics();
      // Seed a welcome chat message
      appendMessage('assistant',
        'Hi! I\'m DocChat. Ask me about the indexed documents — financial performance, AR aging, security incidents, e-bill adoption, or anything else. Try a quick-prompt below.',
        [], { model: 'demo', provider: 'demo' });
    } catch (err) {
      toast('Failed to initialize: ' + err.message, 'err');
      console.error(err);
    }

    // Check if Groq config loaded after boot (async)
    setTimeout(function() {
      var cfg = window.DocChatAPI?.Config?.get();
      if (cfg && cfg.provider === 'groq' && cfg.groqKey) {
        updateProviderPill();
        var status = document.getElementById('composerStatus');
        if (status) status.textContent = 'Groq · live';
      }
    }, 2000);
    
    wireDropzone();
    wireChat();
    wireSettings();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
