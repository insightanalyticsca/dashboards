#!/usr/bin/env python3
"""
generate_executive_payloads.py
Build /data/executive/{key}.json in the EXACT shape of .NET ExecutiveVersionPayload.

Mirrors Controllers/DashboardController.ExecutiveVersions.cs:
  ExecutiveVersionPayload {
    Key, Title, Variant, AsOfLabel, GeneratedUtc,
    Metrics: [{ Key, Label, Value, Format, Period, Mom, Yoy, MomLabel, YoyLabel, DeltaMode, PositiveIsGood }],
    Charts:  [{ Id, Title, Kind, Width, ValueFormat, LeftAxisTitle, RightAxisTitle, Categories, Series: [{ Name, Type, Axis, Stack, Color, Smooth, Data }] }],
    Tables:  [{ Id, Title, Width, Kind, Columns, ColumnGroups, Formats: {col: fmt}, Rows: [{col: val}] }],
    Notes:   [str]
  }

Keys: ar, payments, disconnects, ebill, finalbill (matching data-suite on each executive-*.html)
"""
import json, os
from datetime import datetime, timedelta

OUT = "/home/z/my-project/download/docchat-demo/data/executive"
os.makedirs(OUT, exist_ok=True)

NOW_ISO = datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ")


def months_rolling(n=13, end=None):
    end = end or datetime(2026, 3, 1)
    return [(end.replace(day=1) - timedelta(days=30 * i)).replace(day=1) for i in range(n - 1, -1, -1)]


def write(key, payload):
    path = os.path.join(OUT, f"{key}.json")
    with open(path, "w") as f:
        json.dump(payload, f, indent=2, default=str)
    print(f"  ✓ {key}.json  ({len(json.dumps(payload))} bytes)")


# ════════════════════════════════════════════════════════════════════════════
#  AR PORTFOLIO (Key="ar", Variant="cyan")
# ════════════════════════════════════════════════════════════════════════════

