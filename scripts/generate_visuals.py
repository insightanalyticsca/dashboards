#!/usr/bin/env python3
"""
generate_visuals.py
Builds /data/visuals/<name>.json payloads for the DocChat demo.
Mirrors the .NET MVC GetExecutiveVersionData contract:
  { title, version, asOfLabel, notes[], kpis[], charts[], tables[] }
"""
import json, os
from datetime import datetime, timedelta

ROOT = "/home/z/my-project/download/docchat-demo"
DATA = os.path.join(ROOT, "data", "visuals")
os.makedirs(DATA, exist_ok=True)


def write_json(name, payload):
    path = os.path.join(DATA, name + ".json")
    with open(path, "w") as f:
        json.dump(payload, f, indent=2, default=str)
    print(f"  ✓ {name}.json  ({len(json.dumps(payload))} bytes)")


def months(n=7, start="2026-01-01"):
    d = datetime.fromisoformat(start)
    return [(d + timedelta(days=30 * i)).strftime("%b %Y") for i in range(n)]


def weeks(n=13, start="2026-01-06"):
    d = datetime.fromisoformat(start)
    return [(d + timedelta(days=7 * i)).strftime("%m/%d") for i in range(n)]


# ════════════════════════════════════════════════════════════════════════════
#  EXECUTIVE DASHBOARDS (5) — version 213
# ════════════════════════════════════════════════════════════════════════════

def gen_executive_ar_portfolio():
    payload = {
        "title": "AR Portfolio Executive Summary",
        "version": "213",
        "asOfLabel": "Period: Mar 2026",
        "notes": [
            "Receivables balance decreased $0.4M MoM driven by commercial collections.",
            "91+ day bucket elevated due to 3 large bankruptcy filings — see bankruptcy visual."
        ],
        "kpis": [
            {"label": "Total AR", "value": 14800000, "format": "currency", "delta": -2.6, "deltaMode": "percent", "positiveIsGood": True},
            {"label": "Current % (0-30d)", "value": 62.2, "format": "percent", "delta": 1.4, "deltaMode": "points", "positiveIsGood": True},
            {"label": "91+ Days", "value": 820000, "format": "currency", "delta": 18.3, "deltaMode": "percent", "positiveIsGood": False},
            {"label": "DSO", "value": 41, "format": "number", "delta": -2, "deltaMode": "number", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "ar-buckets-trend",
                "title": "AR Aging Bucket Trend (CAD)",
                "kind": "stackedbar",
                "categories": months(),
                "leftAxisTitle": "CAD (×1000)",
                "series": [
                    {"name": "0-30 days", "type": "stackedbar", "stack": "aging", "data": [8200, 8800, 9200, 9050, 9100, 9200, 9200], "color": "#0808ee"},
                    {"name": "31-60 days", "type": "stackedbar", "stack": "aging", "data": [3400, 3200, 3100, 3150, 3100, 3100, 3100], "color": "#09c698"},
                    {"name": "61-90 days", "type": "stackedbar", "stack": "aging", "data": [1900, 1800, 1700, 1750, 1700, 1700, 1700], "color": "#635BCB"},
                    {"name": "90+ days", "type": "stackedbar", "stack": "aging", "data": [600, 700, 800, 750, 800, 800, 820], "color": "#BBFF05"}
                ]
            },
            {
                "id": "ar-collections-line",
                "title": "Collections vs New AR (CAD ×1000)",
                "kind": "line",
                "categories": months(),
                "leftAxisTitle": "CAD (×1000)",
                "series": [
                    {"name": "New AR", "type": "line", "data": [5200, 5400, 5100, 5300, 4900, 5100, 4900], "color": "#0808ee", "smooth": True},
                    {"name": "Collections", "type": "line", "data": [4900, 5200, 5400, 5100, 5300, 5400, 5500], "color": "#09c698", "smooth": True}
                ]
            },
            {
                "id": "ar-segment-pie",
                "title": "AR by Customer Type",
                "kind": "pie",
                "categories": ["Commercial", "Residential", "Industrial"],
                "series": [{"name": "AR", "type": "pie", "data": [9200, 4500, 1100]}]
            }
        ],
        "tables": [
            {
                "id": "ar-top-customers",
                "title": "Top 5 Commercial Accounts (CAD)",
                "columns": ["Customer", "Balance", "0-30", "31-60", "61-90", "90+"],
                "formats": [None, "currency", "currency", "currency", "currency", "currency"],
                "rows": [
                    ["Acme Industrial Group", 1240000, 890000, 210000, 95000, 45000],
                    ["Northern Logistics Co.", 980000, 720000, 180000, 58000, 24000],
                    ["Pacific Trade Holdings", 760000, 540000, 130000, 62000, 28000],
                    ["EastGate Manufacturing", 620000, 480000, 95000, 32000, 13000],
                    ["Lakeshore Properties Ltd", 540000, 410000, 88000, 28000, 6000]
                ]
            }
        ]
    }
    write_json("executive-ar-portfolio", payload)


