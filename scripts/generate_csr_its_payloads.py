#!/usr/bin/env python3
"""
generate_csr_its_payloads.py
Build /data/csr/<name>.json and /data/its/<name>.json in the exact shape
the .NET GetCustomHtmlLiveData endpoint returns:

  { found, mode, connectionName, schema, obj, objectType, agg,
    rowFields, colFields, valueFields, data: [...rows...], debug }

Each visual's findRows()/rowsOf() searches recursively for the data array,
so the visuals are tolerant of shape — but we match the controller's
response shape faithfully.

Row field names match what each visual's render() function looks up
via keyOf(row, [...]) — confirmed by reading the source.
"""
import json, os
from datetime import datetime, timedelta

ROOT = "/home/z/my-project/download/docchat-demo"
CSR_OUT = os.path.join(ROOT, "data", "csr")
ITS_OUT = os.path.join(ROOT, "data", "its")
os.makedirs(CSR_OUT, exist_ok=True)
os.makedirs(ITS_OUT, exist_ok=True)


def wrap(schema, obj, data, mode="rawRows"):
    """Wrap rows in the GetCustomHtmlLiveData response shape."""
    return {
        "found": True,
        "mode": mode,
        "connectionName": "build",
        "schema": schema,
        "obj": obj,
        "objectType": "view",
        "agg": "RawRows",
        "rowFields": [],
        "colFields": [],
        "valueFields": [],
        "data": data,
        "debug": {
            "source": f"{schema}.{obj}",
            "returnedRows": len(data),
            "requestedMaxRows": 50000
        }
    }


def write(sector, name, payload):
    out = CSR_OUT if sector == "csr" else ITS_OUT
    path = os.path.join(out, f"{name}.json")
    with open(path, "w") as f:
        json.dump(payload, f, indent=2, default=str)
    print(f"  ✓ {sector}/{name}.json  ({len(payload['data'])} rows, {len(json.dumps(payload))} bytes)")


def weeks(n=13, start="2026-01-06"):
    d = datetime.fromisoformat(start)
    return [(d + timedelta(days=7 * i)).strftime("%Y-%m-%d") for i in range(n)]


def months(n=13, end="2026-03-01"):
    end = datetime.fromisoformat(end)
    return [(end.replace(day=1) - timedelta(days=30 * i)).replace(day=1) for i in range(n - 1, -1, -1)]


# ════════════════════════════════════════════════════════════════════════════
#  CSR VISUALS
# ════════════════════════════════════════════════════════════════════════════

def gen_aging_bankruptcies():
    """aging-bankruptcies.html: looks for Type/Category/Name, Metric/Measure/Bucket, Value/Amount/Total/Count
    Metrics expected: 'Month #', 'Month $', 'YTD #', 'YTD $'
    Types: Commercial, Residential, Industrial"""
    types = ["Commercial", "Residential", "Industrial"]
    metrics = ["Month #", "Month $", "YTD #", "YTD $"]
    rows = []
    for t in types:
        # Each type x metric combo is one row
        base_count = {"Commercial": 28, "Residential": 14, "Industrial": 5}[t]
        base_amt = {"Commercial": 285000, "Residential": 78000, "Industrial": 165000}[t]
        for m in metrics:
            if m == "Month #":
                rows.append({"Type": t, "Metric": m, "Value": base_count})
            elif m == "Month $":
                rows.append({"Type": t, "Metric": m, "Value": base_amt})
            elif m == "YTD #":
                rows.append({"Type": t, "Metric": m, "Value": base_count * 3})
            elif m == "YTD $":
                rows.append({"Type": t, "Metric": m, "Value": base_amt * 3})
    write("csr", "aging-bankruptcies", wrap("dbo", "vw_aging_bankruptcies", rows))


