# Cloudflare Worker Deployment Guide — groq-proxy

This replaces the Netlify Edge Function. No deploy limits, no monthly
credit exhaustion. Free tier: 100K requests/day, 1GB KV storage.

## Prerequisites

1. A Cloudflare account (you already have one for DNS)
2. `wrangler` CLI installed: `npm install -g wrangler`
3. Authenticate: `wrangler login`

## Steps

### 1. Create the KV namespace

```bash
cd cloudflare/
wrangler kv:namespace create VISITOR_STATS
```

This outputs something like:
```
{ binding = "VISITOR_STATS", id = "abc123..." }
```

Copy the `id` value and paste it into `wrangler.toml`:
```toml
[[kv_namespaces]]
binding = "VISITOR_STATS"
id = "abc123..."  ← paste here
```

### 2. Set the Groq API key as a secret

```bash
wrangler secret put GROQ_API_KEY
```

Paste your Groq API key when prompted (starts with `gsk_`).

### 3. Deploy the worker

```bash
wrangler deploy
```

This outputs a URL like:
```
https://groq-proxy.<your-subdomain>.workers.dev
```

### 4. (Optional) Set a custom domain

In the Cloudflare dashboard:
- Workers & Pages → groq-proxy → Settings → Triggers → Custom Domains
- Add: `groq.insight-analytics.ca` (or any subdomain)
- Cloudflare auto-provisions the SSL cert

### 5. Update the frontend to use the new URL

Replace ALL references to the old Netlify proxy URL:
```
https://startling-belekoy-b0ec70.netlify.app/groq-proxy
```
With the new Cloudflare Worker URL:
```
https://groq-proxy.<your-subdomain>.workers.dev/groq-proxy
```
(Or the custom domain if you set one: `https://groq.insight-analytics.ca/groq-proxy`)

Files to update:
- `insight-analytics-site/index.html` (var EP = ...)
- `insight-analytics-site/js/ai-search.js` (var GROQ_PROXY = ...)
- `insight-analytics-site/js/blog-markets-dashboard.js`
- `insight-analytics-site/js/blog-pipeline-builder.js`
- `insight-analytics-site/js/blog-load-forecast.js`
- `insight-analytics-site/js/blog-fuzzy-match.js`
- `insight-analytics-site/js/assistant.js`
- `insight-analytics-site/dashboards-preview/js/contact-chat.js`
- `insight-analytics-site/dashboards-preview/js/visual-chat.js`
- `insight-analytics-site/dashboards-preview/js/exec-ai-brief.js`
- `insight-analytics-site/data/groq-config.json`
- `gh-pages-live/data/groq-config.json`
- `gh-pages-live/js/contact-chat.js`
- `gh-pages-live/js/visual-chat.js`
- `gh-pages-live/js/exec-ai-brief.js`
- `gh-pages-live/js/blog-markets-dashboard.js`

### 6. Verify

```bash
# Health check
curl https://groq-proxy.<your-subdomain>.workers.dev/groq-proxy

# Reset visitor counter
curl "https://groq-proxy.<your-subdomain>.workers.dev/groq-proxy?op=reset&password=Domino88!!"

# Check stats (should show 0 after reset)
curl "https://groq-proxy.<your-subdomain>.workers.dev/groq-proxy?op=stats"
```

## What this gives you

- **No deploy limits** — deploy anytime, unlimited times
- **No monthly credit exhaustion** — Cloudflare Workers free tier is 100K req/day
- **Persistent KV storage** — visitor stats survive worker restarts
- **UUID fingerprinting** — same device = same visitor regardless of IP
- **Geolocation** — Cloudflare's `request.cf` provides country/city
- **op=reset endpoint** — zero all counters on demand
- **Bot filtering** — bots never counted
- **IP exclusion** — owner's IPs not counted