def gen_ar():
    periods = months_rolling(13)
    labels = [p.strftime("%b %y") for p in periods]

    payload = {
        "key": "ar",
        "title": "AR Portfolio Executive Summary",
        "variant": "cyan",
        "asOfLabel": "Period: March 2026",
        "generatedUtc": NOW_ISO,
        "metrics": [
            {"key": "total-ar", "label": "Total AR", "value": 14800000, "format": "currency", "period": "Mar 2026", "mom": -2.6, "yoy": 4.2, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True},
            {"key": "current-pct", "label": "Current % (0-30d)", "value": 62.2, "format": "percent", "period": "Mar 2026", "mom": 1.4, "yoy": 2.8, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "points", "positiveIsGood": True},
            {"key": "over90", "label": "91+ Days", "value": 820000, "format": "currency", "period": "Mar 2026", "mom": 18.3, "yoy": -4.1, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": False},
            {"key": "dso", "label": "DSO", "value": 41, "format": "number", "period": "Mar 2026", "mom": -2, "yoy": -3, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "number", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "ar-buckets-trend",
                "title": "AR Aging Bucket Trend (CAD)",
                "kind": "stackedbar",
                "width": "wide",
                "valueFormat": "currency",
                "leftAxisTitle": "CAD",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "0-30 days", "type": "bar", "axis": "left", "stack": "aging", "color": "#0808ee", "smooth": False, "data": [8200, 8400, 8600, 8800, 9000, 9100, 9200, 9150, 9200, 9250, 9200, 9180, 9200]},
                    {"name": "31-60 days", "type": "bar", "axis": "left", "stack": "aging", "color": "#09c698", "smooth": False, "data": [3400, 3300, 3200, 3150, 3100, 3100, 3100, 3150, 3100, 3050, 3100, 3150, 3100]},
                    {"name": "61-90 days", "type": "bar", "axis": "left", "stack": "aging", "color": "#635BCB", "smooth": False, "data": [1900, 1850, 1800, 1750, 1700, 1700, 1700, 1750, 1700, 1700, 1700, 1750, 1700]},
                    {"name": "90+ days", "type": "bar", "axis": "left", "stack": "aging", "color": "#BBFF05", "smooth": False, "data": [600, 650, 700, 750, 800, 800, 800, 800, 800, 820, 820, 820, 820]}
                ]
            },
            {
                "id": "ar-collections-line",
                "title": "Collections vs New AR (CAD ×1000)",
                "kind": "line",
                "width": "wide",
                "valueFormat": "currency",
                "leftAxisTitle": "CAD (×1000)",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "New AR", "type": "line", "axis": "left", "stack": "", "color": "#0808ee", "smooth": True, "data": [5200, 5400, 5100, 5300, 4900, 5100, 4900, 5300, 5200, 5400, 5100, 5300, 4900]},
                    {"name": "Collections", "type": "line", "axis": "left", "stack": "", "color": "#09c698", "smooth": True, "data": [4900, 5200, 5400, 5100, 5300, 5400, 5500, 5300, 5400, 5600, 5500, 5700, 5800]}
                ]
            },
            {
                "id": "ar-segment-pie",
                "title": "AR by Customer Type",
                "kind": "pie",
                "width": "narrow",
                "valueFormat": "currency",
                "leftAxisTitle": "",
                "rightAxisTitle": "",
                "categories": ["Commercial", "Residential", "Industrial"],
                "series": [
                    {"name": "AR", "type": "pie", "axis": "left", "stack": "", "color": "#0808ee", "smooth": False, "data": [9200, 4500, 1100]}
                ]
            }
        ],
        "tables": [
            {
                "id": "ar-top-customers",
                "title": "Top 5 Commercial Accounts (CAD)",
                "width": "wide",
                "kind": "table",
                "columns": ["Customer", "Balance", "0-30", "31-60", "61-90", "90+"],
                "columnGroups": [],
                "formats": {"Balance": "currency", "0-30": "currency", "31-60": "currency", "61-90": "currency", "90+": "currency"},
                "rows": [
                    {"Customer": "Acme Industrial Group", "Balance": 1240000, "0-30": 890000, "31-60": 210000, "61-90": 95000, "90+": 45000},
                    {"Customer": "Northern Logistics Co.", "Balance": 980000, "0-30": 720000, "31-60": 180000, "61-90": 58000, "90+": 24000},
                    {"Customer": "Pacific Trade Holdings", "Balance": 760000, "0-30": 540000, "31-60": 130000, "61-90": 62000, "90+": 28000},
                    {"Customer": "EastGate Manufacturing", "Balance": 620000, "0-30": 480000, "31-60": 95000, "61-90": 32000, "90+": 13000},
                    {"Customer": "Lakeshore Properties Ltd", "Balance": 540000, "0-30": 410000, "31-60": 88000, "61-90": 28000, "90+": 6000}
                ]
            }
        ],
        "notes": [
            "Receivables balance decreased $0.4M MoM driven by commercial collections.",
            "91+ day bucket elevated due to 3 large bankruptcy filings — see bankruptcy visual."
        ]
    }
    write("ar", payload)


# ════════════════════════════════════════════════════════════════════════════
#  CUSTOMER PAYMENTS (Key="payments", Variant="blue")
# ════════════════════════════════════════════════════════════════════════════