def gen_executive_customer_payments():
    payload = {
        "title": "Customer Payments Performance",
        "version": "213",
        "asOfLabel": "Period: Mar 2026",
        "notes": [
            "Auto-pay adoption crossed 60% threshold in commercial segment.",
            "Average payment processing time improved 0.4 days MoM."
        ],
        "kpis": [
            {"label": "Total Payments", "value": 12400000, "format": "currency", "delta": 4.2, "deltaMode": "percent", "positiveIsGood": True},
            {"label": "Auto-Pay Rate", "value": 58.4, "format": "percent", "delta": 2.1, "deltaMode": "points", "positiveIsGood": True},
            {"label": "Avg Days to Pay", "value": 12.3, "format": "decimal", "delta": -0.4, "deltaMode": "number", "positiveIsGood": True},
            {"label": "Failed Payments", "value": 142, "format": "number", "delta": -8, "deltaMode": "percent", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "payments-trend",
                "title": "Payments Volume (CAD ×1000)",
                "kind": "bar",
                "categories": months(),
                "leftAxisTitle": "CAD (×1000)",
                "series": [
                    {"name": "Commercial", "type": "bar", "data": [6200, 6400, 6600, 6800, 6900, 7100, 7300], "color": "#1f4e78"},
                    {"name": "Residential", "type": "bar", "data": [4200, 4300, 4400, 4500, 4600, 4700, 4800], "color": "#38bdf8"}
                ]
            },
            {
                "id": "payment-method-pie",
                "title": "Payment Methods",
                "kind": "pie",
                "categories": ["Auto-Pay", "Online Portal", "Phone", "Mail", "In-Person"],
                "series": [{"name": "% of Payments", "type": "pie", "data": [58, 24, 9, 6, 3]}]
            },
            {
                "id": "days-to-pay-line",
                "title": "Avg Days to Pay",
                "kind": "line",
                "categories": months(),
                "series": [
                    {"name": "Commercial", "type": "line", "data": [14.2, 13.9, 13.5, 13.1, 12.8, 12.5, 12.3], "color": "#1f4e78", "smooth": True},
                    {"name": "Residential", "type": "line", "data": [9.8, 9.6, 9.4, 9.2, 9.0, 8.9, 8.7], "color": "#38bdf8", "smooth": True}
                ]
            }
        ],
        "tables": [
            {
                "id": "payment-methods-table",
                "title": "Payment Methods Breakdown",
                "columns": ["Method", "Volume (CAD)", "% Share", "Avg Days", "Failed %"],
                "formats": [None, "currency", "percent", "decimal", "percent"],
                "rows": [
                    ["Auto-Pay (ACH)", 7230000, 58.3, 0.0, 0.4],
                    ["Online Portal", 2970000, 24.0, 1.2, 0.8],
                    ["Phone (IVR)", 1110000, 9.0, 0.8, 1.2],
                    ["Mail Check", 740000, 6.0, 5.4, 2.1],
                    ["In-Person", 340000, 2.7, 0.0, 0.3]
                ]
            }
        ]
    }
    write_json("executive-customer-payments", payload)


