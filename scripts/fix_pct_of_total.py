#!/usr/bin/env python3
"""
Fix: % of total shows >100% for some device groups.

Bug: the percentage was calculated as (device_count / totalVisits) * 100.
But totalVisits was reduced by the IP-reconciliation pass (which
subtracted excluded-IP visits from totalVisits and ips) WITHOUT also
reducing the devices counts. So sum(device_counts) > totalVisits,
making individual percentages exceed 100%.

Example (current blob state):
  totalVisits = 19  (after reconciliation subtracted owner's 35 visits)
  devices = {Safari·iPhone: 10, Chrome·Windows: 35, Bot·Other: 12}
  sum(devices) = 57
  Chrome·Windows % = (35 / 19) * 100 = 184%  ← WRONG

Fix: use sum of the DISPLAYED device counts as the denominator. This
guarantees percentages always sum to 100%, regardless of reconciliation
mismatches between totalVisits and the device counters.

Files patched (3 % calculations each = 6 total):
  /tmp/my-project/insight-analytics/index.html     (main site — 3 calcs)
  /home/z/my-project/index.html                    (dashboards site — 3 calcs)
"""
import re

# ═══ MAIN SITE ═════════════════════════════════════════════════════════════
MAIN = "/tmp/my-project/insight-analytics/index.html"
with open(MAIN, "r", encoding="utf-8") as f:
    src = f.read()

# (1) renderAllHistory — the per-site all-time device table.
# Add `sumOfDevices` before the forEach, use it as the denominator.
MAIN_OLD_1 = """    var html = '<div style="margin-top:16px;padding:14px;border-radius:12px;border:1px solid var(--card-border,rgba(0,0,0,0.10));background:linear-gradient(135deg,rgba(99,102,241,0.04),rgba(6,182,212,0.03));">';
    html += '<div style="font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;color:var(--muted,#94a3b8);margin-bottom:10px;">All-time (' + devices.length + ' types, ' + ips.length + ' endpoints, ' + total + ' total)</div>';
    html += '<table style="width:100%;border-collapse:collapse;font-size:11px;font-family:-apple-system,BlinkMacSystemFont,sans-serif;">';
    html += '<thead><tr style="border-bottom:1px solid rgba(0,0,0,0.06);color:var(--muted,#94a3b8);font-weight:600;font-size:9px;text-transform:uppercase;letter-spacing:0.03em;"><th style="padding:5px 8px;text-align:left;">Client</th><th style="padding:5px 8px;text-align:right;white-space:nowrap;">Count</th><th style="padding:5px 8px;text-align:right;white-space:nowrap;">% of total</th></tr></thead><tbody>';
    devices.forEach(function(d, i) {
      var bg = i % 2 === 0 ? 'transparent' : 'rgba(99,102,241,0.03)';
      var pct = total > 0 ? ((d.visits / total) * 100).toFixed(1) + '%' : '—';
      var bar = total > 0 ? Math.min(100, (d.visits / total) * 100) : 0;"""

MAIN_NEW_1 = """    var html = '<div style="margin-top:16px;padding:14px;border-radius:12px;border:1px solid var(--card-border,rgba(0,0,0,0.10));background:linear-gradient(135deg,rgba(99,102,241,0.04),rgba(6,182,212,0.03));">';
    html += '<div style="font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;color:var(--muted,#94a3b8);margin-bottom:10px;">All-time (' + devices.length + ' types, ' + ips.length + ' endpoints, ' + total + ' total)</div>';
    html += '<table style="width:100%;border-collapse:collapse;font-size:11px;font-family:-apple-system,BlinkMacSystemFont,sans-serif;">';
    html += '<thead><tr style="border-bottom:1px solid rgba(0,0,0,0.06);color:var(--muted,#94a3b8);font-weight:600;font-size:9px;text-transform:uppercase;letter-spacing:0.03em;"><th style="padding:5px 8px;text-align:left;">Client</th><th style="padding:5px 8px;text-align:right;white-space:nowrap;">Count</th><th style="padding:5px 8px;text-align:right;white-space:nowrap;">% of total</th></tr></thead><tbody>';
    // Use sum of DISPLAYED device counts as the denominator (not totalVisits).
    // totalVisits was reduced by IP-reconciliation without reducing device
    // counts, so (device / totalVisits) could exceed 100%. Sum-of-devices
    // guarantees percentages always sum to 100%.
    var sumOfDevices = devices.reduce(function(s, d) { return s + d.visits; }, 0);
    devices.forEach(function(d, i) {
      var bg = i % 2 === 0 ? 'transparent' : 'rgba(99,102,241,0.03)';
      var pct = sumOfDevices > 0 ? ((d.visits / sumOfDevices) * 100).toFixed(1) + '%' : '—';
      var bar = sumOfDevices > 0 ? Math.min(100, (d.visits / sumOfDevices) * 100) : 0;"""

