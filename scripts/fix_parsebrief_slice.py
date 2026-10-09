#!/usr/bin/env python3
"""
Fix: AI brief text truncated at 300 chars — parseBrief() was slicing.

THE REAL ROOT CAUSE (not max_tokens, not CSS):

parseBrief() does `content.slice(0, 300)` on each section's text
before setting it as the cell's textContent. This truncates every
section to 300 characters, cutting off mid-word.

The user's example:
  'Equities posted a broad rally with the Dow Jones leading gains at
   0.83 percent while the S&P 500 and NASDAQ also advanced. Precious
   metals surged significantly, with silver jumping nearly three
   percent and gold climbing over one percent. In contrast,
   agricultural commodities faced selling pressure a'
                                                              ^ cut at ~300 chars

Three places in parseBrief() do this:
  - line 1681 (Strategy 1: section-header extraction)
  - line 1695 (Strategy 2: paragraph-split fallback)
  - line 1704 (Strategy 3: streaming partial)

Fix: remove the .slice(0, 300) from all 3 places. The max-height:120px
+ overflow-y:auto CSS I added in the previous commit will handle long
text via the tiny scrollbar. The AI can now generate full-length
briefings without being truncated.

Files patched:
  /tmp/my-project/insight-analytics/js/blog-markets-dashboard.js
  /tmp/my-project/insight-analytics/service-worker.js  (version bump)
"""
import re

PATH = "/tmp/my-project/insight-analytics/js/blog-markets-dashboard.js"
with open(PATH, "r", encoding="utf-8") as f:
    src = f.read()

# Three places where .slice(0, 300) truncates the AI text. Remove it.
REPLACEMENTS = [
    # Strategy 1 (line ~1681): section-header extraction
    ("if (el) { el.classList.remove('bmd-shimmer'); el.textContent = content.slice(0, 300); }",
     "if (el) { el.classList.remove('bmd-shimmer'); el.textContent = content; }"),
    # Strategy 2 (line ~1695): paragraph-split fallback
    ("if (el) { el.classList.remove('bmd-shimmer'); el.textContent = p.slice(0, 300); }",
     "if (el) { el.classList.remove('bmd-shimmer'); el.textContent = p; }"),
    # Strategy 3 (line ~1704): streaming partial
    ("if (el0) { el0.classList.remove('bmd-shimmer'); el0.textContent = paragraphs[0].slice(0, 300); }",
     "if (el0) { el0.classList.remove('bmd-shimmer'); el0.textContent = paragraphs[0]; }"),
]

count = 0
for old, new in REPLACEMENTS:
    if old in src:
        src = src.replace(old, new)
        count += 1
        print(f"OK: removed .slice(0, 300) from: {old[:60]}...")
    else:
        print(f"WARN: pattern not found: {old[:60]}...")

with open(PATH, "w", encoding="utf-8") as f:
    f.write(src)
print(f"\n{count} truncation points removed.")

# Bump SW + cache-bust
SW = "/tmp/my-project/insight-analytics/service-worker.js"
with open(SW, "r", encoding="utf-8") as f:
    sw = f.read()
sw_new = re.sub(
    r"const VERSION = 'v\d+\.\d+\.\d+-[^']+';",
    "const VERSION = 'v4.89.0-20261009-remove-300-char-slice';",
    sw, count=1,
)
sw_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.89.0", sw_new)
if sw_new != sw:
    with open(SW, "w", encoding="utf-8") as f:
        f.write(sw_new)
    print("OK: SW VERSION → v4.89.0, all ?v= → 4.89.0")

IDX = "/tmp/my-project/insight-analytics/blog/universal-dashboard-builder.html"
with open(IDX, "r", encoding="utf-8") as f:
    idx = f.read()
idx_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.89.0", idx)
if idx_new != idx:
    with open(IDX, "w", encoding="utf-8") as f:
        f.write(idx_new)
    print("OK: blog/universal-dashboard-builder.html ?v= → 4.89.0")