def gen_executive_disconnects_bankruptcies():
    payload = {
        "title": "Disconnects & Bankruptcies",
        "version": "213",
        "asOfLabel": "Period: Mar 2026",
        "notes": [
            "Disconnects down 14% YoY due to proactive outreach program.",
            "Bankruptcies elevated in commercial segment — 3 large filings this period."
        ],
        "kpis": [
            {"label": "Total Disconnects", "value": 1284, "format": "number", "delta": -14.2, "deltaMode": "percent", "positiveIsGood": True},
            {"label": "Reconnect Rate", "value": 38.4, "format": "percent", "delta": 3.2, "deltaMode": "points", "positiveIsGood": True},
            {"label": "Bankruptcies", "value": 47, "format": "number", "delta": 23.7, "deltaMode": "percent", "positiveIsGood": False},
            {"label": "Bankruptcy Value", "value": 820000, "format": "currency", "delta": 18.3, "deltaMode": "percent", "positiveIsGood": False}
        ],
        "charts": [
            {
                "id": "disconnects-trend",
                "title": "Disconnects vs Reconnects (MoM)",
                "kind": "bar",
                "categories": months(),
                "series": [
                    {"name": "Disconnects", "type": "bar", "data": [220, 210, 195, 188, 175, 168, 158], "color": "#0808ee"},
                    {"name": "Reconnects", "type": "bar", "data": [78, 82, 76, 71, 68, 64, 61], "color": "#09c698"}
                ]
            },
            {
                "id": "bankruptcies-by-type",
                "title": "Bankruptcies by Customer Type",
                "kind": "pie",
                "categories": ["Commercial", "Residential", "Industrial"],
                "series": [{"name": "Count", "type": "pie", "data": [28, 14, 5]}]
            },
            {
                "id": "disconnects-line",
                "title": "12-Month Disconnect Trend",
                "kind": "line",
                "categories": months(),
                "series": [
                    {"name": "Commercial", "type": "line", "data": [82, 78, 72, 68, 65, 62, 58], "color": "#0808ee", "smooth": True},
                    {"name": "Residential", "type": "line", "data": [138, 132, 123, 120, 110, 106, 100], "color": "#635BCB", "smooth": True}
                ]
            }
        ],
        "tables": [
            {
                "id": "bankruptcy-list",
                "title": "Recent Bankruptcy Filings",
                "columns": ["Customer", "Segment", "Filed", "Balance", "Status"],
                "formats": [None, None, None, "currency", None],
                "rows": [
                    ["Maple Ridge Holdings Inc.", "Commercial", "2026-03-12", 285000, "In Proceeding"],
                    ["Coastal Logistics LLC", "Commercial", "2026-03-08", 220000, "In Proceeding"],
                    ["Sunset Industrial Park", "Commercial", "2026-02-28", 165000, "Discharged"],
                    ["Birchwood Apartments", "Residential", "2026-02-22", 78000, "In Proceeding"],
                    ["Riverstone Co-op", "Residential", "2026-02-14", 42000, "Discharged"]
                ]
            }
        ]
    }
    write_json("executive-disconnects-bankruptcies", payload)


def gen_executive_ebill_performance():
    payload = {
        "title": "E-Bill Adoption Performance",
        "version": "213",
        "asOfLabel": "Period: Mar 2026",
        "notes": [
            "E-bill adoption reached 47.2% residential, 71.8% commercial — both ahead of target curve.",
            "Promotional incentive drove 2,400 new paperless sign-ups this period."
        ],
        "kpis": [
            {"label": "Residential %", "value": 47.2, "format": "percent", "delta": 2.8, "deltaMode": "points", "positiveIsGood": True},
            {"label": "Commercial %", "value": 71.8, "format": "percent", "delta": 1.4, "deltaMode": "points", "positiveIsGood": True},
            {"label": "Total Paperless", "value": 38400, "format": "number", "delta": 6.2, "deltaMode": "percent", "positiveIsGood": True},
            {"label": "Cost Saved (YTD)", "value": 184000, "format": "currency", "delta": 12.4, "deltaMode": "percent", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "ebill-adoption-line",
                "title": "Adoption Rate Trend (6mo)",
                "kind": "line",
                "categories": months(),
                "leftAxisTitle": "% Adopted",
                "series": [
                    {"name": "Commercial", "type": "line", "data": [68.5, 69.2, 70.1, 70.6, 71.2, 71.5, 71.8], "color": "#0f766e", "smooth": True},
                    {"name": "Residential", "type": "line", "data": [42.1, 43.0, 44.2, 45.0, 46.1, 46.6, 47.2], "color": "#22d3ee", "smooth": True}
                ]
            },
            {
                "id": "ebill-drivers-bar",
                "title": "Adoption Drivers (Commercial)",
                "kind": "bar",
                "categories": ["Invoice Consolidation", "Auto-Pay", "Sustainability", "Mobile App", "Email Reminder"],
                "series": [{"name": "% of Adopters", "type": "bar", "data": [38, 29, 22, 8, 3], "color": "#0f766e"}]
            },
            {
                "id": "ebill-target-pie",
                "title": "Q4 2026 Target Progress",
                "kind": "pie",
                "categories": ["Achieved", "On Track", "Behind", "At Risk"],
                "series": [{"name": "Segments", "type": "pie", "data": [2, 1, 0, 0]}]
            }
        ],
        "tables": [
            {
                "id": "ebill-segment-table",
                "title": "Adoption by Segment",
                "columns": ["Segment", "Total Customers", "Paperless", "% Adopted", "Target Q4'26", "Gap (pts)"],
                "formats": [None, "number", "number", "percent", "percent", "decimal"],
                "rows": [
                    ["Commercial", 8400, 6031, 71.8, 85.0, -13.2],
                    ["Residential", 24800, 11706, 47.2, 65.0, -17.8],
                    ["Industrial", 1200, 936, 78.0, 90.0, -12.0]
                ]
            }
        ]
    }
    write_json("executive-ebill-performance", payload)


