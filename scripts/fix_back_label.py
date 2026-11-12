#!/usr/bin/env python3
"""Change all blog post 'back' labels to 'Back to Live Demos' consistently."""
import os
import glob

BLOG_DIR = "/tmp/my-project/insight-analytics/blog"

count = 0
for path in sorted(glob.glob(os.path.join(BLOG_DIR, "*.html"))):
    fname = os.path.basename(path)
    if fname == "index.html":
        continue  # blog index doesn't have a back link

    with open(path, "r", encoding="utf-8") as f:
        src = f.read()

    # Replace both variants with "Back to Live Demos"
    new_src = src.replace("<span>Back to Blog</span>", "<span>Back to Live Demos</span>")
    new_src = new_src.replace("<span>Back to Demos</span>", "<span>Back to Live Demos</span>")

    if new_src != src:
        with open(path, "w", encoding="utf-8") as f:
            f.write(new_src)
        count += 1
        print(f"OK: {fname}")

print(f"\n{count} blog posts updated → 'Back to Live Demos'")
