# Netlify Groq Proxy — deploy steps

This is the **only** thing deployed to Netlify. The static site stays on GitHub Pages at https://insightanalyticsca.github.io/dashboards/ — same address, no move.

## Why

The Groq API key was being shipped in `data/groq-config.json` (split + reversed to bypass GitHub secret scanning). That's a hack and the key got revoked. Moving the key server-side (Netlify Edge Function) means:

- The key never reaches the browser
- GitHub secret scanning can't block the repo (the key isn't there)
- The key can be rotated without touching the repo (just update the Netlify env var)

## Deploy (one-time, ~5 min)

1. Go to https://app.netlify.com → **Add new site** → **Deploy manually** (or import from GitHub — but importing will also try to deploy the static site, which we don't want since it's on GH Pages).

   Easier path: drag-and-drop the `netlify/` folder onto the Netlify dashboard.

2. Once the site exists, go to **Site settings → Environment variables → Add a variable**:
   - Key: `GROQ_API_KEY`
   - Value: paste your Groq key (the `gsk_...` string — it was last in commit `8fef66a`'s `data/groq-config.json` `keyParts` field, reversed and concatenated; or just grab a fresh one from https://console.groq.com/keys)
   - Scope: **Functions**

3. Trigger a redeploy (Deploys → Trigger deploy → Clear cache and deploy).

4. Note the Netlify site URL — looks like `https://<random-words>-<hash>.netlify.app` or your custom name.

5. In the repo, edit `data/groq-config.json`:
   ```json
   {
     "provider": "groq",
     "proxyUrl": "https://<your-netlify-site>.netlify.app/groq-proxy",
     "groqModel": "llama-3.3-70b-versatile"
   }
   ```

6. `git commit -m "wire groq proxy URL"` → `git push`

7. Visit https://insightanalyticsca.github.io/dashboards/ — the pill should briefly show `Groq · checking…` then flip to green `Groq · live`. The Chatters AI Brief will stream tokens live on page load.

## Verifying the proxy works

```bash
# Replace with your actual Netlify URL
curl -X POST https://<your-netlify-site>.netlify.app/groq-proxy \
  -H "Content-Type: application/json" \
  -H "Origin: https://insightanalyticsca.github.io" \
  -d '{"messages":[{"role":"user","content":"ping"}],"max_tokens":5,"stream":false}'
```

Should return a Groq chat completion JSON.

## Rotating the key later

Just update the `GROQ_API_KEY` env var on Netlify and redeploy. The repo doesn't need to change.

## Files

- `netlify/edge-functions/groq-proxy.js` — the edge function (Deno runtime, streams SSE pass-through)
- `netlify.toml` — minimal Netlify config
- `data/groq-config.json` (in repo root) — points the static site at the proxy URL