def gen_executive_final_bill_recovery():
    payload = {
        "title": "Final Bill Recovery",
        "version": "213",
        "asOfLabel": "Period: Mar 2026",
        "notes": [
            "Final bill recovery rate improved 2.1pts MoM via enhanced deposit application workflow.",
            "Average recovery time reduced from 47 to 38 days."
        ],
        "kpis": [
            {"label": "Recovery Rate", "value": 64.8, "format": "percent", "delta": 2.1, "deltaMode": "points", "positiveIsGood": True},
            {"label": "Recovered (CAD)", "value": 920000, "format": "currency", "delta": 8.4, "deltaMode": "percent", "positiveIsGood": True},
            {"label": "Avg Days to Recover", "value": 38, "format": "number", "delta": -9, "deltaMode": "percent", "positiveIsGood": True},
            {"label": "Write-offs", "value": 480000, "format": "currency", "delta": -3.2, "deltaMode": "percent", "positiveIsGood": True}
        ],
        "charts": [
            {
                "id": "recovery-trend",
                "title": "Recovery Rate Trend (6mo)",
                "kind": "line",
                "categories": months(),
                "leftAxisTitle": "% Recovered",
                "series": [
                    {"name": "Commercial", "type": "line", "data": [58.2, 60.1, 61.4, 62.8, 63.9, 64.3, 64.8], "color": "#4338ca", "smooth": True},
                    {"name": "Residential", "type": "line", "data": [52.1, 53.8, 55.0, 56.4, 57.2, 58.1, 59.0], "color": "#2dd4bf", "smooth": True}
                ]
            },
            {
                "id": "recovery-methods-bar",
                "title": "Recovery Methods",
                "kind": "bar",
                "categories": ["Deposit Applied", "Payment Plan", "Collection Agency", "Legal Action", "Write-off"],
                "series": [{"name": "CAD", "type": "bar", "data": [420000, 280000, 145000, 75000, 48000], "color": "#4338ca"}]
            },
            {
                "id": "recovery-aging-pie",
                "title": "Recovery by Aging Bucket",
                "kind": "pie",
                "categories": ["0-30 days", "31-60 days", "61-90 days", "90+ days"],
                "series": [{"name": "Recovered", "type": "pie", "data": [380000, 240000, 180000, 120000]}]
            }
        ],
        "tables": [
            {
                "id": "recovery-by-segment",
                "title": "Recovery Performance by Segment",
                "columns": ["Segment", "Final Bills Issued", "Recovered (CAD)", "Recovery %", "Avg Days", "Write-offs"],
                "formats": [None, "number", "currency", "percent", "number", "currency"],
                "rows": [
                    ["Commercial", 845, 580000, 68.6, 34, 28000],
                    ["Residential", 2480, 1540000, 62.1, 41, 320000],
                    ["Industrial", 95, 68000, 71.6, 29, 4800]
                ]
            }
        ]
    }
    write_json("executive-final-bill-recovery", payload)


# ════════════════════════════════════════════════════════════════════════════
#  CSR VISUALS (5)
# ════════════════════════════════════════════════════════════════════════════