def gen_aging_disconnects_reconnects():
    """Same shape as aging-bankruptcies: Type x Metric x Value
    Types: Commercial, Residential, Industrial
    Metrics: Month #, Month $, YTD #, YTD $"""
    types = ["Commercial", "Residential", "Industrial"]
    metrics = ["Month #", "Month $", "YTD #", "YTD $"]
    base = {
        "Commercial":   {"count": 142, "amt": 284000},
        "Residential":  {"count": 384, "amt": 768000},
        "Industrial":   {"count": 31,  "amt": 62000}
    }
    rows = []
    for t in types:
        for m in metrics:
            if m == "Month #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"]})
            elif m == "Month $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"]})
            elif m == "YTD #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"] * 3})
            elif m == "YTD $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"] * 3})
    write("csr", "aging-disconnects-reconnects", wrap("dbo", "vw_aging_disconnects_reconnects", rows))


def gen_ar_buckets_stacked():
    """ar-buckets-stacked.html: needs PeriodDate + (AgingBucket + Amount) [tall format]
    Buckets: '0-30', '31-60', '61-90', '>90'"""
    wks = weeks(13)
    buckets = [
        ("0-30",  [8200, 8400, 8600, 8800, 9000, 9100, 9200, 9150, 9200, 9250, 9200, 9180, 9200]),
        ("31-60", [3400, 3300, 3200, 3150, 3100, 3100, 3100, 3150, 3100, 3050, 3100, 3150, 3100]),
        ("61-90", [1900, 1850, 1800, 1750, 1700, 1700, 1700, 1750, 1700, 1700, 1700, 1750, 1700]),
        (">90",   [600, 650, 700, 750, 800, 800, 800, 800, 800, 820, 820, 820, 820])
    ]
    rows = []
    for i, w in enumerate(wks):
        for bucket, values in buckets:
            rows.append({
                "PeriodDate": w,
                "PeriodKey": f"W_{i+1}",
                "PeriodLabel": f"Wk {i+1}",
                "SortOrder": i + 1,
                "AgingBucket": bucket,
                "Amount": values[i]
            })
    write("csr", "ar-buckets-stacked", wrap("dbo", "vw_ar_buckets_weekly", rows))


def gen_aging_electric_commercial_rolling13():
    """aging-electric-commercial-rolling13.html: needs PeriodDate + Amount (current + prior year)"""
    wks = weeks(13)
    rows = []
    for i, w in enumerate(wks):
        rows.append({
            "PeriodDate": w,
            "PeriodKey": f"W_{i+1}",
            "PeriodLabel": f"Wk {i+1}",
            "SortOrder": i + 1,
            "CategoryGroup": "Commercial",
            "Amount": 2400 + i * 15,
            "PriorYearAmount": 2100 + i * 20
        })
    write("csr", "aging-electric-commercial-rolling13", wrap("dbo", "vw_aging_electric_commercial_rolling13", rows))


def gen_aging_electric_residential_rolling13():
    wks = weeks(13)
    rows = []
    for i, w in enumerate(wks):
        rows.append({
            "PeriodDate": w,
            "PeriodKey": f"W_{i+1}",
            "PeriodLabel": f"Wk {i+1}",
            "SortOrder": i + 1,
            "CategoryGroup": "Residential",
            "Amount": 1800 + i * 12,
            "PriorYearAmount": 1650 + i * 15
        })
    write("csr", "aging-electric-residential-rolling13", wrap("dbo", "vw_aging_electric_residential_rolling13", rows))


def gen_aging_bankruptcies_by_customer_type():
    """aging-bankruptcies-by-customer-type.html: similar pattern, by customer type"""
    types = ["Commercial", "Residential", "Industrial"]
    metrics = ["Month #", "Month $", "YTD #", "YTD $"]
    base = {
        "Commercial":   {"count": 28, "amt": 285000},
        "Residential":  {"count": 14, "amt": 78000},
        "Industrial":   {"count": 5,  "amt": 165000}
    }
    rows = []
    for t in types:
        for m in metrics:
            if m == "Month #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"]})
            elif m == "Month $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"]})
            elif m == "YTD #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"] * 3})
            elif m == "YTD $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"] * 3})
    write("csr", "aging-bankruptcies-by-customer-type", wrap("dbo", "vw_aging_bankruptcies_by_customer_type", rows))


