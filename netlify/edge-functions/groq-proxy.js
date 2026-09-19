// ════════════════════════════════════════════════════════════════════════════
//  groq-proxy — Netlify Edge Function
//  Proxies chat completion requests to Groq, keeping the API key server-side.
//
//  The static site on GitHub Pages (https://insightanalyticsca.github.io/dashboards/)
//  calls this function instead of api.groq.com directly, so the Groq key never
//  reaches the browser. The key is read from the GROQ_API_KEY environment
//  variable set on the Netlify site.
//
//  Supports both:
//    - streaming (stream:true) — passes SSE through as text/event-stream
//    - non-streaming (stream:false) — used by the verifyGroq ping
//
//  CORS is restricted to the GitHub Pages origin (and localhost for dev).
//  ════════════════════════════════════════════════════════════════════════════

const GROQ_URL = 'https://api.groq.com/openai/v1/chat/completions';

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
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type',
    'Access-Control-Max-Age': '86400',
    'Vary': 'Origin'
  };
}

export default async (request, context) => {
  const origin = request.headers.get('origin') || '';

  // CORS preflight
  if (request.method === 'OPTIONS') {
    return new Response(null, { status: 204, headers: corsHeaders(origin) });
  }

  // GET /groq-proxy?op=models → forward to Groq /v1/models
  // (used to discover which models are actually available on this account)
  if (request.method === 'GET') {
    const url = new URL(request.url);
    if (url.searchParams.get('op') === 'models') {
      const GROQ_KEY = Deno.env.get('GROQ_API_KEY') || Deno.env.get('GROQ_KEY');
      if (!GROQ_KEY) {
        return new Response(
          JSON.stringify({ error: 'GROQ_API_KEY env var not set on the edge function' }),
          { status: 500, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
        );
      }
      try {
        const groqRes = await fetch('https://api.groq.com/openai/v1/models', {
          headers: { 'Authorization': 'Bearer ' + GROQ_KEY }
        });
        const body = await groqRes.text();
        return new Response(body, {
          status: groqRes.status,
          headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
        });
      } catch (e) {
        return new Response(
          JSON.stringify({ error: 'Failed to reach Groq', detail: e.message }),
          { status: 502, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
        );
      }
    }
    return new Response(JSON.stringify({ ok: true, service: 'groq-proxy' }), {
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  if (request.method !== 'POST') {
    return new Response(JSON.stringify({ error: 'Method not allowed' }), {
      status: 405,
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  // Read the Groq key from the Netlify env var (set via Netlify dashboard or CLI)
  const GROQ_KEY = Deno.env.get('GROQ_API_KEY') || Deno.env.get('GROQ_KEY');
  if (!GROQ_KEY) {
    return new Response(
      JSON.stringify({ error: 'GROQ_API_KEY env var not set on the edge function' }),
      {
        status: 500,
        headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
      }
    );
  }

  // Parse the incoming request body
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

  // Forward to Groq — the proxy injects the Authorization header here
  let groqRes;
  try {
    groqRes = await fetch(GROQ_URL, {
      method: 'POST',
      headers: {
        'Authorization': 'Bearer ' + GROQ_KEY,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        model: body.model || 'qwen/qwen3.8-27b',
        messages: body.messages,
        temperature: body.temperature ?? 0.3,
        max_tokens: body.max_tokens ?? 800,
        stream: body.stream ?? true
      })
    });
  } catch (e) {
    return new Response(
      JSON.stringify({ error: 'Failed to reach Groq', detail: e.message }),
      {
        status: 502,
        headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
      }
    );
  }

  // Pass through the response — Groq streams text/event-stream when stream:true,
  // we pass the ReadableStream through verbatim so the browser sees the same SSE.
  const respHeaders = {
    'Content-Type': groqRes.headers.get('content-type') || 'application/json',
    ...corsHeaders(origin)
  };
  if ((groqRes.headers.get('content-type') || '').includes('text/event-stream')) {
    respHeaders['Cache-Control'] = 'no-cache';
    respHeaders['Connection'] = 'keep-alive';
  }

  return new Response(groqRes.body, {
    status: groqRes.status,
    headers: respHeaders
  });
};

export const config = {
  path: '/groq-proxy'
};