def gen_aging_bankruptcies():
    payload = {
        "title": "Aging Bankruptcies by Customer Type",
        "asOfLabel": "13-week rolling · Mar 2026",
        "charts": [
            {
                "id": "bk-stacked",
                "title": "Bankruptcies — Commercial vs Residential vs Industrial",
                "kind": "stackedbar",
                "categories": weeks(),
                "leftAxisTitle": "Count",
                "series": [
                    {"name": "Commercial", "type": "stackedbar", "stack": "bk", "data": [3, 4, 2, 5, 4, 6, 3, 5, 4, 6, 4, 5, 4], "color": "#0808ee"},
                    {"name": "Residential", "type": "stackedbar", "stack": "bk", "data": [8, 9, 7, 10, 8, 9, 7, 8, 9, 7, 8, 9, 8], "color": "#09c698"},
                    {"name": "Industrial", "type": "stackedbar", "stack": "bk", "data": [1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 1, 1], "color": "#BBFF05"}
                ]
            }
        ],
        "tables": [
            {
                "id": "bk-table",
                "title": "Recent Bankruptcies — Detail",
                "columns": ["Customer", "Type", "Filed", "Balance (CAD)", "Status"],
                "formats": [None, None, None, "currency", None],
                "rows": [
                    ["Maple Ridge Holdings", "Commercial", "03/12", 285000, "In Proceeding"],
                    ["Birchwood Apartments", "Residential", "03/08", 78000, "In Proceeding"],
                    ["Coastal Logistics LLC", "Commercial", "03/05", 220000, "In Proceeding"],
                    ["Sunset Industrial Park", "Industrial", "02/28", 165000, "Discharged"],
                    ["Riverstone Co-op", "Residential", "02/22", 42000, "Discharged"]
                ]
            }
        ]
    }
    write_json("aging-bankruptcies", payload)


def gen_aging_disconnects_reconnects():
    payload = {
        "title": "Disconnects vs Reconnects",
        "asOfLabel": "13-week rolling · Mar 2026",
        "charts": [
            {
                "id": "dc-rc-trend",
                "title": "Weekly Disconnects & Reconnects",
                "kind": "bar",
                "categories": weeks(),
                "series": [
                    {"name": "Disconnects", "type": "bar", "data": [42, 38, 45, 41, 36, 39, 35, 38, 34, 36, 32, 35, 31], "color": "#0808ee"},
                    {"name": "Reconnects", "type": "bar", "data": [15, 14, 17, 16, 13, 15, 14, 15, 12, 14, 13, 14, 12], "color": "#09c698"}
                ]
            },
            {
                "id": "dc-rate",
                "title": "Reconnect Rate (rolling 4wk avg)",
                "kind": "line",
                "categories": weeks(),
                "leftAxisTitle": "% Reconnected",
                "series": [
                    {"name": "Reconnect Rate", "type": "line", "data": [35.7, 36.8, 37.8, 39.0, 36.1, 38.5, 40.0, 39.5, 35.3, 38.9, 40.6, 40.0, 38.7], "color": "#635BCB", "smooth": True}
                ]
            }
        ],
        "tables": [
            {
                "id": "dc-summary",
                "title": "Disconnect Summary (13wk)",
                "columns": ["Segment", "Disconnects", "Reconnects", "Rate %", "Avg Days to Reconnect"],
                "formats": [None, "number", "number", "percent", "decimal"],
                "rows": [
                    ["Commercial", 142, 54, 38.0, 18.4],
                    ["Residential", 384, 145, 37.8, 22.1],
                    ["Industrial", 31, 12, 38.7, 15.2]
                ]
            }
        ]
    }
    write_json("aging-disconnects-reconnects", payload)


def gen_ar_buckets_stacked():
    payload = {
        "title": "AR Aging Buckets — Stacked (13wk)",
        "asOfLabel": "Period: Mar 2026",
        "charts": [
            {
                "id": "ar-stack",
                "title": "AR Balance by Aging Bucket (CAD ×1000)",
                "kind": "stackedbar",
                "categories": weeks(),
                "leftAxisTitle": "CAD (×1000)",
                "series": [
                    {"name": "0-30 days", "type": "stackedbar", "stack": "ar", "data": [8200, 8400, 8600, 8800, 9000, 9100, 9200, 9150, 9200, 9250, 9200, 9180, 9200], "color": "#0808ee"},
                    {"name": "31-60 days", "type": "stackedbar", "stack": "ar", "data": [3400, 3300, 3200, 3150, 3100, 3100, 3100, 3150, 3100, 3050, 3100, 3150, 3100], "color": "#09c698"},
                    {"name": "61-90 days", "type": "stackedbar", "stack": "ar", "data": [1900, 1850, 1800, 1750, 1700, 1700, 1700, 1750, 1700, 1700, 1700, 1750, 1700], "color": "#635BCB"},
                    {"name": "90+ days", "type": "stackedbar", "stack": "ar", "data": [600, 650, 700, 750, 800, 800, 800, 800, 800, 820, 820, 820, 820], "color": "#BBFF05"}
                ]
            }
        ]
    }
    write_json("ar-buckets-stacked", payload)