def gen_aging_disconnects_by_customer_type():
    types = ["Commercial", "Residential", "Industrial"]
    metrics = ["Month #", "Month $", "YTD #", "YTD $"]
    base = {
        "Commercial":   {"count": 142, "amt": 284000},
        "Residential":  {"count": 384, "amt": 768000},
        "Industrial":   {"count": 31,  "amt": 62000}
    }
    rows = []
    for t in types:
        for m in metrics:
            if m == "Month #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"]})
            elif m == "Month $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"]})
            elif m == "YTD #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"] * 3})
            elif m == "YTD $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"] * 3})
    write("csr", "aging-disconnects-by-customer-type", wrap("dbo", "vw_aging_disconnects_by_customer_type", rows))


def gen_aging_commercial_delta():
    """aging-commercial-delta.html: period + delta values"""
    wks = weeks(13)
    rows = []
    for i, w in enumerate(wks):
        rows.append({
            "PeriodDate": w,
            "PeriodKey": f"W_{i+1}",
            "SortOrder": i + 1,
            "CategoryGroup": "Commercial",
            "Amount": 5200 + i * 20,
            "PriorAmount": 4900 + i * 25,
            "Delta": (5200 + i * 20) - (4900 + i * 25)
        })
    write("csr", "aging-commercial-delta", wrap("dbo", "vw_aging_commercial_delta", rows))


def gen_aging_residential_delta():
    wks = weeks(13)
    rows = []
    for i, w in enumerate(wks):
        rows.append({
            "PeriodDate": w,
            "PeriodKey": f"W_{i+1}",
            "SortOrder": i + 1,
            "CategoryGroup": "Residential",
            "Amount": 3100 + i * 12,
            "PriorAmount": 2950 + i * 15,
            "Delta": (3100 + i * 12) - (2950 + i * 15)
        })
    write("csr", "aging-residential-delta", wrap("dbo", "vw_aging_residential_delta", rows))


def gen_ar_delta_stacked():
    wks = weeks(13)
    buckets = [("0-30", [120, 130, 125, 135, 128, 140, 132]), ("31-60", [40, 38, 42, 35, 45, 41, 39]),
               ("61-90", [18, 20, 17, 22, 19, 21, 18]), (">90", [8, 9, 7, 10, 8, 9, 8])]
    rows = []
    for i, w in enumerate(wks[:7]):
        for bucket, values in buckets:
            rows.append({
                "PeriodDate": w,
                "PeriodKey": f"W_{i+1}",
                "SortOrder": i + 1,
                "AgingBucket": bucket,
                "Amount": values[i]
            })
    write("csr", "ar-delta-stacked", wrap("dbo", "vw_ar_delta_stacked", rows))


def gen_ar_buckets_tabular():
    """ar-buckets-tabular.html: tabular view"""
    types = ["Commercial", "Residential", "Industrial"]
    metrics = ["Month #", "Month $", "YTD #", "YTD $"]
    base = {
        "Commercial":   {"count": 142, "amt": 9200000},
        "Residential":  {"count": 384, "amt": 4500000},
        "Industrial":   {"count": 31,  "amt": 1100000}
    }
    rows = []
    for t in types:
        for m in metrics:
            if m == "Month #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"]})
            elif m == "Month $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"]})
            elif m == "YTD #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"] * 3})
            elif m == "YTD $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"] * 3})
    write("csr", "ar-buckets-tabular", wrap("dbo", "vw_ar_buckets_tabular", rows))


