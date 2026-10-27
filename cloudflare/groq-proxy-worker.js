// ════════════════════════════════════════════════════════════════════════════
//  groq-proxy — Cloudflare Worker (Groq upstream + KV cache + visitor tracking)
//
//  Replaces the Netlify Edge Function. Uses Cloudflare Workers KV for
//  persistent storage (no deploy limits, no monthly credit exhaustion).
//
//  Features:
//    - Groq chat completions proxy (streaming + non-streaming)
//    - In-memory + KV cache (24h TTL) to stay under free-tier TPD
//    - Visitor tracking with UUID fingerprinting (same device = same visitor)
//    - Geolocation via Cloudflare's request.cf
//    - op=reset endpoint to zero all counters
//    - op=stats (public), op=visits + op=devices (password-protected)
//    - Bot filtering (curl, Googlebot, headless browsers, scrapers)
//    - IP exclusion (owner's own IPs don't count)
//
//  Deploy: wrangler deploy
//  KV namespace: VISITOR_STATS (create via wrangler kv:namespace create)
//  Secrets: GROQ_API_KEY (wrangler secret put GROQ_API_KEY)
//  ════════════════════════════════════════════════════════════════════════════

const GROQ_URL = 'https://api.groq.com/openai/v1/chat/completions';
const DEFAULT_MODEL = 'qwen/qwen3.8-27b';
const CACHE_TTL_MS = 24 * 60 * 60 * 1000;
const VERIFY_MAX_TOKENS = 5;
const ADMIN_PASSWORD = 'Domino88!!';
const VISIT_RETENTION_MS = 72 * 60 * 60 * 1000;
const MAX_VISITS = 500;
const MAX_VISITS_KV = 2000;

// Browser-like headers for outbound Groq calls. Groq's edge rejects the
// default `Cloudflare-Workers` UA with a generic 403 Forbidden (verified
// Oct 2026 — same key works from Netlify edge and from curl with a real
// browser UA, but fails from a CF Worker with the default UA).
const GROQ_FETCH_HEADERS = {
  'Authorization': '',  // filled in per-call
  'Content-Type': 'application/json',
  'Accept': 'application/json',
  'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  'Accept-Language': 'en-US,en;q=0.9'
};

const ALLOWED_ORIGINS = [
  'https://insight-analytics.ca',
  'https://www.insight-analytics.ca',
  'https://insightanalyticsca.github.io',
  'http://localhost:3000',
  'http://127.0.0.1:3000',
  'http://localhost:5173',
  'http://127.0.0.1:5173'
];

const EXCLUDED_IPS = new Set([
  '70.54.144.197',
  '216.16.231.122',
  '76.68.87.193',
  '47.57.242.119',    // assistant's outbound IP (NAT pool) — added 2026-10-27
  '47.57.232.232',    // assistant's outbound IP (NAT pool) — added 2026-10-27
  '8.212.10.159',      // assistant's other outbound IP (NAT pool) — added 2026-10-27
  '8.8.8.8',
  '9.9.9.9'
]);

function corsHeaders(origin) {
  const allow = ALLOWED_ORIGINS.includes(origin) ? origin : ALLOWED_ORIGINS[0];
  return {
    'Access-Control-Allow-Origin': allow,
    'Access-Control-Allow-Methods': 'POST, OPTIONS, GET',
    'Access-Control-Allow-Headers': 'Content-Type, X-Page, X-Page-Url',
    'Access-Control-Max-Age': '86400',
    'Vary': 'Origin'
  };
}

const memCache = new Map();
const visitLog = [];
let cumulative = { totalVisits: 0, ips: {}, devices: {}, uuids: {}, sites: { main: { totalVisits: 0, ips: {}, devices: {}, uuids: {} }, dashboards: { totalVisits: 0, ips: {}, devices: {}, uuids: {} } } };
let kvLoaded = false;

async function readKV(env, key) {
  if (!env || !env.VISITOR_STATS) return null;
  try { return await env.VISITOR_STATS.get(key); } catch (e) { return null; }
}
async function writeKV(env, key, value) {
  if (!env || !env.VISITOR_STATS) return;
  try { await env.VISITOR_STATS.put(key, value); } catch (e) {}
}