if MAIN_OLD_1 in src:
    src = src.replace(MAIN_OLD_1, MAIN_NEW_1)
    print("OK: main site — renderAllHistory % → sumOfDevices denominator")
else:
    print("WARN: main site renderAllHistory pattern not found")

# (2) renderState — the all-time unique devices table (from op=visits cumulative).
MAIN_OLD_2 = """    var deviceCounts = cum.deviceCounts || {};
    var totalCum = cum.totalVisits || 0;"""

MAIN_NEW_2 = """    var deviceCounts = cum.deviceCounts || {};
    var totalCum = cum.totalVisits || 0;
    // Use sum of DISPLAYED device counts as the denominator (not totalCum).
    // totalCum was reduced by IP-reconciliation without reducing device
    // counts, so (cnt / totalCum) could exceed 100%.
    var sumOfDeviceCounts = Object.values(deviceCounts).reduce(function(a, b) { return a + b; }, 0);"""

if MAIN_OLD_2 in src:
    src = src.replace(MAIN_OLD_2, MAIN_NEW_2)
    print("OK: main site — sumOfDeviceCounts computed")
else:
    print("WARN: main site renderState setup pattern not found")

# (3) Now change the % calculation in renderState to use sumOfDeviceCounts
MAIN_OLD_3 = "      var pct = totalCum > 0 ? ((cnt / totalCum) * 100).toFixed(1) : '0.0';"
MAIN_NEW_3 = "      var pct = sumOfDeviceCounts > 0 ? ((cnt / sumOfDeviceCounts) * 100).toFixed(1) : '0.0';"

if MAIN_OLD_3 in src:
    src = src.replace(MAIN_OLD_3, MAIN_NEW_3)
    print("OK: main site — renderState % → sumOfDeviceCounts denominator")
else:
    print("WARN: main site renderState % pattern not found")

with open(MAIN, "w", encoding="utf-8") as f:
    f.write(src)


# ═══ DASHBOARDS SITE ═══════════════════════════════════════════════════════
DASH = "/home/z/my-project/index.html"
with open(DASH, "r", encoding="utf-8") as f:
    src = f.read()

# (1) renderAllHistoryDevices — the all-time device table.
DASH_OLD_1 = """    var html = '<div style="margin-top:16px;padding:14px;border-radius:12px;border:1px solid var(--card-border,rgba(0,0,0,0.10));background:linear-gradient(135deg,rgba(99,102,241,0.04),rgba(6,182,212,0.03));">';
    html += '<div style="font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;color:var(--muted,#94a3b8);margin-bottom:10px;">Unique devices (all history — ' + devices.length + ' types, ' + ips.length + ' IPs, ' + totalVisits + ' total visits)</div>';

    // Device table
    html += '<table style="width:100%;border-collapse:collapse;font-size:11px;font-family:-apple-system,BlinkMacSystemFont,sans-serif;">';
    html += '<thead><tr style="border-bottom:1px solid rgba(0,0,0,0.06);color:var(--muted,#94a3b8);font-weight:600;font-size:9px;text-transform:uppercase;letter-spacing:0.03em;">';
    html += '<th style="padding:5px 8px;text-align:left;">Device</th>';
    html += '<th style="padding:5px 8px;text-align:right;white-space:nowrap;">Visits</th>';
    html += '<th style="padding:5px 8px;text-align:right;white-space:nowrap;">% of total</th>';
    html += '</tr></thead><tbody>';
    devices.forEach(function(d, i) {
      var bg = i % 2 === 0 ? 'transparent' : 'rgba(99,102,241,0.03)';
      var pct = totalVisits > 0 ? ((d.visits / totalVisits) * 100).toFixed(1) + '%' : '—';
      var bar = totalVisits > 0 ? Math.min(100, (d.visits / totalVisits) * 100) : 0;"""