def gen_ar_delta_tabular():
    types = ["Commercial", "Residential", "Industrial"]
    metrics = ["Month #", "Month $", "YTD #", "YTD $"]
    base = {
        "Commercial":   {"count": 12, "amt": 95000},
        "Residential":  {"count": 28, "amt": 56000},
        "Industrial":   {"count": 2,  "amt": 14000}
    }
    rows = []
    for t in types:
        for m in metrics:
            if m == "Month #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"]})
            elif m == "Month $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"]})
            elif m == "YTD #":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["count"] * 3})
            elif m == "YTD $":
                rows.append({"Type": t, "Metric": m, "Value": base[t]["amt"] * 3})
    write("csr", "ar-delta-tabular", wrap("dbo", "vw_ar_delta_tabular", rows))


# ════════════════════════════════════════════════════════════════════════════
#  ITS VISUALS
# ════════════════════════════════════════════════════════════════════════════

def gen_its_uptime():
    """its-uptime.html: rows have month_start, category, service, uptime_pct, sla_pct, sla_breach_status"""
    month_start = "2026-02-01"
    services = [
        ("Email",          "Exchange Online",     99.98, 99.95, "Met"),
        ("Web Portal",     "Customer Portal",     99.94, 99.95, "Marginal"),
        ("API",            "REST API Gateway",    99.99, 99.95, "Met"),
        ("Network",        "Corporate VPN",       99.91, 99.95, "Breach"),
        ("Database",       "SQL Server Cluster",  99.97, 99.95, "Met"),
        ("Storage",        "Object Storage",      100.0, 99.95, "Met"),
        ("Network",        "Internal DNS",        100.0, 99.99, "Met"),
        ("Backup",         "Backup Service",      99.98, 99.95, "Met")
    ]
    rows = []
    for cat, svc, up, sla, status in services:
        rows.append({
            "month_start": month_start,
            "category": cat,
            "Category": cat,
            "Service": svc,
            "service": svc,
            "uptime_pct": up,
            "UptimePct": up,
            "sla_pct": sla,
            "SlaPct": sla,
            "sla_breach_status": status,
            "SlaBreachStatus": status,
            "status": status
        })
    write("its", "its-uptime", wrap("dbo", "vw_its_uptime_monthly", rows))


def gen_its_ticket_volume_open():
    """its-ticket-volume-open.html: looks for period_sort + ticket counts"""
    mnths = months(13)
    rows = []
    for i, m in enumerate(mnths):
        rows.append({
            "period_sort": i + 1,
            "Period Sort": i + 1,
            "PeriodSort": i + 1,
            "month_start": m.strftime("%Y-%m-%d"),
            "MonthStart": m.strftime("%Y-%m-%d"),
            "Opened": 142 + (i % 4) * 8,
            "Closed": 128 + (i % 4) * 6,
            "Open": 142 - 128 + i,
            "role": "open",
            "Role": "open",
            "value": 142 - 128 + i,
            "Value": 142 - 128 + i,
            "prior_value": 142 - 128 + (i - 1 if i > 0 else 0),
            "mtd_value": 142 - 128 + i,
            "avg_value": 142 - 128 + 5
        })
    write("its", "its-ticket-volume-open", wrap("dbo", "vw_its_ticket_volume_open", rows))


def gen_its_kb4_phish_mom():
    """its-kb4-phish-mom.html: month_start + failure_type + failure_count + prior_month_count"""
    month_start = "2026-03-01"
    failures = [
        ("Credential Harvesting", 18, 24),
        ("Malware Link Click", 12, 19),
        ("Attachment Open", 8, 14),
        ("Form Submit", 6, 9),
        ("Voice Phishing", 3, 5)
    ]
    rows = []
    for ft, cnt, prior in failures:
        rows.append({
            "month_start": month_start,
            "MonthStart": month_start,
            "failure_type": ft,
            "FailureType": ft,
            "type": ft,
            "failure_count": cnt,
            "FailureCount": cnt,
            "prior_month_count": prior,
            "PriorMonthCount": prior,
            "mom_variance": cnt - prior,
            "MomVariance": cnt - prior
        })
    write("its", "its-kb4-phish-mom", wrap("dbo", "vw_its_kb4_phish_failures", rows))


