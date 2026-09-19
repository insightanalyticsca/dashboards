#!/usr/bin/env python3
"""
Recalculate all date-based labels in dashboard JSONs relative to TODAY.

For executive JSONs (ar, disconnects, ebill, finalbill, payments):
  - asOfLabel: "Period: {current_month_name} {current_year}"
  - generatedUtc: today's ISO timestamp
  - metric[].period: "{current_month_short} {current_year}"
  - chart categories with month labels (e.g., "Jun 25"): shift to end at current month

For chatters.json:
  - asOfLabel: "Week {iso_week} · {current_month_short} {current_year} · Demo Build"
  - generatedUtc: today's ISO timestamp
  - metric[].period: stays "TTM" (trailing twelve months — always correct)

For CSR/ITS version JSONs:
  - _meta.generatedUtc: today's ISO timestamp
"""
import json
import os
from datetime import date, timedelta
from pathlib import Path

REPO = Path('/home/z/my-project')

# Today's date
TODAY = date.today()
ISO_WEEK = TODAY.isocalendar()[1]
CURRENT_YEAR_2 = TODAY.year % 100  # e.g., 26 for 2026
CURRENT_YEAR_4 = TODAY.year
CURRENT_MONTH_NUM = TODAY.month
CURRENT_MONTH_ABBR = TODAY.strftime('%b')  # Sep
CURRENT_MONTH_FULL = TODAY.strftime('%B')  # September

print(f'Today: {TODAY.isoformat()} (ISO week {ISO_WEEK})')
print(f'Current month: {CURRENT_MONTH_ABBR} {CURRENT_YEAR_4}')
print()

# ─── Helper: generate N month labels ending at the current month ──────────
# Format: "MMM YY" (e.g., "Sep 26")
def generate_month_labels(n):
    """Generate N month labels ending at the current month, going backwards."""
    labels = []
    d = TODAY.replace(day=1)  # first day of current month
    for i in range(n):
        labels.append(f'{d.strftime("%b")} {d.year % 100:02d}')
        # Go back one month
        if d.month == 1:
            d = d.replace(year=d.year - 1, month=12)
        else:
            d = d.replace(month=d.month - 1)
    labels.reverse()
    return labels

# Generate 13 months (matching the original count in executive JSONs)
MONTH_LABELS_13 = generate_month_labels(13)
print(f'13-month labels (ending at current month): {MONTH_LABELS_13[:3]}...{MONTH_LABELS_13[-3:]}')

# ─── Helper: detect if a category array contains month labels ──────────────
import re
MONTH_PATTERN = re.compile(r'^[A-Z][a-z]{2} \d{2}$')

def is_month_labels(categories):
    """Check if an array of strings looks like month labels (e.g., 'Jun 25')."""
    if not categories or len(categories) < 3:
        return False
    return sum(1 for c in categories if MONTH_PATTERN.match(str(c))) >= len(categories) * 0.7

# ─── Helper: replace month labels in a categories array ───────────────────
def replace_month_labels(categories, new_labels):
    """Replace month-label categories with new ones, preserving the count."""
    if len(categories) == len(new_labels):
        return new_labels[:]
    elif len(categories) < len(new_labels):
        return new_labels[-len(categories):]
    else:
        # More categories than new labels — pad from the front
        diff = len(categories) - len(new_labels)
        return new_labels[:diff] + new_labels  # shouldn't happen, but safe

# ─── Update executive JSONs ───────────────────────────────────────────────
exec_files = [
    'data/executive/ar.json',
    'data/executive/disconnects.json',
    'data/executive/ebill.json',
    'data/executive/finalbill.json',
    'data/executive/payments.json',
]

print('\n=== Executive JSONs (ar, disconnects, ebill, finalbill, payments) ===')
for fpath in exec_files:
    full_path = REPO / fpath
    with open(full_path) as f:
        d = json.load(f)

    old_asof = d.get('asOfLabel', '')
    # Update asOfLabel
    if 'ebill' in fpath:
        d['asOfLabel'] = f'Through {CURRENT_MONTH_FULL} {CURRENT_YEAR_4}'
    else:
        d['asOfLabel'] = f'Period: {CURRENT_MONTH_FULL} {CURRENT_YEAR_4}'

    # Update generatedUtc
    d['generatedUtc'] = TODAY.isoformat() + 'T00:00:00Z'

    # Update metric periods
    for m in d.get('metrics', []):
        if m.get('period') and 'TTM' not in m['period']:
            m['period'] = f'{CURRENT_MONTH_ABBR} {CURRENT_YEAR_4}'

    # Update chart categories with month labels
    charts_updated = 0
    for c in d.get('charts', []):
        cats = c.get('categories', [])
        if is_month_labels(cats):
            new_cats = replace_month_labels(cats, MONTH_LABELS_13)
            c['categories'] = new_cats
            charts_updated += 1

    with open(full_path, 'w') as f:
        json.dump(d, f, indent=2, ensure_ascii=False)

    print(f'  {fpath}: asOf "{old_asof}" → "{d["asOfLabel"]}", {charts_updated} charts updated')

# ─── Update chatters.json ─────────────────────────────────────────────────
chatters_path = REPO / 'data/executive/chatters.json'
with open(chatters_path) as f:
    d = json.load(f)

old_asof = d.get('asOfLabel', '')
d['asOfLabel'] = f'Week {ISO_WEEK} · {CURRENT_MONTH_ABBR} {CURRENT_YEAR_4} · Demo Build'
d['generatedUtc'] = TODAY.isoformat() + 'T00:00:00Z'

with open(chatters_path, 'w') as f:
    json.dump(d, f, indent=2, ensure_ascii=False)

print(f'\n  data/executive/chatters.json: asOf "{old_asof}" → "{d["asOfLabel"]}"')

# ─── Update CSR/ITS version JSONs ──────────────────────────────────────────
version_dir = REPO / 'data/versions'
version_files = sorted([f for f in os.listdir(version_dir) if f.endswith('.json')])

print(f'\n=== CSR/ITS version JSONs ({len(version_files)} files) ===')
for fname in version_files:
    fpath = version_dir / fname
    with open(fpath) as f:
        d = json.load(f)

    # Update _meta.generatedUtc
    meta = d.get('_meta', {})
    if meta:
        old_utc = meta.get('generatedUtc', '')
        meta['generatedUtc'] = TODAY.isoformat() + 'T00:00:00Z'
        d['_meta'] = meta

    # Also check for any top-level date fields
    if 'asOfLabel' in d:
        d['asOfLabel'] = f'Period: {CURRENT_MONTH_FULL} {CURRENT_YEAR_4}'
    if 'generatedUtc' in d and '_meta' not in d:
        d['generatedUtc'] = TODAY.isoformat() + 'T00:00:00Z'

    with open(fpath, 'w') as f:
        json.dump(d, f, indent=2, ensure_ascii=False)

    if meta:
        print(f'  {fname}: generatedUtc → {meta["generatedUtc"]}')

print(f'\n✓ All temporal labels updated to today ({TODAY.isoformat()}, ISO week {ISO_WEEK})')
print(f'  - 5 executive JSONs: asOfLabel + metric.period + chart categories (13 months ending {CURRENT_MONTH_ABBR} {CURRENT_YEAR_2:02d})')
print(f'  - chatters.json: asOfLabel = Week {ISO_WEEK} · {CURRENT_MONTH_ABBR} {CURRENT_YEAR_4}')
print(f'  - 11 CSR/ITS JSONs: _meta.generatedUtc')
