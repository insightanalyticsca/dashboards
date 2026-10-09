#!/usr/bin/env python3
"""
Add tiny vertical scrollbars to the AI brief cards so all content is reachable.

Bug: the 4 AI brief cells (What Happened / Why It Matters / What to
Expect / What to Do) have overflow:hidden on .bmd-ai-cell (for the
accent strip's rounded corners). Long AI briefings get clipped mid-
word — user showed "...corn and wheat saw not" and "...defensive asse"
as examples.

Fix: add max-height:120px + overflow-y:auto to .bmd-ai-cell-text.
Style the scrollbar as a tiny 4px bar (Chrome/Safari/Edge via
::-webkit-scrollbar, Firefox via scrollbar-width:thin).

Files patched:
  /tmp/my-project/insight-analytics/js/blog-markets-dashboard.js
  /tmp/my-project/insight-analytics/service-worker.js  (version bump)
"""
import re

PATH = "/tmp/my-project/insight-analytics/js/blog-markets-dashboard.js"
with open(PATH, "r", encoding="utf-8") as f:
    src = f.read()

OLD = """      .bmd-ai-cell-text {
        font-size: 11px; line-height: 1.5;
        color: #cbd5e1;
        word-wrap: break-word;
      }"""

NEW = """      .bmd-ai-cell-text {
        font-size: 11px; line-height: 1.5;
        color: #cbd5e1;
        word-wrap: break-word;
        /* Cap height + add tiny vertical scrollbar so all AI text is
           reachable. Without this, long AI briefings get clipped by the
           cell's overflow:hidden (which is there for the accent strip's
           rounded corners). Now the text area scrolls within the cell. */
        max-height: 120px;
        overflow-y: auto;
        /* Firefox: thin scrollbar */
        scrollbar-width: thin;
        scrollbar-color: rgba(99,102,241,0.3) transparent;
      }
      /* Chrome/Safari/Edge: tiny 4px scrollbar */
      .bmd-ai-cell-text::-webkit-scrollbar { width: 4px; }
      .bmd-ai-cell-text::-webkit-scrollbar-track { background: transparent; }
      .bmd-ai-cell-text::-webkit-scrollbar-thumb { background: rgba(99,102,241,0.3); border-radius: 2px; }
      .bmd-ai-cell-text::-webkit-scrollbar-thumb:hover { background: rgba(99,102,241,0.5); }
      [data-bmd-theme="light"] .bmd-ai-cell-text::-webkit-scrollbar-thumb { background: rgba(99,102,241,0.25); }"""

if OLD in src:
    src = src.replace(OLD, NEW)
    with open(PATH, "w", encoding="utf-8") as f:
        f.write(src)
    print("OK: .bmd-ai-cell-text → max-height:120px + tiny scrollbar")
else:
    print("WARN: pattern not found")

# Bump SW + cache-bust
SW = "/tmp/my-project/insight-analytics/service-worker.js"
with open(SW, "r", encoding="utf-8") as f:
    sw = f.read()
sw_new = re.sub(
    r"const VERSION = 'v\d+\.\d+\.\d+-[^']+';",
    "const VERSION = 'v4.87.0-20261009-ai-card-scroll';",
    sw, count=1,
)
sw_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.87.0", sw_new)
if sw_new != sw:
    with open(SW, "w", encoding="utf-8") as f:
        f.write(sw_new)
    print("OK: SW VERSION → v4.87.0, all ?v= → 4.87.0")

IDX = "/tmp/my-project/insight-analytics/blog/universal-dashboard-builder.html"
with open(IDX, "r", encoding="utf-8") as f:
    idx = f.read()
idx_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.87.0", idx)
if idx_new != idx:
    with open(IDX, "w", encoding="utf-8") as f:
        f.write(idx_new)
    print("OK: blog/universal-dashboard-builder.html ?v= → 4.87.0")