def gen_its_open_priority():
    """its-open-priority.html: priority + count + age"""
    priorities = [
        ("P1", "Critical", 4, 2.5),
        ("P2", "High", 18, 4.8),
        ("P3", "Medium", 62, 9.2),
        ("P4", "Low", 142, 18.4)
    ]
    rows = []
    for code, label, count, age in priorities:
        rows.append({
            "Priority": code,
            "priority": code,
            "Label": label,
            "label": label,
            "Count": count,
            "count": count,
            "AvgAge": age,
            "avg_age": age,
            "SLA": 92.0,
            "sla_pct": 92.0
        })
    write("its", "its-open-priority", wrap("dbo", "vw_its_open_priority", rows))


def gen_its_ocsf_report():
    """its-ocsf-report.html: rows with UploadRowCount, Status, RemediatedCount, FailCount, etc."""
    categories = ["Authentication", "Network Activity", "System Activity", "Application", "Findings", "Remediation"]
    rows = []
    for i, cat in enumerate(categories):
        total = [384, 296, 218, 142, 87, 53][i]
        remediated = int(total * 0.65)
        fail = total - remediated
        soft_fail = int(fail * 0.4)
        rows.append({
            "Category": cat,
            "category": cat,
            "UploadRowCount": total,
            "RowCount": total,
            "rowcount": total,
            "Status": "Processed",
            "OEBStatus": "Processed",
            "RemediatedCount": remediated,
            "Remediated": remediated,
            "FailCount": fail,
            "OEBFail": fail,
            "SoftFailCount": soft_fail,
            "OEBSoftFail": soft_fail,
            "RemediationStatus": "In Progress"
        })
    write("its", "its-ocsf-report", wrap("dbo", "vw_its_ocsf_report", rows))


# ════════════════════════════════════════════════════════════════════════════
#  ITS extras (other ITS visuals in the folder)
# ════════════════════════════════════════════════════════════════════════════

def gen_its_ticket_volume_close():
    mnths = months(13)
    rows = []
    for i, m in enumerate(mnths):
        rows.append({
            "period_sort": i + 1,
            "Period Sort": i + 1,
            "month_start": m.strftime("%Y-%m-%d"),
            "Opened": 142 + (i % 4) * 8,
            "Closed": 128 + (i % 4) * 6,
            "role": "closed",
            "value": 128 + (i % 4) * 6,
            "Value": 128 + (i % 4) * 6
        })
    write("its", "its-ticket-volume-close", wrap("dbo", "vw_its_ticket_volume_close", rows))


def gen_its_open_status():
    statuses = ["Open", "In Progress", "Pending", "Resolved", "Closed"]
    counts = [42, 68, 35, 142, 89]
    rows = []
    for i, s in enumerate(statuses):
        rows.append({"Status": s, "status": s, "Count": counts[i], "count": counts[i]})
    write("its", "its-open-status", wrap("dbo", "vw_its_open_status", rows))


def gen_its_priority_status():
    matrix = [
        ("P1", "Open", 2), ("P1", "In Progress", 1), ("P1", "Pending", 1),
        ("P2", "Open", 5), ("P2", "In Progress", 8), ("P2", "Pending", 5),
        ("P3", "Open", 18), ("P3", "In Progress", 22), ("P3", "Pending", 22),
        ("P4", "Open", 42), ("P4", "In Progress", 58), ("P4", "Pending", 42)
    ]
    rows = []
    for p, s, c in matrix:
        rows.append({"Priority": p, "Status": s, "Count": c})
    write("its", "its-priority-status", wrap("dbo", "vw_its_priority_status", rows))


def gen_its_closed_priority():
    rows = [
        {"Priority": "P1", "Count": 8, "AvgAge": 1.8},
        {"Priority": "P2", "Count": 32, "AvgAge": 4.2},
        {"Priority": "P3", "Count": 124, "AvgAge": 8.6},
        {"Priority": "P4", "Count": 284, "AvgAge": 16.8}
    ]
    write("its", "its-closed-priority", wrap("dbo", "vw_its_closed_priority", rows))