def gen_aging_electric_commercial_rolling13():
    payload = {
        "title": "Commercial Electric Arrears — 13wk Rolling",
        "asOfLabel": "Period: Mar 2026",
        "charts": [
            {
                "id": "elec-comm-rolling",
                "title": "Commercial Electric Arrears Trend (CAD ×1000)",
                "kind": "line",
                "categories": weeks(),
                "leftAxisTitle": "CAD (×1000)",
                "series": [
                    {"name": "Current Period", "type": "line", "data": [2400, 2450, 2500, 2480, 2520, 2540, 2560, 2580, 2570, 2590, 2600, 2580, 2600], "color": "#0808ee", "smooth": True},
                    {"name": "Prior Year", "type": "line", "data": [2100, 2150, 2200, 2180, 2220, 2250, 2280, 2300, 2290, 2320, 2340, 2330, 2350], "color": "#c9c9c9", "smooth": True, "lineStyle": {"type": "dashed"}}
                ]
            }
        ]
    }
    write_json("aging-electric-commercial-rolling13", payload)


def gen_aging_forecast_monitor():
    historical = weeks(13, "2026-01-06")
    forecast = weeks(6, "2026-04-06")
    payload = {
        "title": "Aging Forecast Monitor (90-day)",
        "asOfLabel": "Updated: Mar 2026",
        "notes": [
            "Forecast model: ARIMA(2,1,1) trained on trailing 52 weeks.",
            "Confidence band represents 80% prediction interval."
        ],
        "charts": [
            {
                "id": "forecast-line",
                "title": "AR Balance Forecast (CAD ×1000)",
                "kind": "line",
                "categories": historical + forecast,
                "leftAxisTitle": "CAD (×1000)",
                "series": [
                    {"name": "Actual", "type": "line", "data": [14200, 14300, 14100, 14400, 14500, 14600, 14700, 14650, 14750, 14800, 14750, 14800, 14800, None, None, None, None, None, None], "color": "#0808ee", "smooth": True},
                    {"name": "Forecast", "type": "line", "data": [None, None, None, None, None, None, None, None, None, None, None, None, None, 14800, 14920, 15050, 15100, 15200, 15300], "color": "#09c698", "smooth": True, "lineStyle": {"type": "dashed"}},
                    {"name": "Upper Bound", "type": "line", "data": [None, None, None, None, None, None, None, None, None, None, None, None, None, 14800, 15050, 15280, 15420, 15600, 15780], "color": "#635BCB", "smooth": True, "lineStyle": {"opacity": 0.5}, "areaStyle": {"opacity": 0.10}},
                    {"name": "Lower Bound", "type": "line", "data": [None, None, None, None, None, None, None, None, None, None, None, None, None, 14800, 14790, 14820, 14780, 14800, 14820], "color": "#635BCB", "smooth": True, "lineStyle": {"opacity": 0.5}}
                ]
            }
        ]
    }
    write_json("aging-forecast-monitor", payload)


# ════════════════════════════════════════════════════════════════════════════
#  ITS VISUALS (5)
# ════════════════════════════════════════════════════════════════════════════

def gen_its_uptime():
    payload = {
        "title": "ITS Uptime Report — Last Completed Month",
        "asOfLabel": "Feb 2026",
        "notes": [
            "Total services monitored: 24 · Met SLA: 22 · Watch: 2",
            "Overall uptime: 99.974% — within 99.95% SLA target"
        ],
        "charts": [
            {
                "id": "uptime-bar",
                "title": "Service Uptime % by Category",
                "kind": "bar",
                "categories": ["Email", "Web Portal", "API Gateway", "VPN", "Database", "Storage", "DNS", "Backup"],
                "leftAxisTitle": "Uptime %",
                "series": [
                    {"name": "This Month", "type": "bar", "data": [99.98, 99.94, 99.99, 99.91, 99.97, 100.0, 100.0, 99.98], "color": "#0808ee"},
                    {"name": "Last Month", "type": "bar", "data": [99.95, 99.92, 99.97, 99.88, 99.94, 99.99, 100.0, 99.96], "color": "#c9c9c9"}
                ]
            }
        ],
        "tables": [
            {
                "id": "uptime-detail",
                "title": "Service Uptime Detail",
                "columns": ["Service", "Category", "Uptime %", "SLA Target %", "Status", "Incidents"],
                "formats": [None, None, "percent", "percent", None, "number"],
                "rows": [
                    ["Exchange Online", "Email", 99.98, 99.95, "Met", 1],
                    ["Customer Portal", "Web Portal", 99.94, 99.95, "Marginal", 2],
                    ["REST API Gateway", "API", 99.99, 99.95, "Met", 0],
                    ["Corporate VPN", "Network", 99.91, 99.95, "Breach", 3],
                    ["SQL Server Cluster", "Database", 99.97, 99.95, "Met", 1],
                    ["Object Storage", "Storage", 100.0, 99.95, "Met", 0],
                    ["Internal DNS", "Network", 100.0, 99.99, "Met", 0],
                    ["Backup Service", "Backup", 99.98, 99.95, "Met", 0]
                ]
            }
        ]
    }
    write_json("its-uptime", payload)