async function readCumulative(env) {
  const data = await readKV(env, 'cumulative-v2');
  if (data) {
    try {
      const parsed = JSON.parse(data);
      if (parsed && typeof parsed.totalVisits === 'number') {
        cumulative = parsed;
        if (!cumulative.uuids) cumulative.uuids = {};
        if (!cumulative.sites) cumulative.sites = { main: { totalVisits: 0, ips: {}, devices: {}, uuids: {} }, dashboards: { totalVisits: 0, ips: {}, devices: {}, uuids: {} } };
        if (!cumulative.sites.main.uuids) cumulative.sites.main.uuids = {};
        if (!cumulative.sites.dashboards.uuids) cumulative.sites.dashboards.uuids = {};

        // ─── One-time reconciliation: subtract prior counts for any IP that
        // is now in EXCLUDED_IPS but was counted before this fix shipped.
        // Without this, an owner's old self-visits stay baked into
        // totalVisits / ips forever. Mirrors the same logic in the Netlify
        // edge function (see lines ~370 of netlify/edge-functions/groq-proxy.js).
        // Idempotent: the IP entry is deleted from cumulative.ips so the
        // next read won't re-subtract.
        if (cumulative.ips && EXCLUDED_IPS.size > 0) {
          var corrected = false;
          for (var excludedIp of EXCLUDED_IPS) {
            if (cumulative.ips[excludedIp]) {
              var stale = cumulative.ips[excludedIp];
              cumulative.totalVisits = Math.max(0, (cumulative.totalVisits || 0) - stale);
              delete cumulative.ips[excludedIp];
              corrected = true;
            }
          }
          // Also sweep per-site buckets (main + dashboards)
          if (cumulative.sites) {
            for (var siteKey of ['main', 'dashboards']) {
              var sb = cumulative.sites[siteKey];
              if (!sb || !sb.ips) continue;
              for (var excludedIp2 of EXCLUDED_IPS) {
                if (sb.ips[excludedIp2]) {
                  var staleSite = sb.ips[excludedIp2];
                  sb.totalVisits = Math.max(0, (sb.totalVisits || 0) - staleSite);
                  delete sb.ips[excludedIp2];
                  corrected = true;
                }
              }
            }
          }
          if (corrected) {
            // Persist immediately so we don't re-subtract on the next cold start.
            await writeKV(env, 'cumulative-v2', JSON.stringify(cumulative));
          }
        }
      }
    } catch (e) {}
  }
  kvLoaded = true;
}

async function writeCumulative(env) {
  if (!kvLoaded) return;
  await writeKV(env, 'cumulative-v2', JSON.stringify(cumulative));
}

async function readVisitLogKV(env) {
  const data = await readKV(env, 'visits-72h');
  if (!data) return [];
  try {
    const visits = JSON.parse(data);
    if (!Array.isArray(visits)) return [];
    const cutoff = Date.now() - VISIT_RETENTION_MS;
    return visits.filter(v => new Date(v.ts).getTime() > cutoff);
  } catch (e) { return []; }
}