def gen_its_response_sla_mom():
    mnths = months(13)
    rows = []
    for i, m in enumerate(mnths):
        rows.append({
            "month_start": m.strftime("%Y-%m-%d"),
            "MonthStart": m.strftime("%Y-%m-%d"),
            "SLA_Met": 92 + (i % 4),
            "SLA_Breached": 8 - (i % 4),
            "Response_Avg_Hours": 2.4 + (i % 3) * 0.3
        })
    write("its", "its-response-sla-mom", wrap("dbo", "vw_its_response_sla_mom", rows))


def gen_its_closure_sla_mom():
    mnths = months(13)
    rows = []
    for i, m in enumerate(mnths):
        rows.append({
            "month_start": m.strftime("%Y-%m-%d"),
            "MonthStart": m.strftime("%Y-%m-%d"),
            "SLA_Met": 88 + (i % 5),
            "SLA_Breached": 12 - (i % 5),
            "Closure_Avg_Hours": 18.2 + (i % 3) * 0.8
        })
    write("its", "its-closure-sla-mom", wrap("dbo", "vw_its_closure_sla_mom", rows))


def gen_its_kb4_training():
    rows = [
        {"Department": "Operations", "Assigned": 142, "Completed": 138, "Rate": 97.2},
        {"Department": "Customer Service", "Assigned": 218, "Completed": 208, "Rate": 95.4},
        {"Department": "Finance", "Assigned": 84, "Completed": 82, "Rate": 97.6},
        {"Department": "IT", "Assigned": 96, "Completed": 95, "Rate": 99.0},
        {"Department": "HR", "Assigned": 32, "Completed": 31, "Rate": 96.9},
        {"Department": "Sales", "Assigned": 124, "Completed": 118, "Rate": 95.2},
        {"Department": "Executive", "Assigned": 28, "Completed": 28, "Rate": 100.0}
    ]
    write("its", "its-kb4-training", wrap("dbo", "vw_its_kb4_training", rows))


def gen_its_kb4_failure_ppp():
    rows = [
        {"FailureType": "Credential Harvesting", "Clicks": 18, "Reports": 24, "PPP": 1.33},
        {"FailureType": "Malware Link Click", "Clicks": 12, "Reports": 18, "PPP": 1.50},
        {"FailureType": "Attachment Open", "Clicks": 8, "Reports": 14, "PPP": 1.75},
        {"FailureType": "Form Submit", "Clicks": 6, "Reports": 9, "PPP": 1.50},
        {"FailureType": "Voice Phishing", "Clicks": 3, "Reports": 5, "PPP": 1.67}
    ]
    write("its", "its-kb4-failure-ppp", wrap("dbo", "vw_its_kb4_failure_ppp", rows))


# ════════════════════════════════════════════════════════════════════════════
print("Generating CSR payloads...")
gen_aging_bankruptcies()
gen_aging_disconnects_reconnects()
gen_ar_buckets_stacked()
gen_aging_electric_commercial_rolling13()
gen_aging_electric_residential_rolling13()
gen_aging_bankruptcies_by_customer_type()
gen_aging_disconnects_by_customer_type()
gen_aging_commercial_delta()
gen_aging_residential_delta()
gen_ar_delta_stacked()
gen_ar_buckets_tabular()
gen_ar_delta_tabular()

print("\nGenerating ITS payloads...")
gen_its_uptime()
gen_its_ticket_volume_open()
gen_its_kb4_phish_mom()
gen_its_open_priority()
gen_its_ocsf_report()
gen_its_ticket_volume_close()
gen_its_open_status()
gen_its_priority_status()
gen_its_closed_priority()
gen_its_response_sla_mom()
gen_its_closure_sla_mom()
gen_its_kb4_training()
gen_its_kb4_failure_ppp()

print(f"\nDone.")
print(f"CSR files in: {CSR_OUT}")
print(f"ITS files in: {ITS_OUT}")