def gen_payments():
    periods = months_rolling(13)
    labels = [p.strftime("%b %y") for p in periods]

    payload = {
        "key": "payments",
        "title": "Customer Payments Performance",
        "variant": "blue",
        "asOfLabel": "Period: March 2026",
        "generatedUtc": NOW_ISO,
        "metrics": [
            {"key": "total-payments", "label": "Total Payments", "value": 12400000, "format": "currency", "period": "Mar 2026", "mom": 4.2, "yoy": 8.1, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True},
            {"key": "autopay-rate", "label": "Auto-Pay Rate", "value": 58.4, "format": "percent", "period": "Mar 2026", "mom": 2.1, "yoy": 5.4, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "points", "positiveIsGood": True},
            {"key": "avg-days", "label": "Avg Days to Pay", "value": 12.3, "format": "decimal", "period": "Mar 2026", "mom": -0.4, "yoy": -1.2, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "number", "positiveIsGood": True},
            {"key": "failed", "label": "Failed Payments", "value": 142, "format": "number", "period": "Mar 2026", "mom": -8, "yoy": -22, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "payments-trend",
                "title": "Payments Volume (CAD ×1000)",
                "kind": "bar",
                "width": "wide",
                "valueFormat": "currency",
                "leftAxisTitle": "CAD (×1000)",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "Commercial", "type": "bar", "axis": "left", "stack": "", "color": "#1f4e78", "smooth": False, "data": [6200, 6300, 6400, 6500, 6600, 6700, 6800, 6900, 7000, 7100, 7200, 7300, 7300]},
                    {"name": "Residential", "type": "bar", "axis": "left", "stack": "", "color": "#38bdf8", "smooth": False, "data": [4200, 4250, 4300, 4350, 4400, 4450, 4500, 4550, 4600, 4650, 4700, 4750, 4800]}
                ]
            },
            {
                "id": "payment-method-pie",
                "title": "Payment Methods",
                "kind": "pie",
                "width": "narrow",
                "valueFormat": "percent",
                "leftAxisTitle": "",
                "rightAxisTitle": "",
                "categories": ["Auto-Pay", "Online Portal", "Phone", "Mail", "In-Person"],
                "series": [
                    {"name": "% of Payments", "type": "pie", "axis": "left", "stack": "", "color": "#1f4e78", "smooth": False, "data": [58, 24, 9, 6, 3]}
                ]
            },
            {
                "id": "days-to-pay-line",
                "title": "Avg Days to Pay",
                "kind": "line",
                "width": "wide",
                "valueFormat": "decimal",
                "leftAxisTitle": "Days",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "Commercial", "type": "line", "axis": "left", "stack": "", "color": "#1f4e78", "smooth": True, "data": [14.2, 14.0, 13.9, 13.7, 13.5, 13.4, 13.1, 12.9, 12.8, 12.6, 12.5, 12.4, 12.3]},
                    {"name": "Residential", "type": "line", "axis": "left", "stack": "", "color": "#38bdf8", "smooth": True, "data": [9.8, 9.7, 9.6, 9.5, 9.4, 9.3, 9.2, 9.1, 9.0, 8.9, 8.8, 8.7, 8.7]}
                ]
            }
        ],
        "tables": [
            {
                "id": "payment-methods-table",
                "title": "Payment Methods Breakdown",
                "width": "wide",
                "kind": "table",
                "columns": ["Method", "Volume (CAD)", "% Share", "Avg Days", "Failed %"],
                "columnGroups": [],
                "formats": {"Volume (CAD)": "currency", "% Share": "percent", "Avg Days": "decimal", "Failed %": "percent"},
                "rows": [
                    {"Method": "Auto-Pay (ACH)", "Volume (CAD)": 7230000, "% Share": 58.3, "Avg Days": 0.0, "Failed %": 0.4},
                    {"Method": "Online Portal", "Volume (CAD)": 2970000, "% Share": 24.0, "Avg Days": 1.2, "Failed %": 0.8},
                    {"Method": "Phone (IVR)", "Volume (CAD)": 1110000, "% Share": 9.0, "Avg Days": 0.8, "Failed %": 1.2},
                    {"Method": "Mail Check", "Volume (CAD)": 740000, "% Share": 6.0, "Avg Days": 5.4, "Failed %": 2.1},
                    {"Method": "In-Person", "Volume (CAD)": 340000, "% Share": 2.7, "Avg Days": 0.0, "Failed %": 0.3}
                ]
            }
        ],
        "notes": [
            "Auto-pay adoption crossed 60% threshold in commercial segment.",
            "Average payment processing time improved 0.4 days MoM."
        ]
    }
    write("payments", payload)


# ════════════════════════════════════════════════════════════════════════════
#  DISCONNECTS & BANKRUPTCIES (Key="disconnects", Variant="teal")
# ════════════════════════════════════════════════════════════════════════════