async function writeVisitLogKV(env, newVisits) {
  if (newVisits.length === 0) return;
  try {
    const existing = await readVisitLogKV(env);
    const merged = [...existing, ...newVisits];
    const seen = new Set();
    const deduped = merged.filter(v => {
      const key = v.ts + '|' + v.ip;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
    deduped.sort((a, b) => new Date(b.ts).getTime() - new Date(a.ts).getTime());
    const trimmed = deduped.slice(0, MAX_VISITS_KV);
    await writeKV(env, 'visits-72h', JSON.stringify(trimmed));
  } catch (e) {}
}

function isBotUA(ua) {
  if (!ua || ua === 'unknown') return false;
  var lower = ua.toLowerCase();
  var patterns = ['curl', 'wget', 'python', 'scrapy', 'httpx', 'node-fetch', 'axios',
    'got/', 'googlebot', 'bingbot', 'slurp', 'duckduckbot', 'baiduspider',
    'yandexbot', 'ahrefsbot', 'semrushbot', 'facebookexternalhit',
    'twitterbot', 'linkedinbot', 'headlesschrome', 'phantomjs', 'puppeteer',
    'playwright', 'selenium', 'bot', 'crawl', 'spider', 'scrape'];
  for (var i = 0; i < patterns.length; i++) {
    if (lower.indexOf(patterns[i]) >= 0) return true;
  }
  return false;
}

function parseDeviceUA(ua) {
  if (!ua || ua === 'unknown') return 'unknown';
  var browser = 'Other';
  if (ua.indexOf('Edg/') >= 0) browser = 'Edge';
  else if (ua.indexOf('OPR/') >= 0) browser = 'Opera';
  else if (ua.indexOf('Chrome/') >= 0) browser = 'Chrome';
  else if (ua.indexOf('Firefox/') >= 0) browser = 'Firefox';
  else if (ua.indexOf('Safari/') >= 0 && ua.indexOf('Chrome/') < 0) browser = 'Safari';
  var os = 'Other';
  if (ua.indexOf('iPhone') >= 0) os = 'iPhone';
  else if (ua.indexOf('iPad') >= 0) os = 'iPad';
  else if (ua.indexOf('Android') >= 0) os = 'Android';
  else if (ua.indexOf('Mac OS') >= 0 || ua.indexOf('Macintosh') >= 0) os = 'macOS';
  else if (ua.indexOf('Windows') >= 0) os = 'Windows';
  else if (ua.indexOf('Linux') >= 0) os = 'Linux';
  return browser + ' · ' + os;
}

// Redact the last octet (IPv4) or last hextet+suffix (IPv6) of an IP for
// display in the admin panel. Raw IPv6 like 2a09:bac2:17d9:123::1d:10c
// becomes 2a09:bac2:17d9:123::1d:xxx — preserves the network prefix so the
// admin can still see "same /64 network" patterns without exposing the
// full client address. IPv4 keeps the existing pattern (47.57.232.xxx).
function redactIp(ip) {
  if (!ip || ip === 'unknown') return ip;
  // IPv6 (contains ':')
  if (ip.indexOf(':') >= 0) {
    // Strip any zone ID (%eth0 etc.)
    var base = ip.split('%')[0];
    // Match the last segment after the last ':'
    var lastColon = base.lastIndexOf(':');
    if (lastColon > 0) {
      return base.slice(0, lastColon + 1) + 'xxx';
    }
    return base + '...';
  }
  // IPv4 — keep existing pattern: 47.57.232.xxx
  return ip.replace(/(\d+\.\d+\.\d+)\.\d+/, '$1.xxx');
}

// Classify an IP/ASN into a probable type. Uses ASN + AS organization name
// from request.cf (free, no external calls). Returns one of:
//   - 'Verified Bot · <category>'  (Cloudflare-verified bot — Googlebot etc.)
//   - 'Search Engine'              (Google/Microsoft/Bing ASNs, not verified)
//   - 'Cloud · <provider>'        (AWS/Azure/GCP/etc. — probable scraper/proxy)
//   - 'VPN/Proxy · <provider>'     (known commercial VPN/proxy hosts)
//   - 'Mobile · <carrier>'         (T-Mobile, AT&T, Verizon wireless etc.)
//   - 'Residential · <ISP>'        (default for known residential ASNs)
//   - 'Unknown ASN'                (no ASN data — usually a rare network)
//
// Honest caveat: this is approximate. A residential IP can run a bot,
// a cloud IP can be a real developer's outbound, and a verified bot is
// legitimate traffic. The label is a "probable type", not a verdict.
function classifyIpType(cf) {
  if (!cf) return 'Unknown ASN';
  var botCat = cf.verifiedBotCategory || '';
  if (botCat) {
    return 'Verified Bot · ' + botCat;
  }
  var asn = cf.asn || 0;
  var asOrg = (cf.asOrganization || '').toLowerCase();

  // ── Search engine ASNs (not yet verified, but likely a crawler) ──────
  // 15169 Google, 396982 Google LLC (Cloud), 36384 Google, 36392 Google LLC,
  // 8068 Microsoft (Bing), 26914 Microsoft Azure (Bing crawl)
  if (asn === 15169 || asn === 396982 || asn === 36384 || asn === 36392) {
    return 'Search Engine · Google';
  }
  if (asn === 8068 || asn === 26914 || asOrg.indexOf('microsoft') >= 0 && asOrg.indexOf('bing') >= 0) {
    return 'Search Engine · Bing';
  }

  // ── Cloud providers (probable scraper/proxy/dev outbound) ──────────
  // 14618 + 16509 Amazon AWS, 8075 Microsoft Azure, 396982 Google Cloud,
  // 16509 Google Cloud, 15169 Google Cloud, 19527 Oracle Cloud, 14061 DigitalOcean,
  // 63949 Linode, 46562 Performive (data center), 24940 Hetzner, 60068 DataCamp,
  // 51167 Contabo, 212238 Akamai, 20940 Akamai, 16625 Akamai,
  // 45102 Alibaba Cloud, 55960 Tencent Cloud, 58461 China Telecom Cloud,
  // 37963 Alibaba, 132203 Tencent Build, 136907 Huawei Cloud
  var cloudMap = {
    14618: 'AWS', 16509: 'AWS', 15169: 'Google Cloud', 396982: 'Google Cloud',
    8075: 'Azure', 19527: 'Oracle Cloud', 14061: 'DigitalOcean',
    63949: 'Linode', 46562: 'Performive', 24940: 'Hetzner', 60068: 'DataCamp',
    51167: 'Contabo', 212238: 'Akamai', 20940: 'Akamai', 16625: 'Akamai',
    204687: 'Rossman (DE)', 396982: 'Google Cloud', 213230: 'Hetzner',
    46606: 'ServerMania', 36352: 'ColoCrossing', 401192: 'GPU Marketplace',
    45102: 'Alibaba Cloud', 55960: 'Tencent Cloud', 58461: 'China Telecom Cloud',
    37963: 'Alibaba', 132203: 'Tencent', 136907: 'Huawei Cloud',
    45090: 'Shenzhen Tencent', 4134: 'China Telecom', 4837: 'China Unicom',
    9808: 'China Mobile Cloud', 4812: 'China Telecom Backbone'
  };
  if (cloudMap[asn]) {
    return 'Cloud · ' + cloudMap[asn];
  }

  // ── Known VPN/proxy hosts ────────────────────────────────────────────
  // 9009 M247, 35913 DataWeb (DediPath), 199524 GCore, 200651 FlokiNET,
  // 35540 GTHost, 39351 FlokiNET, 62240 Clouvider, 54825 PacketHub,
  // 63018 Lightnode, 212725 OrcaWave, 39351 FlokiNET, 41564 OrionNet,
  // 198093 Sagitta, 209242 Cloudflare (WARP — VPN exit)
  var vpnMap = {
    9009: 'M247', 35913: 'DediPath', 199524: 'GCore', 200651: 'FlokiNET',
    35540: 'GTHost', 39351: 'FlokiNET', 62240: 'Clouvider', 54825: 'PacketHub',
    63018: 'Lightnode', 212725: 'OrcaWave', 41564: 'OrionNet', 198093: 'Sagitta',
    13335: 'Cloudflare WARP', 209242: 'Cloudflare WARP', 24940: 'Hetzner VPN',
    60068: 'DataCamp VPN', 51167: 'Contabo VPN'
  };
  if (vpnMap[asn]) {
    return 'VPN/Proxy · ' + vpnMap[asn];
  }

  // ── Mobile carriers (probable human on phone) ────────────────────────
  // 22047 China Mobile, 23493 Vodafone (multi-country), 6315 Shaw Mobile (CA),
  // 3300 Bell Canada, 577 Bell Canada, 3651 Rogers Wireless (CA), 22995 Rogers (CA),
  // 4323 T-Mobile (US), 20057 AT&T Wireless (US), 6167 Cellcom, 10507 Verizon Wireless (US),
  // 22394 Cellular One, 701 Verizon Business, 7018 AT&T, 7029 Windstream
  var mobileMap = {
    22047: 'China Mobile', 23493: 'Vodafone', 6315: 'Shaw Mobile',
    3300: 'Bell Canada', 577: 'Bell Canada', 3651: 'Rogers Wireless',
    22995: 'Rogers', 4323: 'T-Mobile US', 20057: 'AT&T Wireless',
    6167: 'Cellcom', 10507: 'Verizon Wireless', 22394: 'Cellular One',
    7029: 'Windstream', 3320: 'Deutsche Telekom', 3215: 'Orange (FR)',
    20712: 'Andrews (Telecom)', 26617: 'Mobily'
  };
  if (mobileMap[asn]) {
    return 'Mobile · ' + mobileMap[asn];
  }

  // ── Residential ISPs (probable human) ────────────────────────────────
  // Fallback heuristic: if the AS org name contains a known residential ISP
  // keyword, classify as residential.
  var residentialKeywords = ['comcast', 'rogers', 'bell canada', 'verizon',
    'at&t', 'telus', 'shaw', 'cox comm', 'charter', 'spectrum', 'centurylink',
    'windstream', 'frontier', 'mediatelecom', 'china telecom', 'china unicom',
    'france telecom', 'british telecom', 'deutsche telekom', 'telefonica',
    'telmex', 'kddi', 'ntt', 'softbank', 'sk broadband', 'singtel', 'telstra',
    'virgin media', 'sky broadband', 'plusnet', 'talktalk'];
  for (var i = 0; i < residentialKeywords.length; i++) {
    if (asOrg.indexOf(residentialKeywords[i]) >= 0) {
      return 'Residential · ' + (cf.asOrganization || residentialKeywords[i]);
    }
  }

  // Fallback: ASN present but no match — show the AS org name as-is
  if (asOrg) {
    return 'Other · ' + cf.asOrganization;
  }
  return 'Unknown ASN';
}

// Classify a visit as likely-human vs likely-bot using static signals only.
// No external API calls, no rate limits, zero added latency.
// Returns: { label: 'Likely Human'|'Likely Bot'|'Uncertain'|'Verified Bot'|'You',
//            score: 0-100 (100 = definitely a bot, 0 = definitely human),
//            reason: short phrase explaining the score }
//
// Honest caveats (also surfaced in the Reason field when relevant):
// - This is a heuristic. Real devs sometimes browse from AWS at 5 AM.
// - Compromised residential IPs (botnets) will score as "Likely Human".
// - Cloud IPs running real Chrome UAs are ambiguous; we mark as Uncertain.
function classifyRealBot(cf, headers, page, ipType) {
  // Verified bot — Cloudflare-confirmed via reverse DNS. Authoritative.
  if (cf && cf.verifiedBotCategory) {
    return {
      label: 'Verified Bot',
      score: 100,
      reason: 'Cloudflare-verified bot (' + cf.verifiedBotCategory + ')'
    };
  }

  var score = 50;  // start at uncertain
  var reasons = [];

  var ua = headers.get('user-agent') || '';
  var referrer = headers.get('referer') || 'direct';
  var asn = cf && cf.asn || 0;
  var ipTypeL = (ipType || '').toLowerCase();

  // ── Signal 1: User-Agent presence ────────────────────────────────────
  if (!ua || ua === 'unknown') {
    score += 40;
    reasons.push('no User-Agent');
  }

  // ── Signal 2: Referer presence (real browsers usually send one) ────
  if (referrer === 'direct' || !referrer) {
    score += 10;
    reasons.push('no referrer');
  } else {
    score -= 15;
    reasons.push('has referrer');
  }

  // ── Signal 3: ASN type — cloud/datacenter = strong bot signal ──────
  if (ipTypeL.indexOf('cloud') === 0) {
    score += 30;
    reasons.push('cloud IP');
  } else if (ipTypeL.indexOf('vpn/proxy') === 0) {
    score += 25;
    reasons.push('VPN/proxy IP');
  } else if (ipTypeL.indexOf('search engine') === 0) {
    score += 35;
    reasons.push('search engine ASN');
  } else if (ipTypeL.indexOf('mobile') === 0) {
    score -= 25;
    reasons.push('mobile carrier');
  } else if (ipTypeL.indexOf('residential') === 0) {
    score -= 20;
    reasons.push('residential ISP');
  } else if (ipTypeL.indexOf('other') === 0) {
    // Unknown corporate ASN — neutral, slightly suspect
    score += 5;
    reasons.push('corporate ASN');
  }

  // ── Signal 4: Page path — diagnostic URLs are a dead giveaway ─────
  if (page && (page.indexOf('?test') >= 0 || page.indexOf('?probe') >= 0 ||
               page.indexOf('?verify') >= 0 || page.indexOf('?tailtest') >= 0)) {
    score += 50;
    reasons.push('diagnostic URL');
  }

  // ── Signal 5: Real-browser UA strings (Chrome/Safari/Firefox/Edge) ─
  // Real browsers send detailed UAs. Bots sometimes spoof, but if a bot
  // bothers to spoof, it usually sends a real-looking UA — so this signal
  // is weak on its own, but combined with cloud IP it lowers confidence.
  if (ua && ua !== 'unknown') {
    if (ua.indexOf('Chrome/') >= 0 || ua.indexOf('Safari/') >= 0 ||
        ua.indexOf('Firefox/') >= 0 || ua.indexOf('Edg/') >= 0) {
      score -= 10;
      reasons.push('real browser UA');
    }
    // Empty/minimal UA strings are bot signatures
    if (ua.length < 30) {
      score += 20;
      reasons.push('minimal UA');
    }
  }

  // Clamp 0-100
  score = Math.max(0, Math.min(100, score));

  // ── Determine label from score ─────────────────────────────────────
  var label;
  if (score >= 80) {
    label = 'Likely Bot';
  } else if (score >= 60) {
    label = 'Uncertain (lean bot)';
  } else if (score >= 40) {
    label = 'Uncertain';
  } else if (score >= 20) {
    label = 'Uncertain (lean human)';
  } else {
    label = 'Likely Human';
  }

  // ── Override: Cloudflare WARP + missing UA = test traffic ──────────
  // The combination of WARP + no UA + no referrer + direct hit almost
  // always indicates a diagnostic probe (curl, iOS Shortcut, privacy
  // browser). Bump score up.
  if (ipTypeL.indexOf('cloudflare warp') >= 0 && (!ua || ua === 'unknown')) {
    score = Math.max(score, 75);
    label = 'Likely Bot';
    if (reasons.indexOf('WARP + no UA (test?)') < 0) {
      reasons.push('WARP + no UA (test?)');
    }
  }

  // Cap reasons to 3 most informative
  reasons = reasons.slice(0, 3);

  return {
    label: label,
    score: score,
    reason: reasons.join(' · ')
  };
}

async function logVisit(request, env, ctx) {
  const headers = request.headers;
  // Geo data: in modern Workers, request.cf is populated (not ctx.cf, which
  // was the older API and now returns null). Fall back through:
  //   1. request.cf (preferred — full city/country/lat/long/etc.)
  //   2. ctx.cf (older compat — may be null in modern runtime)
  //   3. cf-ipcountry header (always available, country only)
  var cf = request.cf || ctx.cf || {};
  var country = cf.country || cf.countryName || headers.get('cf-ipcountry') || 'unknown';
  var city = cf.city || 'unknown';
  // IP type classification — uses ASN + AS org + verifiedBotCategory from
  // request.cf. Zero external calls, zero latency. See classifyIpType()
  // for the full bucketing logic + honest caveats.
  var ipType = classifyIpType(cf);
  var asn = cf.asn || 0;
  var asOrganization = cf.asOrganization || '';
  var referrer = headers.get('referer') || 'direct';
  var page = headers.get('x-page') || '';
  if (!page) { try { page = new URL(request.url).searchParams.get('page') || ''; } catch(_) {} }
  if (!page) {
    page = 'direct';
    if (referrer && referrer !== 'direct') {
      try { var refUrl = new URL(referrer); page = refUrl.pathname.replace(/^\/dashboards\/?/, '') || 'index.html'; if (refUrl.hash) page += refUrl.hash; } catch(e) { page = referrer.slice(0, 80); }
    }
  }
  // Real/bot classification — uses static signals (ASN, UA, referrer, page).
  // Must come AFTER `page` is resolved (it uses page for diagnostic-URL detection).
  var realBot = classifyRealBot(cf, headers, page, ipType);

  var ip = headers.get('cf-connecting-ip') || headers.get('x-forwarded-for')?.split(',')[0]?.trim() || headers.get('x-real-ip') || 'unknown';
  ip = ip.replace(/^\[|\]$/g, '').trim();

  if (EXCLUDED_IPS.has(ip)) return null;

  var ua = headers.get('user-agent') || 'unknown';
  if (isBotUA(ua)) return null;

  var device = parseDeviceUA(ua);
  var site = 'main';
  try { var _sp = new URL(request.url).searchParams; if (_sp.get('site')) site = _sp.get('site'); } catch(_) {}
  if (site !== 'main' && site !== 'dashboards') site = 'main';

  var uid = '';
  try { uid = new URL(request.url).searchParams.get('uid') || ''; } catch(_) {}
  if (!uid || uid.length < 8) uid = '';

  const visit = { ts: new Date().toISOString(), ip, country, city, ipType, asn, asOrganization, realLabel: realBot.label, realScore: realBot.score, realReason: realBot.reason, page, method: request.method, ua, site, uid };
  visitLog.push(visit);
  if (visitLog.length > MAX_VISITS) visitLog.shift();

  cumulative.totalVisits++;
  cumulative.ips[ip] = (cumulative.ips[ip] || 0) + 1;
  cumulative.devices[device] = (cumulative.devices[device] || 0) + 1;
  if (uid) cumulative.uuids[uid] = (cumulative.uuids[uid] || 0) + 1;
  if (!cumulative.sites) cumulative.sites = { main: { totalVisits: 0, ips: {}, devices: {}, uuids: {} }, dashboards: { totalVisits: 0, ips: {}, devices: {}, uuids: {} } };
  if (!cumulative.sites[site]) cumulative.sites[site] = { totalVisits: 0, ips: {}, devices: {}, uuids: {} };
  cumulative.sites[site].totalVisits++;
  cumulative.sites[site].ips[ip] = (cumulative.sites[site].ips[ip] || 0) + 1;
  cumulative.sites[site].devices[device] = (cumulative.sites[site].devices[device] || 0) + 1;
  if (uid) cumulative.sites[site].uuids[uid] = (cumulative.sites[site].uuids[uid] || 0) + 1;

  await writeCumulative(env);
  // Visit-log KV write is done via ctx.waitUntil at the fetch handler level
  // (so the worker stays alive long enough for the write to complete even
  // after the response is returned to the client).
  return visit;
}

async function sha256(text) {
  const data = new TextEncoder().encode(text);
  const hash = await crypto.subtle.digest('SHA-256', data);
  return [...new Uint8Array(hash)].map(b => b.toString(16).padStart(2, '0')).join('');
}

export default {
  async fetch(request, env, ctx) {
    const origin = request.headers.get('origin') || '';
    const url = new URL(request.url);
    const op = url.searchParams.get('op');
    const isPost = request.method === 'POST';

    // Log visits (only for beacon/page-loads, not polling/admin)
    if (!isPost && op !== 'visits' && op !== 'stats' && op !== 'devices' && op !== 'models' && op !== 'reset') {
      if (!kvLoaded) await readCumulative(env);
      await logVisit(request, env, ctx);
      // Use ctx.waitUntil to keep the runtime alive for the visit-log write
      // (which is fire-and-forget inside logVisit). Without this, Cloudflare
      // terminates the worker when the response is returned, killing the
      // pending KV write before it completes.
      // (logVisit already awaits writeCumulative, but the visit-log write
      // inside it is intentionally non-awaited to keep response latency low.)
      try { ctx.waitUntil(writeVisitLogKV(env, visitLog.slice(-10))); } catch(e) {}
    }

    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders(origin) });
    }

    if (request.method === 'GET') {
      // op=beacon
      if (op === 'beacon') {
        return new Response(new Uint8Array([0x47,0x49,0x46,0x38,0x39,0x61,0x01,0x00,0x01,0x00,0x80,0x00,0x00,0xff,0xff,0xff,0x21,0xf9,0x04,0x01,0x00,0x00,0x00,0x00,0x2c,0x00,0x00,0x00,0x00,0x01,0x00,0x01,0x00,0x00,0x02,0x02,0x44,0x01,0x00,0x3b]),
          { status: 200, headers: { 'Content-Type': 'image/gif', ...corsHeaders(origin) } });
      }

      // op=reset
      if (op === 'reset') {
        const pwd = url.searchParams.get('password') || '';
        if (pwd !== ADMIN_PASSWORD) return new Response(JSON.stringify({ error: 'Unauthorized' }), { status: 403, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
        cumulative = { totalVisits: 0, ips: {}, devices: {}, uuids: {}, sites: { main: { totalVisits: 0, ips: {}, devices: {}, uuids: {} }, dashboards: { totalVisits: 0, ips: {}, devices: {}, uuids: {} } } };
        visitLog.length = 0;
        kvLoaded = true;
        await writeCumulative(env);
        await writeKV(env, 'visits-72h', JSON.stringify([]));
        return new Response(JSON.stringify({ success: true, message: 'All visitor counters reset to 0', resetAt: new Date().toISOString() }), { status: 200, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
      }

      // op=stats
      if (op === 'stats') {
        if (!kvLoaded) await readCumulative(env);
        var sites = cumulative.sites || {};
        return new Response(JSON.stringify({
          devices: Object.keys(cumulative.devices || {}).length,
          ips: Object.keys(cumulative.ips || {}).length,
          visits: cumulative.totalVisits || 0,
          sites: {
            main: { devices: Object.keys((sites.main||{}).devices||{}).length, ips: Object.keys((sites.main||{}).ips||{}).length, visits: (sites.main||{}).totalVisits || 0 },
            dashboards: { devices: Object.keys((sites.dashboards||{}).devices||{}).length, ips: Object.keys((sites.dashboards||{}).ips||{}).length, visits: (sites.dashboards||{}).totalVisits || 0 }
          }
        }), { status: 200, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
      }

      // op=devices
      if (op === 'devices') {
        const pwd = url.searchParams.get('password') || '';
        if (pwd !== ADMIN_PASSWORD) return new Response(JSON.stringify({ error: 'Unauthorized' }), { status: 403, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
        if (!kvLoaded) await readCumulative(env);
        const siteFilter = url.searchParams.get('site');
        var bucket = cumulative; var bucketLabel = 'combined';
        if (siteFilter === 'main' || siteFilter === 'dashboards') { var s = cumulative.sites || {}; bucket = s[siteFilter] || { totalVisits: 0, ips: {}, devices: {} }; bucketLabel = siteFilter; }
        const deviceList = Object.entries(bucket.devices || {}).filter(([d]) => d.indexOf('Bot') !== 0).map(([d,c]) => ({ device: d, visits: c })).sort((a,b) => b.visits - a.visits);
        var ipGeo = {};
        try { const bv = await readVisitLogKV(env); [...bv, ...visitLog].forEach(v => { if (v.ip && !ipGeo[v.ip]) ipGeo[v.ip] = { country: v.country||'unknown', city: v.city||'unknown', ipType: v.ipType||'Unknown ASN', asn: v.asn||0, asOrganization: v.asOrganization||'', lastSeen: v.ts, device: v.ua?parseDeviceUA(v.ua):'unknown', uid: v.uid||'' }; }); } catch(e) {}
        const ipList = Object.entries(bucket.ips||{}).filter(([ip]) => !EXCLUDED_IPS.has(ip)).map(([ip,c]) => { var g = ipGeo[ip]||{}; return { ip: redactIp(ip), visits: c, country: g.country||'unknown', city: g.city||'unknown', ipType: g.ipType||'Unknown ASN', asn: g.asn||0, asOrganization: g.asOrganization||'', lastSeen: g.lastSeen||'', device: g.device||'unknown', uid: g.uid||'' }; }).sort((a,b) => b.visits - a.visits);
        return new Response(JSON.stringify({ site: bucketLabel, totalVisits: bucket.totalVisits||0, distinctDevices: deviceList.length, distinctIPs: ipList.length, distinctVisitors: Object.keys(bucket.uuids||{}).length, devices: deviceList, ips: ipList, capturedAt: new Date().toISOString() }), { status: 200, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
      }

      // op=visits
      if (op === 'visits') {
        const pwd = url.searchParams.get('password') || '';
        if (pwd !== ADMIN_PASSWORD) return new Response(JSON.stringify({ error: 'Unauthorized' }), { status: 403, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
        if (!kvLoaded) await readCumulative(env);
        const bv = await readVisitLogKV(env);
        const merged = [...bv, ...visitLog];
        const seen = new Set();
        const deduped = merged.filter(v => { const k = v.ts+'|'+v.ip; if (seen.has(k)) return false; seen.add(k); return true; });
        const cutoff = Date.now() - VISIT_RETENTION_MS;
        let recent = deduped.filter(v => new Date(v.ts).getTime() > cutoff);
        recent = EXCLUDED_IPS.size > 0 ? recent.filter(v => !EXCLUDED_IPS.has(v.ip)) : recent;
        const sf = url.searchParams.get('site');
        if (sf === 'main' || sf === 'dashboards') recent = recent.filter(v => (v.site||'main') === sf);
        recent = recent.filter(v => !isBotUA(v.ua||''));
        recent.sort((a,b) => new Date(b.ts).getTime() - new Date(a.ts).getTime());
        // Redact IPs (IPv4 last octet → xxx, IPv6 last hextet → xxx) before
        // sending to the admin panel. Raw IPs are never exposed client-side.
        recent = recent.map(v => Object.assign({}, v, { ip: redactIp(v.ip) }));
        var cs = (sf && cumulative.sites && cumulative.sites[sf]) ? cumulative.sites[sf] : cumulative;
        return new Response(JSON.stringify({ count: recent.length, visits: recent, site: sf||'combined', retentionHours: 72, source: 'cloudflare-kv', cumulative: { totalVisits: cs.totalVisits||0, distinctIPs: Object.keys(cs.ips||{}).length, distinctDevices: Object.keys(cs.devices||{}).length, ipCounts: Object.fromEntries(Object.entries(cs.ips||{}).map(([ip,c]) => [redactIp(ip), c])), deviceCounts: cs.devices||{} }, edge: request.cf?.country || ctx.cf?.country || 'unknown', capturedAt: new Date().toISOString() }), { status: 200, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
      }

      // op=models
      if (op === 'models') {
        const GROQ_KEY = env.GROQ_API_KEY;
        if (!GROQ_KEY) return new Response(JSON.stringify({ error: 'GROQ_API_KEY not set' }), { status: 500, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
        try { const r = await fetch('https://api.groq.com/openai/v1/models', { headers: { ...GROQ_FETCH_HEADERS, 'Authorization': 'Bearer ' + GROQ_KEY } }); const d = await r.json(); return new Response(JSON.stringify(d), { status: r.status, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }); } catch (e) { return new Response(JSON.stringify({ error: e.message }), { status: 500, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }); }
      }

      return new Response(JSON.stringify({ ok: true, service: 'groq-proxy (Cloudflare Worker)', cache: 'memory+kv' }), { status: 200, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
    }

    // POST: Groq proxy
    if (request.method === 'POST') {
      const GROQ_KEY = env.GROQ_API_KEY;
      if (!GROQ_KEY) return new Response(JSON.stringify({ error: 'GROQ_API_KEY not set' }), { status: 500, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } });
      const body = await request.json();
      const model = body.model || DEFAULT_MODEL;
      const isStreaming = body.stream === true;
      const maxTokens = body.max_tokens || 1000;
      const isVerify = maxTokens <= VERIFY_MAX_TOKENS;

      if (!isVerify) {
        const ck = 'chat:' + await sha256(JSON.stringify({ model, messages: body.messages, temperature: body.temperature ?? 0.3, max_tokens: maxTokens, stream: isStreaming }));
        const cached = memCache.get(ck);
        if (cached && (Date.now() - cached.ts) < CACHE_TTL_MS) {
          if (isStreaming) {
            const enc = new TextEncoder();
            const stream = new ReadableStream({ start(c) { cached.text.split('').forEach(ch => { c.enqueue(enc.encode('data: ' + JSON.stringify({ id:'cache', object:'chat.completion.chunk', created:Math.floor(Date.now()/1000), model, choices:[{index:0,delta:{content:ch},finish_reason:null}] }) + '\n\n')); }); c.enqueue(enc.encode('data: ' + JSON.stringify({ id:'cache', object:'chat.completion.chunk', created:Math.floor(Date.now()/1000), model, choices:[{index:0,delta:{},finish_reason:'stop'}] }) + '\n\n')); c.enqueue(enc.encode('data: [DONE]\n\n')); c.close(); } });
            return new Response(stream, { status: 200, headers: { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', 'X-Cache': 'HIT', ...corsHeaders(origin) } });
          } else {
            return new Response(JSON.stringify({ id:'cache', object:'chat.completion', created:Math.floor(Date.now()/1000), model, choices:[{index:0,message:{role:'assistant',content:cached.text},finish_reason:'stop'}], usage:{prompt_tokens:0,completion_tokens:0,total_tokens:0} }), { status: 200, headers: { 'Content-Type': 'application/json', 'X-Cache': 'HIT', ...corsHeaders(origin) } });
          }
        }
      }

      const groqRes = await fetch(GROQ_URL, { method: 'POST', headers: { ...GROQ_FETCH_HEADERS, 'Authorization': 'Bearer ' + GROQ_KEY }, body: JSON.stringify({ ...body, model, stream: isStreaming }) });
      if (!groqRes.ok) { const err = await groqRes.text(); return new Response(JSON.stringify({ error: 'Groq API error', status: groqRes.status, groqResponse: err.slice(0, 500), keyPrefix: GROQ_KEY.slice(0, 10) + '...' }), { status: groqRes.status, headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) } }); }

      if (isStreaming) {
        const enc = new TextEncoder(); let fullText = '';
        const ts = new ReadableStream({ async start(c) { const r = groqRes.body.getReader(); const d = new TextDecoder(); let buf = '';
          while (true) { const { done, value } = await r.read(); if (done) break; buf += d.decode(value, { stream: true }); const lines = buf.split('\n'); buf = lines.pop() || '';
            for (const line of lines) { const t = line.trim(); if (!t.startsWith('data:')) continue; const data = t.slice(5).trim(); if (data === '[DONE]') continue;
              try { const e = JSON.parse(data); const delta = e.choices?.[0]?.delta?.content || ''; if (delta) fullText += delta; } catch(_){}
              c.enqueue(enc.encode(line + '\n')); } }
          c.enqueue(enc.encode('data: [DONE]\n\n')); c.close();
          if (!isVerify && fullText.length > 20) { const ck = 'chat:' + await sha256(JSON.stringify({ model, messages: body.messages, temperature: body.temperature ?? 0.3, max_tokens: maxTokens, stream: isStreaming })); memCache.set(ck, { ts: Date.now(), text: fullText }); if (memCache.size > 100) { const o = [...memCache.entries()].sort((a,b) => a[1].ts - b[1].ts)[0]; if (o) memCache.delete(o[0]); } }
        } });
        return new Response(ts, { status: 200, headers: { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', 'X-Cache': 'MISS', ...corsHeaders(origin) } });
      }

      const groqJson = await groqRes.json();
      if (!isVerify) { const text = groqJson.choices?.[0]?.message?.content || ''; if (text.length > 20) { const ck = 'chat:' + await sha256(JSON.stringify({ model, messages: body.messages, temperature: body.temperature ?? 0.3, max_tokens: maxTokens, stream: false })); memCache.set(ck, { ts: Date.now(), text }); if (memCache.size > 100) { const o = [...memCache.entries()].sort((a,b) => a[1].ts - b[1].ts)[0]; if (o) memCache.delete(o[0]); } } }
      return new Response(JSON.stringify(groqJson), { status: groqRes.status, headers: { 'Content-Type': 'application/json', 'X-Cache': 'MISS', ...corsHeaders(origin) } });
    }

    return new Response('Method not allowed', { status: 405, headers: corsHeaders(origin) });
  }
};
