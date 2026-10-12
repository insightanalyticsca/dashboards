#!/usr/bin/env node
/**
 * seo-optimizer.mjs — Automated SEO optimization via Groq LLM
 *
 * Scans all HTML pages, extracts content, asks Groq for the best
 * search terms / meta descriptions / titles, and writes them back.
 *
 * Also generates/updates:
 *   - sitemap.xml
 *   - robots.txt
 *   - JSON-LD structured data per page
 *   - Open Graph + Twitter Card meta tags
 *   - A tracking file (data/seo-tracking.json) that logs what was
 *     applied and when, so you can see the optimization history.
 */

import { readFileSync, writeFileSync, existsSync, readdirSync, statSync } from 'fs';
import { join, dirname, basename } from 'path';

const SITE_URL = 'https://insightanalyticsca.github.io/dashboards';
const GROQ_KEY = process.env.GROQ_API_KEY || '';
const GROQ_MODEL = 'openai/gpt-oss-120b';
const GROQ_PROXY = 'https://startling-belekoy-b0ec70.netlify.app/groq-proxy';
const REPO_ROOT = process.cwd();

// ─── Collect all HTML pages ──────────────────────────────────────────────
function collectHtmlFiles() {
  const files = [];
  function scan(dir) {
    for (const entry of readdirSync(dir)) {
      if (entry === '.git' || entry === 'node_modules' || entry === 'netlify') continue;
      const full = join(dir, entry);
      const stat = statSync(full);
      if (stat.isDirectory()) {
        scan(full);
      } else if (entry.endsWith('.html')) {
      files.push(full);
      }
    }
  }
  scan(REPO_ROOT);
  return files;
}

// ─── Extract text content from HTML (strip tags) ────────────────────────
function extractContent(html) {
  // Get title
  const titleMatch = html.match(/<title[^>]*>(.*?)<\/title>/is);
  const title = titleMatch ? titleMatch[1].trim() : '';

  // Get existing description
  const descMatch = html.match(/<meta\s+name=["']description["']\s+content=["']([^"']*)["']/is);
  const desc = descMatch ? descMatch[1].trim() : '';

  // Strip scripts, styles, and HTML tags to get raw text
  const text = html
    .replace(/<script[^>]*>.*?<\/script>/gis, ' ')
    .replace(/<style[^>]*>.*?<\/style>/gis, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();

  // First 2000 chars for context
  return { title, desc, text: text.slice(0, 2000) };
}

// ─── Ask Groq for SEO recommendations ───────────────────────────────────
async function getSeoRecommendations(filePath, content) {
  // Use direct Groq key if available, otherwise use the Netlify edge function proxy
  const hasDirectKey = !!GROQ_KEY;
  const hasProxy = !hasDirectKey; // proxy has the key server-side

  if (!hasDirectKey && !hasProxy) {
    console.log('  No Groq access — using heuristic fallback');
    return heuristicSeo(filePath, content);
  }

  const relPath = filePath.replace(REPO_ROOT + '/', '');
  const prompt = `You are an SEO expert. Analyze this webpage and return ONLY a JSON object with optimized SEO metadata.

Page URL path: /${relPath}
Current title: ${content.title}
Current description: ${content.desc || '(none)'}

Page content (first 1500 chars):
${content.text.slice(0, 1500)}

Return JSON with these fields:
{
  "title": "SEO-optimized page title (50-60 chars, includes primary keyword)",
  "description": "Meta description (140-160 chars, compelling, includes primary keyword)",
  "keywords": "comma-separated list of 8-12 relevant search keywords",
  "ogTitle": "Short social share title (40 chars max)",
  "ogDescription": "Social share description (100 chars max)",
  "jsonLd": {
    "@context": "https://schema.org",
    "@type": "WebApplication",
    "name": "...",
    "description": "...",
    "applicationCategory": "BusinessApplication",
    "url": "${SITE_URL}/${relPath}",
    "keywords": "..."
  }
}

Rules:
- Title must include the primary keyword naturally
- Description must be compelling and include the primary keyword
- Keywords must be terms people actually search for
- Do NOT use generic terms like "dashboard demo" — be specific
- Return ONLY the JSON, no markdown fences`;

  try {
    let res;
    if (hasDirectKey) {
      // Direct Groq API call
      res = await fetch('https://api.groq.com/openai/v1/chat/completions', {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${GROQ_KEY}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          model: GROQ_MODEL,
          messages: [{ role: 'user', content: prompt }],
          temperature: 0.3,
          max_tokens: 800
        })
      });
    } else {
      // Use the Netlify edge function proxy (has Groq key server-side)
      res = await fetch(`${GROQ_PROXY}?op=chat`, {
        method: 'POST',
        headers: {
          'Origin': 'https://insightanalyticsca.github.io',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          messages: [{ role: 'user', content: prompt }],
          model: GROQ_MODEL,
          temperature: 0.3,
          max_tokens: 800,
          stream: false
        })
      });
    }

    if (!res.ok) {
      console.log(`  Groq error ${res.status} — using fallback`);
      return heuristicSeo(filePath, content);
    }

    // The proxy streams SSE — collect all chunks
    const contentType = res.headers.get('content-type') || '';
    let json;

    if (contentType.includes('text/event-stream') || contentType.includes('stream')) {
      // Parse SSE stream
      const text = await res.text();
      let fullContent = '';
      for (const line of text.split('\n')) {
        if (!line.startsWith('data:')) continue;
        const data = line.slice(5).trim();
        if (data === '[DONE]') continue;
        try {
          const evt = JSON.parse(data);
          if (evt.choices?.[0]?.delta?.content) {
            fullContent += evt.choices[0].delta.content;
          }
        } catch {}
      }
      json = { choices: [{ message: { content: fullContent } }] };
    } else {
      json = await res.json();
    }

    let text = json.choices?.[0]?.message?.content || '{}';
    text = text.replace(/```json?\n?/g, '').replace(/```/g, '').trim();
    return JSON.parse(text);
  } catch (e) {
    console.log(`  Groq failed: ${e.message} — using fallback`);
    return heuristicSeo(filePath, content);
  }
}

// ─── Heuristic fallback (no API key) ─────────────────────────────────────
function heuristicSeo(filePath, content) {
  const relPath = filePath.replace(REPO_ROOT + '/', '');
  const baseName = basename(filePath, '.html');

  // Derive keywords from content
  const words = content.text.toLowerCase()
    .replace(/[^a-z0-9\s]/g, ' ')
    .split(/\s+/)
    .filter(w => w.length > 4);
  const freq = {};
  words.forEach(w => freq[w] = (freq[w] || 0) + 1);
  const topWords = Object.entries(freq)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 10)
    .map(([w]) => w);

  const title = content.title || baseName.replace(/-/g, ' ');
  const desc = content.desc || content.text.slice(0, 155).trim();

  return {
    title: title.slice(0, 60),
    description: desc.slice(0, 160),
    keywords: topWords.join(', '),
    ogTitle: title.slice(0, 40),
    ogDescription: desc.slice(0, 100),
    jsonLd: {
      '@context': 'https://schema.org',
      '@type': 'WebApplication',
      name: title,
      description: desc,
      applicationCategory: 'BusinessApplication',
      url: `${SITE_URL}/${relPath}`,
      keywords: topWords.join(', ')
    }
  };
}

