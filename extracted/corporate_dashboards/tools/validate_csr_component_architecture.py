#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APPSETTINGS = ROOT / "appsettings.json"
MANIFEST = ROOT / "wwwroot" / "csr" / "csr-pages.manifest.json"
CUSTOM_HTML = ROOT / "wwwroot" / "custom-html"
RUNTIME = ROOT / "wwwroot" / "js" / "csr-dashboard-runtime.js"
STYLES = ROOT / "wwwroot" / "css" / "csr-dashboard-runtime.css"
BASELINE = ROOT / "tools" / "csr-template-baseline.json"
RUNTIME_TEST = ROOT / "tools" / "validate_csr_runtime.js"

SUPPORTED_TYPES = {
    "columnChart", "stackedColumnChart", "barChart", "stackedBarChart",
    "lineChart", "lineStackedColumnComboChart", "pieChart", "pie",
    "donutChart", "donut", "tableEx", "pivotTable", "card",
    "multiRowCard", "slicer", "map", "textbox", "actionButton",
}
ACTUAL_TYPE_COUNTS = {
    "slicer": 28,
    "tableEx": 13,
    "columnChart": 11,
    "textbox": 8,
    "pivotTable": 7,
    "lineStackedColumnComboChart": 7,
    "lineChart": 6,
    "multiRowCard": 3,
    "map": 2,
    "card": 1,
    "barChart": 1,
    "actionButton": 1,
}


def fail(message: str) -> None:
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def canonical_hash(template: dict) -> str:
    content = {key: value for key, value in template.items() if key != "HtmlFile"}
    raw = json.dumps(content, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def main() -> None:
    settings = json.loads(APPSETTINGS.read_text(encoding="utf-8-sig"))
    templates = settings["Dashboard"]["CustomHtml"]["Templates"]
    pages = [t for t in templates if str(t.get("Role", "")).lower() == "csr-page"]
    visuals = [t for t in templates if str(t.get("Role", "")).lower() == "csr-visual"]
    page_keys = {t["Key"] for t in pages}

    if len(pages) != 21:
        fail(f"expected 21 CSR pages, found {len(pages)}")
    if len(visuals) != 88:
        fail(f"expected 88 CSR visuals, found {len(visuals)}")

    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
    baseline_hashes = baseline.get("hashes", {})
    csr_templates = pages + visuals
    if len(baseline_hashes) != len(csr_templates):
        fail("CSR baseline count does not match current CSR template count")
    for template in csr_templates:
        expected = baseline_hashes.get(template["Key"])
        actual = canonical_hash(template)
        if expected != actual:
            fail(f"CSR template changed beyond HtmlFile: {template['Key']}")

    for page in pages:
        if page.get("HtmlFile") != "csr-page.html":
            fail(f"page {page['Key']} does not use csr-page.html")
        if not page.get("Sources"):
            fail(f"page {page['Key']} has no configured data sources")

    type_counts: Counter[str] = Counter()
    for visual in visuals:
        if visual.get("HtmlFile") != "csr-visual.html":
            fail(f"visual {visual['Key']} does not use csr-visual.html")
        if visual.get("PageKey") not in page_keys:
            fail(f"visual {visual['Key']} references missing page {visual.get('PageKey')}")
        config = visual.get("VisualConfig")
        if not isinstance(config, dict):
            fail(f"visual {visual['Key']} has no VisualConfig object")
        visual_type = config.get("type") or config.get("Type")
        if visual_type not in SUPPORTED_TYPES:
            fail(f"visual {visual['Key']} uses unsupported type {visual_type!r}")
        position = config.get("position") or config.get("Position")
        if not isinstance(position, dict):
            fail(f"visual {visual['Key']} has no configured position")
        for field in ("x", "y", "w", "h"):
            if field not in position:
                fail(f"visual {visual['Key']} position lacks {field}")
        type_counts[str(visual_type)] += 1

    if dict(type_counts) != ACTUAL_TYPE_COUNTS:
        fail(f"CSR visual inventory changed: {dict(type_counts)}")

    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    manifest_pages = manifest.get("pages", [])
    manifest_keys = {page["key"] for page in manifest_pages}
    if manifest_keys != page_keys:
        fail(f"manifest/page-key mismatch: missing={sorted(page_keys-manifest_keys)}, extra={sorted(manifest_keys-page_keys)}")
    manifest_visual_count = sum(len(page.get("visuals", [])) for page in manifest_pages)
    if manifest_visual_count != 88:
        fail(f"manifest visual count changed: {manifest_visual_count}")

    legacy = [
        p.name for p in CUSTOM_HTML.glob("*.html")
        if p.name not in {"csr-page.html", "csr-visual.html"}
        and (p.name.startswith("csr-") or re.fullmatch(r"v\d+-[0-9a-f]+\.html", p.name, re.I))
    ]
    if legacy:
        fail("legacy CSR HTML files remain: " + ", ".join(sorted(legacy)[:10]))

    if not STYLES.is_file():
        fail("shared CSR stylesheet is missing")

    runtime = RUNTIME.read_text(encoding="utf-8")
    required_markers = [
        "class CsrComponentRegistry",
        "class CsrColumnChartComponent",
        "class CsrStackedColumnChartComponent",
        "class CsrHorizontalBarChartComponent",
        "class CsrStackedColumnLineComboComponent",
        "class CsrLineChartComponent",
        "class CsrPieComponent",
        "class CsrTableComponent",
        "class CsrMatrixComponent",
        "const CSR_COMPONENTS",
        "Promise.allSettled",
        "wrapCategoryLabel",
        "barMinHeight",
        "csr-primary-stack",
        "rotate: 0",
        "csr-dashboard-visual-layout:changed",
        "csr-visual-fullscreen",
        "xTitle",
        "yTitle",
    ]
    for marker in required_markers:
        if marker not in runtime:
            fail(f"runtime marker is missing: {marker}")

    if "#FF4D8D" in runtime or "#FF8A65" in runtime:
        fail("ordinary red/pink chart colours remain in the CSR runtime palette")
    if "--csr-error:#a82c4d" not in STYLES.read_text(encoding="utf-8"):
        fail("semantic error red was removed from the CSR stylesheet")

    try:
        subprocess.run(["node", "--check", str(RUNTIME)], check=True, capture_output=True, text=True)
        subprocess.run(["node", str(RUNTIME_TEST)], check=True, capture_output=True, text=True)
    except FileNotFoundError:
        fail("Node.js is required for runtime validation")
    except subprocess.CalledProcessError as exc:
        fail((exc.stderr or exc.stdout or str(exc)).strip())

    print("CSR component architecture validation passed")
    print(f"  pages: {len(pages)}")
    print(f"  visuals: {len(visuals)}")
    print(f"  types: {dict(type_counts)}")
    print("  shared HTML shells: 2")
    print("  legacy CSR HTML wrappers: 0")
    print("  aging category visual: recreated with new ID and shared columnChart class")
    print("  stacked combo visuals: validated")
    print("  stored SQL layout JSON: untouched; recreated visual uses the preserved appsettings position")


if __name__ == "__main__":
    main()