def gen_its_ticket_volume_open():
    payload = {
        "title": "ITS Ticket Volume — Open vs Closed",
        "asOfLabel": "13-week rolling · Mar 2026",
        "charts": [
            {
                "id": "tickets-open-line",
                "title": "Weekly Open vs Closed Tickets",
                "kind": "line",
                "categories": weeks(),
                "series": [
                    {"name": "Opened", "type": "line", "data": [142, 138, 156, 148, 162, 154, 168, 144, 158, 152, 166, 148, 160], "color": "#0808ee", "smooth": True},
                    {"name": "Closed", "type": "line", "data": [128, 134, 142, 152, 158, 148, 162, 158, 154, 162, 158, 156, 164], "color": "#09c698", "smooth": True}
                ]
            },
            {
                "id": "tickets-by-priority-bar",
                "title": "Open Tickets by Priority",
                "kind": "bar",
                "categories": ["P1 Critical", "P2 High", "P3 Medium", "P4 Low"],
                "series": [{"name": "Count", "type": "bar", "data": [4, 18, 62, 142], "color": "#0808ee"}]
            }
        ]
    }
    write_json("its-ticket-volume-open", payload)


def gen_its_kb4_phish_mom():
    payload = {
        "title": "KB4 Phishing Simulation — MoM Trend",
        "asOfLabel": "Period: Mar 2026",
        "charts": [
            {
                "id": "kb4-phish-mom",
                "title": "Phishing Click Rate (MoM %)",
                "kind": "bar",
                "categories": months(7, "2025-09-01"),
                "leftAxisTitle": "Click Rate %",
                "series": [
                    {"name": "Click Rate %", "type": "bar", "data": [18.7, 17.2, 15.8, 14.4, 13.5, 12.9, 12.3], "color": "#0808ee"}
                ]
            },
            {
                "id": "kb4-by-dept-bar",
                "title": "Click Rate by Department (Q1 2026)",
                "kind": "bar",
                "categories": ["Operations", "Customer Service", "Finance", "HR", "IT", "Sales", "Executive"],
                "series": [{"name": "Click Rate %", "type": "bar", "data": [21.4, 17.8, 9.2, 8.1, 4.3, 12.6, 2.1], "color": "#0808ee"}]
            }
        ],
        "tables": [
            {
                "id": "kb4-summary",
                "title": "KB4 Simulation Summary (Q1 2026)",
                "columns": ["Metric", "Q4 2025", "Q1 2026", "Δ", "Target"],
                "formats": [None, "percent", "percent", "decimal", "percent"],
                "rows": [
                    ["Overall Click Rate", 18.7, 12.3, -6.4, 10.0],
                    ["Report Rate", 22.1, 34.2, 12.1, 40.0],
                    ["Repeat Offender Rate", 8.4, 5.2, -3.2, 3.0],
                    ["Training Completion", 92.1, 96.4, 4.3, 95.0]
                ]
            }
        ]
    }
    write_json("its-kb4-phish-mom", payload)