def gen_disconnects():
    periods = months_rolling(13)
    labels = [p.strftime("%b %y") for p in periods]

    payload = {
        "key": "disconnects",
        "title": "Disconnects & Bankruptcies",
        "variant": "teal",
        "asOfLabel": "Period: March 2026",
        "generatedUtc": NOW_ISO,
        "metrics": [
            {"key": "total-dc", "label": "Total Disconnects", "value": 1284, "format": "number", "period": "Mar 2026", "mom": -14.2, "yoy": -8.4, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True},
            {"key": "reconnect-rate", "label": "Reconnect Rate", "value": 38.4, "format": "percent", "period": "Mar 2026", "mom": 3.2, "yoy": 5.8, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "points", "positiveIsGood": True},
            {"key": "bankruptcies", "label": "Bankruptcies", "value": 47, "format": "number", "period": "Mar 2026", "mom": 23.7, "yoy": 12.4, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": False},
            {"key": "bk-value", "label": "Bankruptcy Value", "value": 820000, "format": "currency", "period": "Mar 2026", "mom": 18.3, "yoy": 7.2, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": False}
        ],
        "charts": [
            {
                "id": "disconnects-trend",
                "title": "Disconnects vs Reconnects (MoM)",
                "kind": "bar",
                "width": "wide",
                "valueFormat": "number",
                "leftAxisTitle": "Count",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "Disconnects", "type": "bar", "axis": "left", "stack": "", "color": "#0808ee", "smooth": False, "data": [220, 218, 215, 210, 208, 205, 200, 195, 188, 180, 175, 168, 158]},
                    {"name": "Reconnects", "type": "bar", "axis": "left", "stack": "", "color": "#09c698", "smooth": False, "data": [78, 80, 82, 78, 76, 75, 72, 70, 68, 65, 64, 62, 61]}
                ]
            },
            {
                "id": "bankruptcies-by-type",
                "title": "Bankruptcies by Customer Type",
                "kind": "pie",
                "width": "narrow",
                "valueFormat": "number",
                "leftAxisTitle": "",
                "rightAxisTitle": "",
                "categories": ["Commercial", "Residential", "Industrial"],
                "series": [
                    {"name": "Count", "type": "pie", "axis": "left", "stack": "", "color": "#0808ee", "smooth": False, "data": [28, 14, 5]}
                ]
            },
            {
                "id": "disconnects-line",
                "title": "13-Month Disconnect Trend",
                "kind": "line",
                "width": "wide",
                "valueFormat": "number",
                "leftAxisTitle": "Count",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "Commercial", "type": "line", "axis": "left", "stack": "", "color": "#0808ee", "smooth": True, "data": [82, 80, 78, 76, 74, 72, 70, 68, 65, 62, 60, 58, 56]},
                    {"name": "Residential", "type": "line", "axis": "left", "stack": "", "color": "#635BCB", "smooth": True, "data": [138, 135, 132, 128, 125, 122, 118, 115, 110, 108, 106, 102, 100]}
                ]
            }
        ],
        "tables": [
            {
                "id": "bankruptcy-list",
                "title": "Recent Bankruptcy Filings",
                "width": "wide",
                "kind": "table",
                "columns": ["Customer", "Segment", "Filed", "Balance", "Status"],
                "columnGroups": [],
                "formats": {"Balance": "currency"},
                "rows": [
                    {"Customer": "Maple Ridge Holdings Inc.", "Segment": "Commercial", "Filed": "2026-03-12", "Balance": 285000, "Status": "In Proceeding"},
                    {"Customer": "Coastal Logistics LLC", "Segment": "Commercial", "Filed": "2026-03-08", "Balance": 220000, "Status": "In Proceeding"},
                    {"Customer": "Sunset Industrial Park", "Segment": "Commercial", "Filed": "2026-02-28", "Balance": 165000, "Status": "Discharged"},
                    {"Customer": "Birchwood Apartments", "Segment": "Residential", "Filed": "2026-02-22", "Balance": 78000, "Status": "In Proceeding"},
                    {"Customer": "Riverstone Co-op", "Segment": "Residential", "Filed": "2026-02-14", "Balance": 42000, "Status": "Discharged"}
                ]
            }
        ],
        "notes": [
            "Disconnects down 14% YoY due to proactive outreach program.",
            "Bankruptcies elevated in commercial segment — 3 large filings this period."
        ]
    }
    write("disconnects", payload)


# ════════════════════════════════════════════════════════════════════════════
#  E-BILL PERFORMANCE (Key="ebill", Variant="violet")
# ════════════════════════════════════════════════════════════════════════════

