#!/usr/bin/env python3
"""
Fix: AI brief cards showed truncated text — root cause was max_tokens
too low (600), not CSS clipping.

The user reported that all 4 AI brief cells showed text ending mid-word:
  '...corn and wheat saw not'    (mid-word)
  '...defensive asse'            (mid-word 'assets')

This is classic LLM token-limit truncation — Groq stopped generating
at 600 tokens, cutting off the text mid-generation. The scrollbar I
added in the previous commit had nothing to scroll because the text
was never fully generated.

Fix: increase max_tokens from 600 → 1500. This gives the AI enough
room to complete all 4 sections fully (4 × ~300 tokens = 1200, plus
headers + formatting). The scrollbar fix from the previous commit
still helps for very long briefings, but the primary issue was the
token cap.

Files patched:
  /tmp/my-project/insight-analytics/js/blog-markets-dashboard.js
  /tmp/my-project/insight-analytics/service-worker.js  (version bump)
"""
import re

PATH = "/tmp/my-project/insight-analytics/js/blog-markets-dashboard.js"
with open(PATH, "r", encoding="utf-8") as f:
    src = f.read()

OLD = "max_tokens: 600, stream: true, temperature: 0.3"
NEW = "max_tokens: 1500, stream: true, temperature: 0.3"

if OLD in src:
    src = src.replace(OLD, NEW)
    with open(PATH, "w", encoding="utf-8") as f:
        f.write(src)
    print("OK: max_tokens 600 → 1500 (AI brief won't be truncated mid-word)")
else:
    print("WARN: max_tokens:600 pattern not found")

# Bump SW + cache-bust
SW = "/tmp/my-project/insight-analytics/service-worker.js"
with open(SW, "r", encoding="utf-8") as f:
    sw = f.read()
sw_new = re.sub(
    r"const VERSION = 'v\d+\.\d+\.\d+-[^']+';",
    "const VERSION = 'v4.88.0-20261009-ai-max-tokens-1500';",
    sw, count=1,
)
sw_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.88.0", sw_new)
if sw_new != sw:
    with open(SW, "w", encoding="utf-8") as f:
        f.write(sw_new)
    print("OK: SW VERSION → v4.88.0, all ?v= → 4.88.0")

IDX = "/tmp/my-project/insight-analytics/blog/universal-dashboard-builder.html"
with open(IDX, "r", encoding="utf-8") as f:
    idx = f.read()
idx_new = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.88.0", idx)
if idx_new != idx:
    with open(IDX, "w", encoding="utf-8") as f:
        f.write(idx_new)
    print("OK: blog/universal-dashboard-builder.html ?v= → 4.88.0")
