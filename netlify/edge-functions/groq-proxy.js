// ════════════════════════════════════════════════════════════════════════════
//  groq-proxy — Netlify Edge Function (Groq upstream + in-memory cache)
//
//  Switched back to Groq after OpenRouter's poolside/laguna-s-2.1:free
//  turned out to be too rate-limited (429 upstream). Groq's qwen-3.8-27b
//  was working fine before — just needs the cache layer to stay under
//  the 200K TPD free-tier cap.
//
//  In-memory cache (per-edge-instance, no netlify:blobs import needed):
//    - Keyed by SHA-256 of request body
//    - TTL: 24 hours
//    - Cache hit: stream the cached text back as simulated SSE
//    - Cache miss: call Groq, stream to client, store in memory
//    - Lower hit rate than Netlify Blobs (per-instance, not shared across
//      edges) but works without the bundler issues that blocked Blobs
//
//  Verify pings (max_tokens <= 5) SKIP the cache — real health checks.
//  ════════════════════════════════════════════════════════════════════════════

const GROQ_URL = 'https://api.groq.com/openai/v1/chat/completions';
const DEFAULT_MODEL = 'qwen/qwen3.8-27b';
const CACHE_TTL_MS = 24 * 60 * 60 * 1000;  // 24 hours
const VERIFY_MAX_TOKENS = 5;

const ALLOWED_ORIGINS = [
  'https://insightanalyticsca.github.io',
  'http://localhost:3000',
  'http://127.0.0.1:3000',
  'http://localhost:5173',
  'http://127.0.0.1:5173'
];

function corsHeaders(origin) {
  const allow = ALLOWED_ORIGINS.includes(origin) ? origin : ALLOWED_ORIGINS[0];
  return {
    'Access-Control-Allow-Origin': allow,
    'Access-Control-Allow-Methods': 'POST, OPTIONS, GET',
    'Access-Control-Allow-Headers': 'Content-Type',
    'Access-Control-Max-Age': '86400',
    'Vary': 'Origin'
  };
}

// ─── In-memory cache (per-edge-instance) ─────────────────────────────────
// Netlify Blobs would be shared across edges, but the import failed the
// local bundler. In-memory is per-instance but still saves tokens within
// a single edge — most users in the same region hit the same instance.
const memCache = new Map();

async function sha256(text) {
  const data = new TextEncoder().encode(text);
  const hash = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(hash))
    .map(b => b.toString(16).padStart(2, '0'))
    .join('');
}

// Simulate SSE streaming from a cached text — preserves the streaming UX
function simulatedSSEStream(fullText, model) {
  const id = 'cache-' + Date.now();
  const encoder = new TextEncoder();
  return new ReadableStream({
    async start(controller) {
      const chunks = fullText.match(/\S+\s*/g) || [fullText];
      for (const chunk of chunks) {
        const openaiChunk = {
          id,
          object: 'chat.completion.chunk',
          created: Math.floor(Date.now() / 1000),
          model,
          choices: [{
            index: 0,
            delta: { content: chunk },
            finish_reason: null
          }]
        };
        controller.enqueue(encoder.encode('data: ' + JSON.stringify(openaiChunk) + '\n\n'));
        await new Promise(r => setTimeout(r, 8));
      }
      const finalChunk = {
        id,
        object: 'chat.completion.chunk',
        created: Math.floor(Date.now() / 1000),
        model,
        choices: [{ index: 0, delta: {}, finish_reason: 'stop' }]
      };
      controller.enqueue(encoder.encode('data: ' + JSON.stringify(finalChunk) + '\n\n'));
      controller.enqueue(encoder.encode('data: [DONE]\n\n'));
      controller.close();
    }
  });
}