def gen_ebill():
    periods = months_rolling(13)
    labels = [p.strftime("%b %y") for p in periods]

    payload = {
        "key": "ebill",
        "title": "E-Bill Adoption Performance",
        "variant": "violet",
        "asOfLabel": "Through March 2026",
        "generatedUtc": NOW_ISO,
        "metrics": [
            {"key": "res-pct", "label": "Residential %", "value": 47.2, "format": "percent2", "period": "Mar 2026", "mom": 2.8, "yoy": 6.4, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "points", "positiveIsGood": True},
            {"key": "com-pct", "label": "Commercial %", "value": 71.8, "format": "percent2", "period": "Mar 2026", "mom": 1.4, "yoy": 3.8, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "points", "positiveIsGood": True},
            {"key": "total-paperless", "label": "Total Paperless", "value": 38400, "format": "number", "period": "Mar 2026", "mom": 6.2, "yoy": 14.1, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True},
            {"key": "cost-saved", "label": "Cost Saved (YTD)", "value": 184000, "format": "currency", "period": "Mar 2026", "mom": 12.4, "yoy": 28.6, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "ebill-adoption-line",
                "title": "Adoption Rate Trend (13mo)",
                "kind": "line",
                "width": "wide",
                "valueFormat": "percent2",
                "leftAxisTitle": "% Adopted",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "Commercial", "type": "line", "axis": "left", "stack": "", "color": "#845EF7", "smooth": True, "data": [68.5, 68.8, 69.2, 69.5, 69.8, 70.1, 70.4, 70.6, 70.9, 71.2, 71.4, 71.5, 71.8]},
                    {"name": "Residential", "type": "line", "axis": "left", "stack": "", "color": "#22d3ee", "smooth": True, "data": [42.1, 42.4, 42.8, 43.2, 43.6, 44.0, 44.4, 44.8, 45.2, 45.6, 46.1, 46.6, 47.2]}
                ]
            },
            {
                "id": "ebill-drivers-bar",
                "title": "Adoption Drivers (Commercial)",
                "kind": "bar",
                "width": "narrow",
                "valueFormat": "percent",
                "leftAxisTitle": "% of Adopters",
                "rightAxisTitle": "",
                "categories": ["Invoice Consolidation", "Auto-Pay", "Sustainability", "Mobile App", "Email Reminder"],
                "series": [
                    {"name": "% of Adopters", "type": "bar", "axis": "left", "stack": "", "color": "#845EF7", "smooth": False, "data": [38, 29, 22, 8, 3]}
                ]
            }
        ],
        "tables": [
            {
                "id": "ebill-segment-table",
                "title": "Adoption by Segment",
                "width": "wide",
                "kind": "table",
                "columns": ["Segment", "Total Customers", "Paperless", "% Adopted", "Target Q4'26", "Gap (pts)"],
                "columnGroups": [],
                "formats": {"Total Customers": "number", "Paperless": "number", "% Adopted": "percent2", "Target Q4'26": "percent2", "Gap (pts)": "decimal"},
                "rows": [
                    {"Segment": "Commercial", "Total Customers": 8400, "Paperless": 6031, "% Adopted": 71.8, "Target Q4'26": 85.0, "Gap (pts)": -13.2},
                    {"Segment": "Residential", "Total Customers": 24800, "Paperless": 11706, "% Adopted": 47.2, "Target Q4'26": 65.0, "Gap (pts)": -17.8},
                    {"Segment": "Industrial", "Total Customers": 1200, "Paperless": 936, "% Adopted": 78.0, "Target Q4'26": 90.0, "Gap (pts)": -12.0}
                ]
            }
        ],
        "notes": [
            "E-bill adoption reached 47.2% residential, 71.8% commercial — both ahead of target curve.",
            "Promotional incentive drove 2,400 new paperless sign-ups this period."
        ]
    }
    write("ebill", payload)


# ════════════════════════════════════════════════════════════════════════════
#  FINAL BILL RECOVERY (Key="finalbill", Variant="indigo")
# ════════════════════════════════════════════════════════════════════════════

