# DocChat — RAG Assistant Demo

Static HTML + ECharts + Groq-ready RAG demo. 100% free, 100% GitHub Pages.

A modernized clone of the **DocChat** feature from the original .NET MVC app, rebuilt
as a single-page static site that runs on GitHub Pages with optional write-back via the
GitHub Contents API.

## What's inside

- **Document library** — drop files (PDF/DOCX/XLSX), simulated chunking, full CRUD
- **Chat with retrieval** — keyword retrieval over JSON chunks, with optional Groq streaming
- **Analytics dashboard** — 7 ECharts visuals (liquid gauge, rose, animated area, heatmap, radial bar, gauge, dual-axis area)
- **Settings modal** — swap AI provider (Demo / Groq / Ollama) without touching code
- **Animated hero** — node-network canvas, gradient mesh background, glow effects
- **Dark glassmorphic theme** — modernized palette (indigo / cyan / magenta on deep navy)

## Quick start (local)

```bash
cd docchat-demo
python3 -m http.server 8080
# open http://localhost:8080
```

No build step. No dependencies. Just static files.

## Deploy to GitHub Pages

```bash
# 1. Create the repo on GitHub (e.g. insight_analytics/docchat-demo)

# 2. Push the files
git init
git add .
git commit -m "init: docchat demo"
git branch -M main
git remote add origin https://github.com/insight_analytics/docchat-demo.git
git push -u origin main

# 3. Enable Pages in repo Settings → Pages → Source: main / root
# 4. Live at https://insight_analytics.github.io/docchat-demo/
```

## Modes

### Demo mode (default)
- Works out of the box, no API key needed
- Chat returns deterministic answers from local JSON chunks
- CRUD updates only the in-memory cache (refresh resets)
- Great for prospect demos and screenshots

### Groq mode (live AI)
1. Click **Settings** (gear icon, top-right)
2. Select **Groq** as provider
3. Paste your Groq API key (`gsk_...`) — get one at [console.groq.com](https://console.groq.com)
4. Pick a model (default: `llama-3.3-70b-versatile`)
5. Save → chat now streams real responses

Groq is on a generous free tier — no credit card required to start.

### GitHub write-back (persistence)
For full CRUD that persists as git commits:

1. Create a GitHub PAT (classic) with `repo` scope
2. Settings → fill **GitHub Repo** (`owner/repo`), **Branch** (`main`), **PAT**
3. Save → all create/update/delete operations now commit to the repo
4. Every change becomes a git commit with full history / audit trail

Without a PAT, writes update only the in-memory cache.

## File structure

```
docchat-demo/
├── index.html              ← single-page app
├── css/
│   └── styles.css          ← modernized palette + glassmorphism
├── js/
│   ├── api.js              ← JSON CRUD + Groq streaming + Ollama fallback
│   ├── charts.js           ← ECharts theme + 7 dazzling visuals + hero canvas
│   └── app.js              ← UI wiring: library, chat, settings, toasts
├── data/
│   ├── documents.json      ← document metadata (12 seeded)
│   ├── chunks.json        ← text chunks (20 seeded)
│   ├── queries.json       ← chat history (8 seeded)
│   └── analytics.json    ← chart aggregates
└── README.md
```

## Tech

- **ECharts 5** + **echarts-liquidfill** for the gauge
- **Vanilla JS** (no framework, no build step)
- **Font Awesome 6** icons
- **Inter** + **JetBrains Mono** fonts
- **GitHub Contents API** for optional write-back

## Hues

Modernized from the original .NET MVC palette:

| Token | Original | Modernized | Why |
|---|---|---|---|
| `--blue` | `#0808EE` | `#6366F1` | Indigo — softer, more enterprise-premium |
| `--teal` | `#09C698` | `#06B6D4` | Cyan — better contrast on dark |
| `--navy` | `#171777` | `#4338CA` | Indigo-700 — pairs with primary |
| `--green` | `#BBFF05` | `#10B981` | Emerald — readable on white (WCAG AA) |
| (new) `--hot` | — | `#EC4899` | Pink — for dazzle accents |
| `--bg` | `#f0f2f8` | `#0A0E27` | Deep navy — glassmorphic base |

## What's demoable

- Drag-drop upload (simulated chunking)
- CRUD on documents (add / edit tags / remove)
- Chat with streaming (real via Groq, simulated in demo mode)
- Source citations with click-to-preview
- 7 animated charts with staggered entrance
- Liquid-fill gauge for "system health"
- Live node-network canvas in hero
- Toast notifications on every action
- Settings modal with 3 providers + GitHub write-back config

## What's NOT in the demo (intentionally)

- Real vector embeddings (would require a server or hosted embeddings API)
- Real PDF/DOCX text extraction (would require `pdf.js` + `mammoth.js` — easy to add)
- Multi-user auth (single-browser demo)
- Production-grade rate limiting on the GH Contents API

## License

MIT — do whatever. Just don't blame me.