def gen_its_open_priority():
    payload = {
        "title": "ITS Open Tickets — By Priority",
        "asOfLabel": "Period: Mar 2026",
        "charts": [
            {
                "id": "open-priority-pie",
                "title": "Open Tickets Distribution",
                "kind": "pie",
                "categories": ["P1 Critical", "P2 High", "P3 Medium", "P4 Low"],
                "series": [{"name": "Count", "type": "pie", "data": [4, 18, 62, 142]}]
            },
            {
                "id": "open-age-bar",
                "title": "Avg Age (Days) by Priority",
                "kind": "bar",
                "categories": ["P1 Critical", "P2 High", "P3 Medium", "P4 Low"],
                "leftAxisTitle": "Days",
                "series": [{"name": "Avg Age", "type": "bar", "data": [2.5, 4.8, 9.2, 18.4], "color": "#0808ee"}]
            }
        ],
        "tables": [
            {
                "id": "open-tickets-detail",
                "title": "Open Tickets — All Priorities",
                "columns": ["Ticket #", "Priority", "Category", "Assigned", "Age (Days)", "SLA %"],
                "formats": [None, None, None, None, "decimal", "percent"],
                "rows": [
                    ["INC-10241", "P1", "Network Outage", "J. Patel", 0.4, 92.0],
                    ["INC-10238", "P1", "Database Down", "M. Chen", 1.8, 65.0],
                    ["INC-10234", "P2", "Email Delivery", "S. Rivera", 2.4, 78.0],
                    ["INC-10229", "P2", "VPN Auth Failure", "T. Nguyen", 4.1, 84.0],
                    ["INC-10218", "P3", "Account Locked", "K. Williams", 6.2, 92.0],
                    ["INC-10205", "P3", "Printer Queue", "L. Garcia", 8.8, 96.0],
                    ["REQ-09874", "P4", "Software Install", "B. Kim", 12.4, 99.0],
                    ["REQ-09841", "P4", "Hardware Request", "B. Kim", 21.2, 95.0]
                ]
            }
        ]
    }
    write_json("its-open-priority", payload)


def gen_its_ocsf_report():
    payload = {
        "title": "ITS OCSF Security Events Report",
        "asOfLabel": "Period: Mar 2026",
        "notes": [
            "OCSF = Open Cybersecurity Schema Framework v1.1",
            "Total events processed: 1.2M · Critical: 47 · High: 184"
        ],
        "charts": [
            {
                "id": "ocsf-events-line",
                "title": "Security Events Trend (MoM, ×1000)",
                "kind": "line",
                "categories": months(7, "2025-09-01"),
                "leftAxisTitle": "Events (×1000)",
                "series": [
                    {"name": "Critical", "type": "line", "data": [38, 42, 45, 41, 44, 47, 47], "color": "#ef4444", "smooth": True},
                    {"name": "High", "type": "line", "data": [142, 158, 167, 171, 178, 181, 184], "color": "#f59e0b", "smooth": True},
                    {"name": "Medium", "type": "line", "data": [421, 438, 445, 462, 478, 491, 504], "color": "#0808ee", "smooth": True}
                ]
            },
            {
                "id": "ocsf-category-pie",
                "title": "Events by OCSF Category",
                "kind": "pie",
                "categories": ["Authentication", "Network Activity", "System Activity", "Application", "Findings", "Remediation"],
                "series": [{"name": "Events", "type": "pie", "data": [384, 296, 218, 142, 87, 53]}]
            }
        ],
        "tables": [
            {
                "id": "ocsf-top-events",
                "title": "Top Security Events (Critical)",
                "columns": ["Event ID", "Category", "Source", "Severity", "Status", "Detected"],
                "formats": [None, None, None, None, None, None],
                "rows": [
                    ["EVT-2026-03-4471", "Authentication", "Auth Service", "Critical", "Open", "2026-03-22 14:32"],
                    ["EVT-2026-03-4468", "Network Activity", "Firewall East", "Critical", "Investigating", "2026-03-22 11:08"],
                    ["EVT-2026-03-4462", "System Activity", "DB Cluster A", "Critical", "Resolved", "2026-03-21 22:14"],
                    ["EVT-2026-03-4458", "Authentication", "VPN Gateway", "Critical", "Open", "2026-03-21 16:45"],
                    ["EVT-2026-03-4451", "Application", "Customer Portal", "Critical", "Resolved", "2026-03-21 09:22"]
                ]
            }
        ]
    }
    write_json("its-ocsf-report", payload)


# ════════════════════════════════════════════════════════════════════════════
#  Run
# ════════════════════════════════════════════════════════════════════════════

print("Generating JSON payloads...")
gen_executive_ar_portfolio()
gen_executive_customer_payments()
gen_executive_disconnects_bankruptcies()
gen_executive_ebill_performance()
gen_executive_final_bill_recovery()
gen_aging_bankruptcies()
gen_aging_disconnects_reconnects()
gen_ar_buckets_stacked()
gen_aging_electric_commercial_rolling13()
gen_aging_forecast_monitor()
gen_its_uptime()
gen_its_ticket_volume_open()
gen_its_kb4_phish_mom()
gen_its_open_priority()
gen_its_ocsf_report()
print(f"\nGenerated 15 JSON payloads in {DATA}")