def gen_finalbill():
    periods = months_rolling(13)
    labels = [p.strftime("%b %y") for p in periods]

    payload = {
        "key": "finalbill",
        "title": "Final Bill Recovery",
        "variant": "indigo",
        "asOfLabel": "Period: March 2026",
        "generatedUtc": NOW_ISO,
        "metrics": [
            {"key": "recovery-rate", "label": "Recovery Rate", "value": 64.8, "format": "percent", "period": "Mar 2026", "mom": 2.1, "yoy": 4.8, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "points", "positiveIsGood": True},
            {"key": "recovered", "label": "Recovered (CAD)", "value": 920000, "format": "currency", "period": "Mar 2026", "mom": 8.4, "yoy": 12.2, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True},
            {"key": "avg-days", "label": "Avg Days to Recover", "value": 38, "format": "number", "period": "Mar 2026", "mom": -9, "yoy": -14, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True},
            {"key": "writeoffs", "label": "Write-offs", "value": 480000, "format": "currency", "period": "Mar 2026", "mom": -3.2, "yoy": -8.4, "momLabel": "MoM", "yoyLabel": "YoY", "deltaMode": "percent", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "recovery-trend",
                "title": "Recovery Rate Trend (13mo)",
                "kind": "line",
                "width": "wide",
                "valueFormat": "percent",
                "leftAxisTitle": "% Recovered",
                "rightAxisTitle": "",
                "categories": labels,
                "series": [
                    {"name": "Commercial", "type": "line", "axis": "left", "stack": "", "color": "#4338ca", "smooth": True, "data": [58.2, 58.8, 59.4, 60.1, 60.7, 61.4, 62.0, 62.5, 62.8, 63.4, 63.9, 64.3, 64.8]},
                    {"name": "Residential", "type": "line", "axis": "left", "stack": "", "color": "#2dd4bf", "smooth": True, "data": [52.1, 52.6, 53.2, 53.8, 54.4, 55.0, 55.6, 56.2, 56.8, 57.4, 58.1, 58.6, 59.0]}
                ]
            },
            {
                "id": "recovery-methods-bar",
                "title": "Recovery Methods (CAD)",
                "kind": "bar",
                "width": "narrow",
                "valueFormat": "currency",
                "leftAxisTitle": "CAD",
                "rightAxisTitle": "",
                "categories": ["Deposit Applied", "Payment Plan", "Collection Agency", "Legal Action", "Write-off"],
                "series": [
                    {"name": "CAD", "type": "bar", "axis": "left", "stack": "", "color": "#4338ca", "smooth": False, "data": [420000, 280000, 145000, 75000, 48000]}
                ]
            },
            {
                "id": "recovery-aging-pie",
                "title": "Recovery by Aging Bucket",
                "kind": "pie",
                "width": "narrow",
                "valueFormat": "currency",
                "leftAxisTitle": "",
                "rightAxisTitle": "",
                "categories": ["0-30 days", "31-60 days", "61-90 days", "90+ days"],
                "series": [
                    {"name": "Recovered", "type": "pie", "axis": "left", "stack": "", "color": "#4338ca", "smooth": False, "data": [380000, 240000, 180000, 120000]}
                ]
            }
        ],
        "tables": [
            {
                "id": "recovery-by-segment",
                "title": "Recovery Performance by Segment",
                "width": "wide",
                "kind": "table",
                "columns": ["Segment", "Final Bills Issued", "Recovered (CAD)", "Recovery %", "Avg Days", "Write-offs"],
                "columnGroups": [],
                "formats": {"Final Bills Issued": "number", "Recovered (CAD)": "currency", "Recovery %": "percent", "Avg Days": "number", "Write-offs": "currency"},
                "rows": [
                    {"Segment": "Commercial", "Final Bills Issued": 845, "Recovered (CAD)": 580000, "Recovery %": 68.6, "Avg Days": 34, "Write-offs": 28000},
                    {"Segment": "Residential", "Final Bills Issued": 2480, "Recovered (CAD)": 1540000, "Recovery %": 62.1, "Avg Days": 41, "Write-offs": 320000},
                    {"Segment": "Industrial", "Final Bills Issued": 95, "Recovered (CAD)": 68000, "Recovery %": 71.6, "Avg Days": 29, "Write-offs": 4800}
                ]
            }
        ],
        "notes": [
            "Final bill recovery rate improved 2.1pts MoM via enhanced deposit application workflow.",
            "Average recovery time reduced from 47 to 38 days."
        ]
    }
    write("finalbill", payload)


# ════════════════════════════════════════════════════════════════════════════
print("Generating executive payloads in ExecutiveVersionPayload shape...")
gen_ar()
gen_payments()
gen_disconnects()
gen_ebill()
gen_finalbill()
print(f"\nDone. Files in {OUT}")
