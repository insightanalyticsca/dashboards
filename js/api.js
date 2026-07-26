/* ════════════════════════════════════════════════════════════════════════════
   api.js — JSON + GH Contents API + Groq streaming + Ollama fallback
   Single facade so app.js never cares where data lives.
   ════════════════════════════════════════════════════════════════════════════ */

(function (global) {
  'use strict';

  // ─── Config ──────────────────────────────────────────────────────────────
  const CONFIG = {
    // Where the JSON files live. Relative to the page so it works on GH Pages.
    dataBase: './data/',
    // Optional: GitHub repo for write-back via Contents API.
    // If left blank, writes are local-only (in-memory).
    // Pattern: owner/repo — e.g. 'insight_analytics/docchat-demo'
    ghRepo: localStorage.getItem('docchat.gh.repo') || '',
    ghBranch: localStorage.getItem('docchat.gh.branch') || 'main',
    ghToken: localStorage.getItem('docchat.gh.token') || '',
    // AI provider
    provider: localStorage.getItem('docchat.provider') || 'demo',
    groqKey: localStorage.getItem('docchat.groq.key') || '',
    groqModel: localStorage.getItem('docchat.groq.model') || 'llama-3.3-70b-versatile',
    ollamaBase: localStorage.getItem('docchat.ollama.base') || 'http://localhost:11434',
    ollamaModel: localStorage.getItem('docchat.ollama.model') || 'gemma3:1b',
    // Embedding endpoint (Groq doesn't offer embeddings — keep Ollama)
    ollamaEmbed: localStorage.getItem('docchat.ollama.embed') || 'nomic-embed-text'
  };

  // ─── In-memory cache + write buffer ──────────────────────────────────────
  const cache = { documents: null, chunks: null, queries: null, analytics: null };

  // ─── JSON fetch helpers (read) ───────────────────────────────────────────
  async function fetchJson(name) {
    if (cache[name]) return cache[name];
    const url = CONFIG.dataBase + name + '.json';
    const res = await fetch(url, { cache: 'no-store' });
    if (!res.ok) throw new Error(`Failed to load ${name}.json (${res.status})`);
    const json = await res.json();
    cache[name] = json;
    return json;
  }

  // ─── Write helpers ───────────────────────────────────────────────────────
  // If ghRepo + ghToken are configured, writes go through GitHub Contents API.
  // Otherwise we update the in-memory cache (demo mode) and toast a warning.

  async function writeJson(name, payload, commitMsg) {
    if (CONFIG.ghRepo && CONFIG.ghToken) {
      return writeViaGithub(name, payload, commitMsg);
    }
    cache[name] = payload;
    return { mode: 'demo', persisted: false };
  }

  async function writeViaGithub(name, payload, commitMsg) {
    const path = `data/${name}.json`;
    const url = `https://api.github.com/repos/${CONFIG.ghRepo}/contents/${path}`;

    // Step 1: get current sha (required for updates; new files don't need it).
    let sha;
    try {
      const probe = await fetch(url + `?ref=${CONFIG.ghBranch}`, {
        headers: { Authorization: `Bearer ${CONFIG.ghToken}`, Accept: 'application/vnd.github+json' }
      });
      if (probe.ok) {
        const meta = await probe.json();
        sha = meta.sha;
      }
    } catch (_) { /* new file */ }

    // Step 2: PUT updated content (base64-encoded).
    const body = {
      message: commitMsg || `chore(data): update ${name}.json`,
      content: btoa(unescape(encodeURIComponent(JSON.stringify(payload, null, 2)))),
      branch: CONFIG.ghBranch
    };
    if (sha) body.sha = sha;

    const res = await fetch(url, {
      method: 'PUT',
      headers: {
        Authorization: `Bearer ${CONFIG.ghToken}`,
        Accept: 'application/vnd.github+json',
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(body)
    });

    if (!res.ok) {
      const err = await res.text();
      throw new Error(`GitHub write failed (${res.status}): ${err}`);
    }
    cache[name] = payload;
    return { mode: 'github', persisted: true, commit: (await res.json()).commit };
  }

  // ─── CRUD: documents ─────────────────────────────────────────────────────
  const Documents = {
    async list() {
      const data = await fetchJson('documents');
      return data.documents;
    },
    async get(id) {
      const docs = await this.list();
      return docs.find(d => d.id === id);
    },
    async create(doc) {
      const data = await fetchJson('documents');
      const newDoc = {
        id: 'doc-' + String(Date.now()).slice(-8),
        uploadedAt: new Date().toISOString(),
        uploadedBy: 'demo@user',
        chunkCount: 0,
        status: 'pending',
        ...doc
      };
      data.documents.unshift(newDoc);
      data.version = (data.version || 1) + 1;
      data.generatedAt = new Date().toISOString();
      await writeJson('documents', data, `docs: add ${newDoc.title}`);
      return newDoc;
    },
    async update(id, patch) {
      const data = await fetchJson('documents');
      const idx = data.documents.findIndex(d => d.id === id);
      if (idx < 0) throw new Error('Document not found: ' + id);
      data.documents[idx] = { ...data.documents[idx], ...patch, id };
      data.version = (data.version || 1) + 1;
      data.generatedAt = new Date().toISOString();
      await writeJson('documents', data, `docs: update ${id}`);
      return data.documents[idx];
    },
    async remove(id) {
      const data = await fetchJson('documents');
      data.documents = data.documents.filter(d => d.id !== id);
      data.version = (data.version || 1) + 1;
      data.generatedAt = new Date().toISOString();
      await writeJson('documents', data, `docs: remove ${id}`);
      return true;
    }
  };

  // ─── CRUD: chunks ────────────────────────────────────────────────────────
  const Chunks = {
    async list() {
      const data = await fetchJson('chunks');
      return data.chunks;
    },
    async forDoc(docId) {
      const all = await this.list();
      return all.filter(c => c.documentId === docId);
    },
    async create(chunk) {
      const data = await fetchJson('chunks');
      const newChunk = { id: 'chk-' + Date.now(), ...chunk };
      data.chunks.push(newChunk);
      data.version = (data.version || 1) + 1;
      await writeJson('chunks', data, `chunks: add ${newChunk.id}`);
      return newChunk;
    }
  };

  // ─── CRUD: queries (chat history) ────────────────────────────────────────
  const Queries = {
    async list() {
      const data = await fetchJson('queries');
      return data.queries;
    },
    async record(query) {
      const data = await fetchJson('queries');
      const entry = {
        id: 'q-' + Date.now(),
        askedAt: new Date().toISOString(),
        ...query
      };
      data.queries.unshift(entry);
      data.version = (data.version || 1) + 1;
      await writeJson('queries', data, `queries: record ${entry.id}`);
      return entry;
    }
  };

  // ─── Analytics (read-only aggregate) ──────────────────────────────────────
  const Analytics = {
    async get() {
      return await fetchJson('analytics');
    }
  };

  // ══════════════════════════════════════════════════════════════════════════
  //  AI: providers
  // ══════════════════════════════════════════════════════════════════════════

  // ─── Simple keyword retrieval over local chunks (mimics vector retrieval) ─
  // We pretend to embed the question and rank chunks by token overlap.
  // This is NOT a real semantic search — it's a demo stand-in.
  function scoreChunk(text, query) {
    const t = text.toLowerCase();
    const q = query.toLowerCase();
    const qTokens = q.split(/\W+/).filter(w => w.length > 3);
    let score = 0;
    for (const w of qTokens) {
      if (t.includes(w)) score += 0.15;
      if (new RegExp('\\b' + w).test(t)) score += 0.10;
    }
    // Slight randomization to feel like cosine similarity
    return Math.min(0.99, score + (Math.random() * 0.05));
  }

  async function retrieve(query, topK = 4) {
    const chunks = await Chunks.list();
    const scored = chunks.map(c => ({
      ...c,
      score: scoreChunk(c.text, query)
    }));
    return scored
      .sort((a, b) => b.score - a.score)
      .slice(0, topK);
  }

  // ─── Demo mode: deterministic answer from retrieved chunks ────────────────
  function demoAnswer(question, sources) {
    if (sources.length === 0) {
      return {
        text: "I don't have any documents matching that question yet. Upload a document or try rephrasing.",
        tokens: { prompt: 0, completion: 0, total: 0 }
      };
    }
    const top = sources[0];
    const text = [
      `Based on "${top.text.slice(0, 80)}…":`,
      '',
      top.text,
      '',
      sources.length > 1
        ? `(Also retrieved ${sources.length - 1} additional supporting chunks.)`
        : ''
    ].join('\n').trim();
    return {
      text,
      tokens: { prompt: 96, completion: 52, total: 148 }
    };
  }

  // ─── Groq: streaming chat completion (OpenAI-compatible) ──────────────────
  async function groqChatStream(messages, onToken) {
    if (!CONFIG.groqKey) throw new Error('Groq API key not set. Open Settings to configure.');

    const res = await fetch('https://api.groq.com/openai/v1/chat/completions', {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${CONFIG.groqKey}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        model: CONFIG.groqModel,
        messages,
        temperature: 0.4,
        max_tokens: 800,
        stream: true
      })
    });

    if (!res.ok) {
      const err = await res.text();
      throw new Error(`Groq API error (${res.status}): ${err}`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let fullText = '';
    let promptTokens = 0;
    let completionTokens = 0;

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });

      const lines = buffer.split('\n');
      buffer = lines.pop() || '';

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed.startsWith('data:')) continue;
        const data = trimmed.slice(5).trim();
        if (data === '[DONE]') continue;
        try {
          const evt = JSON.parse(data);
          if (evt.choices && evt.choices[0]?.delta?.content) {
            const token = evt.choices[0].delta.content;
            fullText += token;
            completionTokens++;
            if (onToken) onToken(token);
          }
          if (evt.usage) {
            promptTokens = evt.usage.prompt_tokens || promptTokens;
            completionTokens = evt.usage.completion_tokens || completionTokens;
          }
        } catch (_) { /* ignore partial */ }
      }
    }

    return {
      text: fullText,
      tokens: {
        prompt: promptTokens,
        completion: completionTokens,
        total: promptTokens + completionTokens
      }
    };
  }

  // ─── Ollama: non-streaming fallback (kept from original .NET port) ───────
  async function ollamaChat(prompt) {
    const res = await fetch(`${CONFIG.ollamaBase}/api/generate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model: CONFIG.ollamaModel,
        prompt,
        stream: false
      })
    });
    if (!res.ok) throw new Error(`Ollama error (${res.status})`);
    const json = await res.json();
    return {
      text: json.response || '',
      tokens: { prompt: json.prompt_eval_count || 0, completion: json.eval_count || 0, total: 0 }
    };
  }

  // ─── Unified Ask facade ──────────────────────────────────────────────────
  //   question: string
  //   onToken(token): optional streaming callback
  //   returns { answer, sources, model, provider, tokens, latencyMs }
  async function ask(question, onToken) {
    const start = performance.now();
    const sources = await retrieve(question, 4);

    const context = sources.map((s, i) =>
      `[${i + 1}] (doc: ${s.documentId}, page: ${s.page || '?'}, score: ${s.score.toFixed(2)})\n${s.text}`
    ).join('\n\n');

    const systemPrompt = `You are DocChat, an assistant that answers strictly from the provided document excerpts.
Cite sources using [N] notation matching the bracketed indices above.
If the excerpts don't contain the answer, say so honestly. Do not invent facts.
Keep responses to 3-5 sentences unless the user asks for more detail.`;

    const userPrompt = `Question: ${question}\n\nDocument excerpts:\n${context || '(none)'}`;

    let result, model, provider;

    if (CONFIG.provider === 'groq' && CONFIG.groqKey) {
      const messages = [
        { role: 'system', content: systemPrompt },
        { role: 'user', content: userPrompt }
      ];
      result = await groqChatStream(messages, onToken);
      model = CONFIG.groqModel;
      provider = 'groq';
    } else if (CONFIG.provider === 'ollama') {
      const prompt = `${systemPrompt}\n\n${userPrompt}`;
      result = await ollamaChat(prompt);
      model = CONFIG.ollamaModel;
      provider = 'ollama';
    } else {
      // Demo mode — simulate streaming by tokenizing the canned answer
      result = demoAnswer(question, sources);
      model = 'demo';
      provider = 'demo';
      if (onToken) {
        const tokens = result.text.match(/\S+\s*/g) || [result.text];
        for (const t of tokens) {
          await new Promise(r => setTimeout(r, 24));
          onToken(t);
        }
      }
    }

    return {
      answer: result.text,
      sources,
      model,
      provider,
      tokens: result.tokens,
      latencyMs: Math.round(performance.now() - start)
    };
  }

  // ─── Config persistence ──────────────────────────────────────────────────
  const Config = {
    get() { return { ...CONFIG }; },
    set(patch) {
      Object.assign(CONFIG, patch);
      for (const k of Object.keys(patch)) {
        const storageKey = 'docchat.' + k.replace(/([A-Z])/g, '.$1').toLowerCase();
        localStorage.setItem(storageKey, patch[k]);
      }
    }
  };

  // ─── Export ──────────────────────────────────────────────────────────────
  global.DocChatAPI = {
    Documents, Chunks, Queries, Analytics,
    Config, retrieve, ask
  };

})(window);
