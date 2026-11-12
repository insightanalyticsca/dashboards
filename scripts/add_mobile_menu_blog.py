#!/usr/bin/env python3
"""
Add the #mobile-menu div to the 4 blog pages that are missing it.

3 blog pages already have the mobile-menu div (and the hamburger works
on them):
  - fuzzy-address-matching.html
  - personalized-power-automate.html
  - weather-load-forecast.html

4 blog pages are MISSING the mobile-menu div (hamburger does nothing
because app.js does `if (!mobileMenu) return;`):
  - blog/index.html
  - database-archaeology.html
  - universal-dashboard-builder.html
  - universal-time-series-prediction.html

Fix: inject the same mobile-menu HTML (with absolute URLs to the main
site) right after </header> on the 4 pages that are missing it.

All blog pages already load ../js/app.js which wires up the click
handler. Once the div exists, the hamburger works identically to the
main site.
"""
import os
import glob

BLOG_DIR = "/tmp/my-project/insight-analytics/blog"

# The mobile-menu HTML block — uses absolute URLs to the main site
# (since blog pages don't have #services, #dashboard, etc. sections).
MOBILE_MENU_HTML = """<!-- Mobile menu (same as main site — hamburger toggle wired by app.js) -->
  <div class="mobile-menu" id="mobile-menu" role="menu" aria-label="Mobile navigation">
    <a href="https://insight-analytics.ca/#services" class="nav-link" role="menuitem">Services</a>
    <a href="https://insight-analytics.ca/#dashboard" class="nav-link" role="menuitem">Dashboard</a>
    <a href="https://insight-analytics.ca/#cases" class="nav-link" role="menuitem">Case Studies</a>
    <a href="/blog/" class="nav-link" role="menuitem" title="Live Demos — see it live. Production-ready pipelines.">Live Demos</a>
    <a href="https://insight-analytics.ca/#contact" class="nav-link" role="menuitem">Contact</a>
    <a href="https://insight-analytics.ca/#contact" class="btn btn-primary"><i class="fas fa-calendar-check"></i> Book a Demo</a>
  </div>
"""

count = 0
for path in sorted(glob.glob(os.path.join(BLOG_DIR, "*.html"))):
    fname = os.path.basename(path)
    with open(path, "r", encoding="utf-8") as f:
        src = f.read()

    # Skip if already has the mobile-menu div
    if 'id="mobile-menu"' in src:
        print(f"SKIP (already has mobile-menu): {fname}")
        continue

    # Find </header> and inject the mobile-menu div right after it
    if "</header>" not in src:
        print(f"WARN: no </header> found in {fname}")
        continue

    new_src = src.replace("</header>", "</header>\n\n" + MOBILE_MENU_HTML, 1)
    if new_src != src:
        with open(path, "w", encoding="utf-8") as f:
            f.write(new_src)
        count += 1
        print(f"OK: {fname} — mobile-menu div added after </header>")

print(f"\n{count} pages patched.")