export default async (request, context) => {
  const origin = request.headers.get('origin') || '';

  if (request.method === 'OPTIONS') {
    return new Response(null, { status: 204, headers: corsHeaders(origin) });
  }

  const GROQ_KEY = Deno.env.get('GROQ_API_KEY') || Deno.env.get('GROQ_KEY');
  if (!GROQ_KEY) {
    return new Response(
      JSON.stringify({ error: 'GROQ_API_KEY env var not set on the edge function' }),
      { status: 500, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
    );
  }

  // GET /groq-proxy?op=models → list Groq models
  if (request.method === 'GET') {
    const url = new URL(request.url);
    if (url.searchParams.get('op') === 'models') {
      try {
        const res = await fetch('https://api.groq.com/openai/v1/models', {
          headers: { 'Authorization': 'Bearer ' + GROQ_KEY }
        });
        const body = await res.text();
        return new Response(body, {
          status: res.status,
          headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
        });
      } catch (e) {
        return new Response(
          JSON.stringify({ error: 'Failed to reach Groq', detail: e.message }),
          { status: 502, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
        );
      }
    }
    return new Response(JSON.stringify({ ok: true, service: 'groq-proxy', cache: 'memory' }), {
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  if (request.method !== 'POST') {
    return new Response(JSON.stringify({ error: 'Method not allowed' }), {
      status: 405,
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  let body;
  try {
    body = await request.json();
  } catch (e) {
    return new Response(JSON.stringify({ error: 'Invalid JSON body' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  if (!Array.isArray(body.messages) || body.messages.length === 0) {
    return new Response(JSON.stringify({ error: 'messages[] required' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  const requestedModel = body.model || DEFAULT_MODEL;
  const isStreaming = body.stream ?? true;
  const maxTokens = body.max_tokens ?? 800;
  const isVerifyPing = maxTokens <= VERIFY_MAX_TOKENS;

  // ─── CACHE CHECK (skip for verify pings) ─────────────────────────────────
  if (!isVerifyPing) {
    const cacheKey = 'chat:' + await sha256(JSON.stringify({
      model: requestedModel,
      messages: body.messages,
      temperature: body.temperature ?? 0.3,
      max_tokens: maxTokens,
      stream: isStreaming
    }));
    const cached = memCache.get(cacheKey);
    if (cached && (Date.now() - cached.ts < CACHE_TTL_MS)) {
      // Cache hit! Stream the cached text back as simulated SSE
      if (isStreaming) {
        return new Response(simulatedSSEStream(cached.text, requestedModel), {
          status: 200,
          headers: {
            'Content-Type': 'text/event-stream',
            'Cache-Control': 'no-cache',
            'Connection': 'keep-alive',
            'X-Cache': 'HIT',
            ...corsHeaders(origin)
          }
        });
      }
      // Non-streaming cache hit
      const openaiResponse = {
        id: 'cache-' + cached.ts,
        object: 'chat.completion',
        created: Math.floor(cached.ts / 1000),
        model: requestedModel,
        choices: [{
          index: 0,
          message: { role: 'assistant', content: cached.text },
          finish_reason: 'stop'
        }],
        usage: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 }
      };
      return new Response(JSON.stringify(openaiResponse), {
        status: 200,
        headers: {
          'Content-Type': 'application/json',
          'X-Cache': 'HIT',
          ...corsHeaders(origin)
        }
      });
    }
  }

  // ─── CACHE MISS — call Groq ──────────────────────────────────────────────
  let groqRes;
  try {
    groqRes = await fetch(GROQ_URL, {
      method: 'POST',
      headers: {
        'Authorization': 'Bearer ' + GROQ_KEY,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        model: requestedModel,
        messages: body.messages,
        temperature: body.temperature ?? 0.3,
        max_tokens: maxTokens,
        stream: isStreaming
      })
    });
  } catch (e) {
    return new Response(
      JSON.stringify({ error: 'Failed to reach Groq', detail: e.message }),
      { status: 502, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
    );
  }

  if (!groqRes.ok) {
    const errBody = await groqRes.text();
    return new Response(errBody, {
      status: groqRes.status,
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  // ─── STREAMING: stream to client AND collect for caching ────────────────
  if (isStreaming) {
    const groqReader = groqRes.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let fullText = '';

    const transformedStream = new ReadableStream({
      async start(controller) {
        const encoder = new TextEncoder();
        try {
          while (true) {
            const { done, value } = await groqReader.read();
            if (done) break;
            const chunk = decoder.decode(value, { stream: true });
            buffer += chunk;
            controller.enqueue(encoder.encode(chunk));
            const lines = buffer.split('\n');
            buffer = lines.pop() || '';
            for (const line of lines) {
              const trimmed = line.trim();
              if (!trimmed.startsWith('data:')) continue;
              const data = trimmed.slice(5).trim();
              if (data === '[DONE]') continue;
              try {
                const evt = JSON.parse(data);
                const delta = evt.choices?.[0]?.delta?.content || '';
                if (delta) fullText += delta;
              } catch (_) {}
            }
          }
          if (buffer) controller.enqueue(encoder.encode(buffer));
        } finally {
          controller.close();
          // Store in memory cache for next time
          if (!isVerifyPing && fullText.length > 20) {
            const cacheKey = 'chat:' + await sha256(JSON.stringify({
              model: requestedModel,
              messages: body.messages,
              temperature: body.temperature ?? 0.3,
              max_tokens: maxTokens,
              stream: isStreaming
            }));
            memCache.set(cacheKey, { ts: Date.now(), text: fullText });
            // Garbage-collect old entries (keep cache under 100 entries)
            if (memCache.size > 100) {
              const oldest = [...memCache.entries()].sort((a, b) => a[1].ts - b[1].ts)[0];
              if (oldest) memCache.delete(oldest[0]);
            }
          }
        }
      }
    });

    return new Response(transformedStream, {
      status: 200,
      headers: {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache',
        'Connection': 'keep-alive',
        'X-Cache': 'MISS',
        ...corsHeaders(origin)
      }
    });
  }

  // ─── NON-STREAMING: parse, cache, return ────────────────────────────────
  const groqJson = await groqRes.json();
  if (!isVerifyPing) {
    const text = groqJson.choices?.[0]?.message?.content || '';
    if (text.length > 20) {
      const cacheKey = 'chat:' + await sha256(JSON.stringify({
        model: requestedModel,
        messages: body.messages,
        temperature: body.temperature ?? 0.3,
        max_tokens: maxTokens,
        stream: isStreaming
      }));
      memCache.set(cacheKey, { ts: Date.now(), text });
      if (memCache.size > 100) {
        const oldest = [...memCache.entries()].sort((a, b) => a[1].ts - b[1].ts)[0];
        if (oldest) memCache.delete(oldest[0]);
      }
    }
  }
  return new Response(JSON.stringify(groqJson), {
    status: groqRes.status,
    headers: {
      'Content-Type': 'application/json',
      'X-Cache': 'MISS',
      ...corsHeaders(origin)
    }
  });
};

export const config = {
  path: '/groq-proxy'
};