DASH_NEW_1 = """    var html = '<div style="margin-top:16px;padding:14px;border-radius:12px;border:1px solid var(--card-border,rgba(0,0,0,0.10));background:linear-gradient(135deg,rgba(99,102,241,0.04),rgba(6,182,212,0.03));">';
    html += '<div style="font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;color:var(--muted,#94a3b8);margin-bottom:10px;">Unique devices (all history — ' + devices.length + ' types, ' + ips.length + ' IPs, ' + totalVisits + ' total visits)</div>';

    // Device table
    html += '<table style="width:100%;border-collapse:collapse;font-size:11px;font-family:-apple-system,BlinkMacSystemFont,sans-serif;">';
    html += '<thead><tr style="border-bottom:1px solid rgba(0,0,0,0.06);color:var(--muted,#94a3b8);font-weight:600;font-size:9px;text-transform:uppercase;letter-spacing:0.03em;">';
    html += '<th style="padding:5px 8px;text-align:left;">Device</th>';
    html += '<th style="padding:5px 8px;text-align:right;white-space:nowrap;">Visits</th>';
    html += '<th style="padding:5px 8px;text-align:right;white-space:nowrap;">% of total</th>';
    html += '</tr></thead><tbody>';
    // Use sum of DISPLAYED device counts as the denominator (not totalVisits).
    // totalVisits was reduced by IP-reconciliation without reducing device
    // counts, so (device / totalVisits) could exceed 100%.
    var sumOfDevices = devices.reduce(function(s, d) { return s + d.visits; }, 0);
    devices.forEach(function(d, i) {
      var bg = i % 2 === 0 ? 'transparent' : 'rgba(99,102,241,0.03)';
      var pct = sumOfDevices > 0 ? ((d.visits / sumOfDevices) * 100).toFixed(1) + '%' : '—';
      var bar = sumOfDevices > 0 ? Math.min(100, (d.visits / sumOfDevices) * 100) : 0;"""

if DASH_OLD_1 in src:
    src = src.replace(DASH_OLD_1, DASH_NEW_1)
    print("OK: dashboards site — renderAllHistoryDevices % → sumOfDevices")
else:
    print("WARN: dashboards site renderAllHistoryDevices pattern not found")

# (2) renderVisits — the all-time unique devices table (from cumulative).
DASH_OLD_2 = """    var ipCounts = cum.ipCounts || {};
    var deviceCounts = cum.deviceCounts || {};
    var totalCumVisits = cum.totalVisits || 0;"""

DASH_NEW_2 = """    var ipCounts = cum.ipCounts || {};
    var deviceCounts = cum.deviceCounts || {};
    var totalCumVisits = cum.totalVisits || 0;
    // Use sum of DISPLAYED device counts as the denominator (not totalCumVisits).
    // totalCumVisits was reduced by IP-reconciliation without reducing device
    // counts, so (cnt / totalCumVisits) could exceed 100%.
    var sumOfDeviceCounts = Object.values(deviceCounts).reduce(function(a, b) { return a + b; }, 0);"""

if DASH_OLD_2 in src:
    src = src.replace(DASH_OLD_2, DASH_NEW_2)
    print("OK: dashboards site — sumOfDeviceCounts computed")
else:
    print("WARN: dashboards site renderVisits setup pattern not found")

# (3) Change the % calculation to use sumOfDeviceCounts
DASH_OLD_3 = "      var pct = totalCumVisits > 0 ? ((cnt / totalCumVisits) * 100).toFixed(1) : '0.0';"
DASH_NEW_3 = "      var pct = sumOfDeviceCounts > 0 ? ((cnt / sumOfDeviceCounts) * 100).toFixed(1) : '0.0';"

if DASH_OLD_3 in src:
    src = src.replace(DASH_OLD_3, DASH_NEW_3)
    print("OK: dashboards site — renderVisits % → sumOfDeviceCounts")
else:
    print("WARN: dashboards site renderVisits % pattern not found")

with open(DASH, "w", encoding="utf-8") as f:
    f.write(src)


# ═══ Bump SW + cache-bust ═════════════════════════════════════════════════
for path, version in [
    ("/tmp/my-project/insight-analytics/service-worker.js", "v4.86.0-20261009-pct-of-total-fix"),
    ("/home/z/my-project/service-worker.js", "dashboards-v21"),
]:
    with open(path, "r", encoding="utf-8") as f:
        sw = f.read()
    if "insight-analytics" in path:
        new_sw = re.sub(r"const VERSION = 'v\d+\.\d+\.\d+-[^']+';", f"const VERSION = '{version}';", sw, count=1)
        new_sw = re.sub(r"\?v=\d+\.\d+\.\d+", "?v=4.86.0", new_sw)
    else:
        new_sw = re.sub(r"var CACHE = 'dashboards-v\d+';", f"var CACHE = '{version}';", sw, count=1)
        new_sw = re.sub(r"\?v=\d{8}", "?v=20261009", new_sw)
    if new_sw != sw:
        with open(path, "w", encoding="utf-8") as f:
            f.write(new_sw)
        print(f"OK: {path} → {version}")

for path, find, repl in [
    ("/tmp/my-project/insight-analytics/index.html", r"\?v=\d+\.\d+\.\d+", "?v=4.86.0"),
    ("/home/z/my-project/index.html", r"\?v=\d{8}", "?v=20261009"),
]:
    with open(path, "r", encoding="utf-8") as f:
        idx = f.read()
    new_idx = re.sub(find, repl, idx)
    if new_idx != idx:
        with open(path, "w", encoding="utf-8") as f:
            f.write(new_idx)
        print(f"OK: {path} cache-bust → {repl}")