// ─── Apply SEO metadata to HTML ─────────────────────────────────────────
function applySeoToHtml(html, seo, relPath) {
  let modified = html;
  const url = `${SITE_URL}/${relPath}`;

  // Update <title>
  modified = modified.replace(
    /<title[^>]*>.*?<\/title>/is,
    `<title>${escapeHtml(seo.title)}</title>`
  );

  // Update or add meta description
  if (modified.match(/<meta\s+name=["']description["']/i)) {
    modified = modified.replace(
      /<meta\s+name=["']description["']\s+content=["'][^"']*["']/is,
      `<meta name="description" content="${escapeAttr(seo.description)}">`
    );
  } else {
    modified = modified.replace(
      /<head[^>]*>/i,
      match => `${match}\n  <meta name="description" content="${escapeAttr(seo.description)}">`
    );
  }

  // Update or add meta keywords
  if (modified.match(/<meta\s+name=["']keywords["']/i)) {
    modified = modified.replace(
      /<meta\s+name=["']keywords["']\s+content=["'][^"']*["']/is,
      `<meta name="keywords" content="${escapeAttr(seo.keywords)}">`
    );
  } else {
    modified = modified.replace(
      /<meta\s+name=["']description["']\s+content=["'][^"']*["']/is,
      match => `${match}\n  <meta name="keywords" content="${escapeAttr(seo.keywords)}">`
    );
  }

  // Add Open Graph tags (remove old ones first)
  modified = modified.replace(/<meta\s+property=["']og:[^"']*["']\s+content=["'][^"']*["']\s*\/?>/gis, '');
  const ogTags = `
  <meta property="og:title" content="${escapeAttr(seo.ogTitle || seo.title)}">
  <meta property="og:description" content="${escapeAttr(seo.ogDescription || seo.description)}">
  <meta property="og:type" content="website">
  <meta property="og:url" content="${url}">`;
  modified = modified.replace(
    /<meta\s+name=["']keywords["']\s+content=["'][^"']*["']/is,
    match => `${match}\n${ogTags}`
  );

  // Add Twitter Card tags (remove old ones first)
  modified = modified.replace(/<meta\s+name=["']twitter:[^"']*["']\s+content=["'][^"']*["']\s*\/?>/gis, '');
  const twitterTags = `
  <meta name="twitter:card" content="summary">
  <meta name="twitter:title" content="${escapeAttr(seo.ogTitle || seo.title)}">
  <meta name="twitter:description" content="${escapeAttr(seo.ogDescription || seo.description)}">`;
  modified = modified.replace(
    /<meta\s+property=["']og:url["']\s+content=["'][^"']*["']/is,
    match => `${match}\n${twitterTags}`
  );

  // Add JSON-LD structured data (remove old one first)
  modified = modified.replace(/<script\s+type=["']application\/ld\+json["']\s*>.*?<\/script>/gis, '');
  const jsonLd = `\n  <script type="application/ld+json">${JSON.stringify(seo.jsonLd)}</script>`;
  modified = modified.replace(
    /<\/head>/i,
    `${jsonLd}\n</head>`
  );

  return modified;
}

function escapeHtml(s) { return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
function escapeAttr(s) { return escapeHtml(s).replace(/"/g,'&quot;'); }

// ─── Generate sitemap.xml ───────────────────────────────────────────────
function generateSitemap(htmlFiles) {
  const now = new Date().toISOString().split('T')[0];
  const urls = htmlFiles.map(f => {
    const relPath = f.replace(REPO_ROOT + '/', '');
    const url = `${SITE_URL}/${relPath}`;
    // Priority: index.html = 1.0, visuals.html = 0.9, executive = 0.8, others = 0.6
    let priority = '0.6';
    if (relPath === 'index.html') priority = '1.0';
    else if (relPath === 'visuals.html') priority = '0.9';
    else if (relPath.includes('executive')) priority = '0.8';
    else if (relPath.includes('csr')) priority = '0.7';
    return `  <url>
    <loc>${url}</loc>
    <lastmod>${now}</lastmod>
    <changefreq>weekly</changefreq>
    <priority>${priority}</priority>
  </url>`;
  }).join('\n');

  return `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls}
</urlset>`;
}

// ─── Generate robots.txt ────────────────────────────────────────────────
function generateRobots() {
  return `User-agent: *
Allow: /
Disallow: /netlify/
Disallow: /data/

Sitemap: ${SITE_URL}/sitemap.xml`;
}

// ─── Main ────────────────────────────────────────────────────────────────
async function main() {
  console.log('🔍 SEO Optimizer — scanning HTML files...\n');

  const htmlFiles = collectHtmlFiles();
  console.log(`Found ${htmlFiles.length} HTML pages\n`);

  // Load existing tracking
  const trackingPath = join(REPO_ROOT, 'data', 'seo-tracking.json');
  let tracking = { history: [], lastRun: null };
  if (existsSync(trackingPath)) {
    try { tracking = JSON.parse(readFileSync(trackingPath, 'utf-8')); } catch {}
  }

  const changes = [];

  // Process each HTML file
  for (const filePath of htmlFiles) {
    const relPath = filePath.replace(REPO_ROOT + '/', '');
    console.log(`📄 ${relPath}`);

    const html = readFileSync(filePath, 'utf-8');
    const content = extractContent(html);
    const seo = await getSeoRecommendations(filePath, content);
    const updated = applySeoToHtml(html, seo, relPath);

    if (updated !== html) {
      writeFileSync(filePath, updated);
      console.log(`  ✅ Updated: title="${seo.title}"`);
      changes.push({
        page: relPath,
        title: seo.title,
        description: seo.description,
        keywords: seo.keywords,
        timestamp: new Date().toISOString()
      });
    } else {
      console.log(`  · No changes needed`);
    }

    // Rate limit: wait 500ms between Groq calls
    if (GROQ_KEY) await new Promise(r => setTimeout(r, 500));
  }

  // Generate sitemap.xml
  console.log('\n🗺️  Generating sitemap.xml...');
  writeFileSync(join(REPO_ROOT, 'sitemap.xml'), generateSitemap(htmlFiles));

  // Generate robots.txt
  console.log('🤖 Generating robots.txt...');
  writeFileSync(join(REPO_ROOT, 'robots.txt'), generateRobots());

  // Update tracking file
  tracking.lastRun = new Date().toISOString();
  tracking.history.push({
    runAt: tracking.lastRun,
    pagesOptimized: changes.length,
    totalPages: htmlFiles.length,
    changes: changes.map(c => ({ page: c.page, title: c.title, keywords: c.keywords }))
  });
  // Keep last 30 runs
  tracking.history = tracking.history.slice(-30);
  writeFileSync(trackingPath, JSON.stringify(tracking, null, 2));

  console.log(`\n✅ Done. ${changes.length}/${htmlFiles.length} pages optimized.`);
  console.log(`📊 Tracking log: data/seo-tracking.json (${tracking.history.length} runs)`);
}

main().catch(e => {
  console.error('SEO optimizer failed:', e);
  process.exit(1);
});
