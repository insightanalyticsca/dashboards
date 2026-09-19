// ════════════════════════════════════════════════════════════════════════════
//  groq-proxy → gemini-proxy — Netlify Edge Function
//
//  Originally forwarded to Groq. As of this commit, forwards to Google
//  Gemini's OpenAI-compatible endpoint instead — same request/response
//  format, just a different upstream. The function name + path stay as
//  "groq-proxy" for backward compat (the client JS still calls /groq-proxy).
//
//  The Gemini key is held server-side as the GEMINI_API_KEY environment
//  variable on the Netlify site. The browser never sees it.
//
//  Gemini's OpenAI-compatible endpoint:
//    POST https://generativelanguage.googleapis.com/v1beta/openai/chat/completions
//    Authorization: Bearer {GEMINI_API_KEY}
//    Body: { model, messages, temperature, max_tokens, stream }
//    Response: standard OpenAI format (SSE for streaming, JSON otherwise)
//
//  CORS restricted to the GitHub Pages origin (and localhost for dev).
//  ════════════════════════════════════════════════════════════════════════════

const UPSTREAM_URL = 'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions';
const MODELS_URL = 'https://generativelanguage.googleapis.com/v1beta/models';
const DEFAULT_MODEL = 'gemini-1.5-flash';

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

export default async (request, context) => {
  const origin = request.headers.get('origin') || '';

  // CORS preflight
  if (request.method === 'OPTIONS') {
    return new Response(null, { status: 204, headers: corsHeaders(origin) });
  }

  // Read the API key from the Netlify env var
  const API_KEY = Deno.env.get('GEMINI_API_KEY') || Deno.env.get('GROQ_API_KEY') || Deno.env.get('GROQ_KEY');
  if (!API_KEY) {
    return new Response(
      JSON.stringify({ error: 'GEMINI_API_KEY env var not set on the edge function' }),
      { status: 500, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
    );
  }

  // GET /groq-proxy?op=models → list available Gemini models
  if (request.method === 'GET') {
    const url = new URL(request.url);
    if (url.searchParams.get('op') === 'models') {
      try {
        const res = await fetch(MODELS_URL + '?pageSize=100', {
          headers: { 'Authorization': 'Bearer ' + API_KEY }
        });
        const body = await res.text();
        return new Response(body, {
          status: res.status,
          headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
        });
      } catch (e) {
        return new Response(
          JSON.stringify({ error: 'Failed to reach Gemini', detail: e.message }),
          { status: 502, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
        );
      }
    }
    return new Response(JSON.stringify({ ok: true, service: 'gemini-proxy' }), {
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
  }

  if (request.method !== 'POST') {
    return new Response(JSON.stringify({ error: 'Method not allowed' }), {
      status: 405,
      headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
    });
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

  // Forward to Gemini's OpenAI-compatible endpoint — same body format,
  // Gemini accepts { model, messages, temperature, max_tokens, stream }
  let upstreamRes;
  try {
    upstreamRes = await fetch(UPSTREAM_URL, {
      method: 'POST',
      headers: {
        'Authorization': 'Bearer ' + API_KEY,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        model: body.model || DEFAULT_MODEL,
        messages: body.messages,
        temperature: body.temperature ?? 0.3,
        max_tokens: body.max_tokens ?? 800,
        stream: body.stream ?? true
      })
    });
  } catch (e) {
    return new Response(
      JSON.stringify({ error: 'Failed to reach Gemini', detail: e.message }),
      { status: 502, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
    );
  }

  // Pass through the response — Gemini streams text/event-stream when stream:true,
  // we pass the ReadableStream through verbatim so the browser sees the same SSE.
  const respHeaders = {
    'Content-Type': upstreamRes.headers.get('content-type') || 'application/json',
    ...corsHeaders(origin)
  };
  if ((upstreamRes.headers.get('content-type') || '').includes('text/event-stream')) {
    respHeaders['Cache-Control'] = 'no-cache';
    respHeaders['Connection'] = 'keep-alive';
  }

  return new Response(upstreamRes.body, {
    status: upstreamRes.status,
    headers: respHeaders
  });
};

export const config = {
  path: '/groq-proxy'
};
