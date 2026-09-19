// ════════════════════════════════════════════════════════════════════════════
//  groq-proxy → openrouter-proxy — Netlify Edge Function
//
//  Originally forwarded to Groq, then to Gemini, now to OpenRouter.
//  The function name + path stay as "groq-proxy" for backward compat
//  (the client JS still calls /groq-proxy).
//
//  OpenRouter's OpenAI-compatible endpoint:
//    POST https://openrouter.ai/api/v1/chat/completions
//    Authorization: Bearer {OPENROUTER_API_KEY}
//    Body: { model, messages, temperature, max_tokens, stream }
//    Response: standard OpenAI format (SSE for streaming, JSON otherwise)
//
//  Free models on OpenRouter (marked with :free suffix):
//    - meta-llama/llama-3.1-8b-instruct:free       (default — non-reasoning)
//    - meta-llama/llama-3.3-70b-instruct:free      (sometimes available)
//    - google/gemma-2-9b-it:free
//    - mistralai/mistral-7b-instruct:free
//    - qwen/qwen-2.5-7b-instruct:free
//
//  Rate limits (free tier):
//    - Without $5 credits: 50 requests/day across all :free models
//    - With $5+ credits:   1000 requests/day across all :free models
//    - Resets at 00:00 UTC daily
//
//  CORS restricted to the GitHub Pages origin (and localhost for dev).
//  ════════════════════════════════════════════════════════════════════════════

const UPSTREAM_URL = 'https://openrouter.ai/api/v1/chat/completions';
const MODELS_URL = 'https://openrouter.ai/api/v1/models';
const DEFAULT_MODEL = 'meta-llama/llama-3.1-8b-instruct:free';

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
  const API_KEY = Deno.env.get('OPENROUTER_API_KEY') || Deno.env.get('GEMINI_API_KEY') || Deno.env.get('GROQ_API_KEY') || Deno.env.get('GROQ_KEY');
  if (!API_KEY) {
    return new Response(
      JSON.stringify({ error: 'OPENROUTER_API_KEY env var not set on the edge function' }),
      { status: 500, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
    );
  }

  // GET /groq-proxy?op=models → list available OpenRouter models
  if (request.method === 'GET') {
    const url = new URL(request.url);
    if (url.searchParams.get('op') === 'models') {
      try {
        const res = await fetch(MODELS_URL, {
          headers: { 'Authorization': 'Bearer ' + API_KEY }
        });
        const body = await res.text();
        return new Response(body, {
          status: res.status,
          headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) }
        });
      } catch (e) {
        return new Response(
          JSON.stringify({ error: 'Failed to reach OpenRouter', detail: e.message }),
          { status: 502, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
        );
      }
    }
    return new Response(JSON.stringify({ ok: true, service: 'openrouter-proxy' }), {
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

  // Filter out provider-specific params that OpenRouter doesn't understand
  // (e.g., Gemini's reasoning_effort — keep the body clean OpenAI format)
  const cleanBody = {
    model: body.model || DEFAULT_MODEL,
    messages: body.messages,
    temperature: body.temperature ?? 0.3,
    max_tokens: body.max_tokens ?? 800,
    stream: body.stream ?? true
  };

  // Forward to OpenRouter
  let upstreamRes;
  try {
    upstreamRes = await fetch(UPSTREAM_URL, {
      method: 'POST',
      headers: {
        'Authorization': 'Bearer ' + API_KEY,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(cleanBody)
    });
  } catch (e) {
    return new Response(
      JSON.stringify({ error: 'Failed to reach OpenRouter', detail: e.message }),
      { status: 502, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }
    );
  }

  // Pass through the response — OpenRouter streams text/event-stream when
  // stream:true, we pass the ReadableStream through verbatim
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
