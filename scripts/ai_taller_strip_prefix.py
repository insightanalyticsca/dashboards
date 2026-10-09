#!/usr/bin/env python3
"""
Two fixes:
1. Increase AI card height by 20%: max-height 360px → 432px
2. Strip trailing section-number prefixes (like "3:") that leak from
   the NEXT section's header into the current section's content.

Bug: Groq generates sections with numbered headers like:
  1: WHAT HAPPENED
  ...
  2: WHY IT MATTERS
  ...
  3: WHAT TO EXPECT
  ...

parseBrief() finds "what to expect" at position X, but the actual
header starts earlier with "3: ". When extracting section 2's
content, contentEnd = X (position of "what to expect"), so the
content includes everything up to "what to expect" — including
the "3:" prefix that comes before it. Result: section 2's text
ends with "...resilient economic backdrop. 3:"

Fix: strip trailing patterns like "3:", "3.", "3 -", "3 )" from
the end of each section's content. Also strip leading number
prefixes (like "1:") from the start.
"""
import re

PATH = "/tmp/my-project/insight-analytics/js/blog-markets-dashboard.js"
with open(PATH, "r", encoding="utf-8") as f:
    src = f.read()

# (1) Increase height: 360px → 432px (20% more)
src = src.replace("max-height: 360px;", "max-height: 432px;")
print("OK: max-height 360px → 432px (+20%)")

# (2) Strip leading + trailing section-number prefixes from parseBrief content.
# Find the content-cleaning line and add number-prefix stripping.
OLD_CLEAN = "        content = content.replace(/^(what happened|why it matters|what to expect|what to do)[:\\-\\s]*/i, '').trim();"
NEW_CLEAN = """        // Strip leading section name (e.g. "WHAT HAPPENED:") if present
        content = content.replace(/^(what happened|why it matters|what to expect|what to do)[:\\-\\s]*/i, '').trim();
        // Strip leading number prefix (e.g. "1:", "2.", "3 -") — Groq
        // often prefixes section headers with numbers.
        content = content.replace(/^\\d+[:\\.\\-\\)\\s]+/, '').trim();
        // Strip TRAILING number prefix that leaked from the NEXT section's
        // header (e.g. "...backdrop. 3:" — the "3:" is from "3: WHAT TO EXPECT").
        // Matches: optional whitespace, then digits, then optional : . - ),
        // at the very end of the string.
        content = content.replace(/\\s*\\d+[:\\.\\-\\)]?\\s*$/, '').trim();"""

if OLD_CLEAN in src:
    src = src.replace(OLD_CLEAN, NEW_CLEAN)
    print("OK: parseBrief — strip leading + trailing number prefixes")
else:
    print("WARN: parseBrief clean pattern not found")

with open(PATH, "w", encoding="utf-8") as f:
    f.write(src)

# (3) Bump SW + cache-bust
SW = "/tmp/my-project/insight-analytics/service-worker.js"
with open(SW, "r", encoding="utf-8") as f:
    sw = f.read()
sw_new = re.sub(
    r"const VERSION = 'v\d+\.\d+\.\d+-[^']+';",
    "const VERSION = 'v4.91.0-20261009-taller-strip-number-prefix';",
    sw, count=1,
)
sw_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.91.0", sw_new)
if sw_new != sw:
    with open(SW, "w", encoding="utf-8") as f:
        f.write(sw_new)
    print("OK: SW → v4.91.0, ?v= → 4.91.0")

IDX = "/tmp/my-project/insight-analytics/blog/universal-dashboard-builder.html"
with open(IDX, "r", encoding="utf-8") as f:
    idx = f.read()
idx_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.91.0", idx)
if idx_new != idx:
    with open(IDX, "w", encoding="utf-8") as f:
        f.write(idx_new)
    print("OK: blog HTML ?v= → 4.91.0")
