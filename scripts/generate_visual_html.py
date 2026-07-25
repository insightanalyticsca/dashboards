#!/usr/bin/env python3
"""generate_visual_html.py — emit /visuals/<name>.html for each visual."""
import os

ROOT = "/home/z/my-project/download/docchat-demo"
PAGES = os.path.join(ROOT, "visuals")
os.makedirs(PAGES, exist_ok=True)

# name -> (title, sector, variant)
VISUALS = [
    # Executive (5) — version 213
    ("executive-ar-portfolio",         "AR Portfolio",                 "executive", "cyan"),
    ("executive-customer-payments",    "Customer Payments",             "executive", "blue"),
    ("executive-disconnects-bankruptcies", "Disconnects & Bankruptcies","executive", "teal"),
    ("executive-ebill-performance",    "E-Bill Performance",            "executive", "indigo"),
    ("executive-final-bill-recovery",  "Final Bill Recovery",           "executive", "blue"),
    # CSR (5)
    ("aging-bankruptcies",             "Aging Bankruptcies",            "csr",       "default"),
    ("aging-disconnects-reconnects",   "Disconnects vs Reconnects",     "csr",       "default"),
    ("ar-buckets-stacked",             "AR Buckets Stacked",            "csr",       "default"),
    ("aging-electric-commercial-rolling13", "Commercial Electric Arrears","csr",     "default"),
    ("aging-forecast-monitor",         "Aging Forecast Monitor",        "csr",       "default"),
    # ITS (5)
    ("its-uptime",                     "ITS Uptime Report",            "its",       "default"),
    ("its-ticket-volume-open",         "Ticket Volume Open",           "its",       "default"),
    ("its-kb4-phish-mom",              "KB4 Phishing MoM",             "its",       "default"),
    ("its-open-priority",              "Open by Priority",             "its",       "default"),
    ("its-ocsf-report",                "OCSF Security Events",         "its",       "default"),
]

TEMPLATE = """<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>{title}</title>
  <link rel="stylesheet" href="../css/executive-dashboard-suite.css?v=20260726">
  <link rel="stylesheet" href="../css/visuals-gallery.css?v=20260726">
  <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.2/css/all.min.css">
  <script src="https://cdn.jsdelivr.net/npm/echarts@5/dist/echarts.min.js"></script>
</head>
<body data-suite="{suite}" data-variant="{variant}" data-sector="{sector}">
  <div class="vis-frame">
    <header class="vis-topbar">
      <a class="vis-back" href="../visuals.html">
        <i class="fa-solid fa-arrow-left"></i> Gallery
      </a>
      <div class="vis-breadcrumb">
        <span class="vis-sector vis-sector--{sector}">{sector_upper}</span>
        <span class="vis-sep">/</span>
        <span class="vis-name">{title}</span>
      </div>
      <a class="vis-gh" href="https://github.com/insightanalyticsca/dashboards" target="_blank" rel="noopener">
        <i class="fa-brands fa-github"></i>
      </a>
    </header>

    <div class="vis-host">
      <div class="vis-loading" id="loading">
        <span class="spin"></span>
        <span>Loading dashboard data…</span>
      </div>
      <div id="app" style="display:none"></div>
    </div>

    <footer class="vis-foot">
      <span>Cloned from .NET MVC <code>corporate_dashboards</code> · static demo</span>
      <span>v213 · {name}</span>
    </footer>
  </div>

  <script src="../js/visuals-renderer.js?v=20260726"></script>
  <script>
    (async function() {{
      try {{
        const res = await fetch('../data/visuals/{name}.json?v=20260726');
        if (!res.ok) throw new Error('HTTP ' + res.status);
        const payload = await res.json();
        document.getElementById('loading').style.display = 'none';
        document.getElementById('app').style.display = 'block';
        window.renderVisual(payload);
      }} catch (err) {{
        document.getElementById('loading').innerHTML = '<span class="err">⚠ ' + err.message + '</span>';
      }}
    }})();
  </script>
</body>
</html>
"""

for name, title, sector, variant in VISUALS:
    html = TEMPLATE.format(
        title=title, suite=sector, variant=variant, sector=sector,
        sector_upper=sector.upper(), name=name
    )
    path = os.path.join(PAGES, name + ".html")
    with open(path, "w") as f:
        f.write(html)
    print(f"  ✓ {name}.html  ({len(html)} bytes)")

print(f"\nGenerated {len(VISUALS)} visual HTML pages in {PAGES}")
