# Executive dashboard versions 213-217

This package adds five new dashboard versions to the current `corporate_dashboards` application. It does not replace or edit an existing dashboard version, existing CSR/PBIP visual, existing SQL reporting object, existing upload pipeline, or existing calculation path.

The PowerPoint is used only as a requirements inventory. The new pages use the application's current visual language: compact KPI cards, internal panel titles, shared table/chart classes, existing typography, and related palettes with a small variation by version.

## New versions

| Version | Title | Template key | Page file |
|---:|---|---|---|
| 213 | E-Bill Performance | `executive-ebill-performance` | `executive-ebill-performance.html` |
| 214 | AR Portfolio | `executive-ar-portfolio` | `executive-ar-portfolio.html` |
| 215 | Disconnects, Reconnects and Bankruptcies | `executive-disconnects-bankruptcies` | `executive-disconnects-bankruptcies.html` |
| 216 | Final Bill Collections Recovery - Electric | `executive-final-bill-recovery` | `executive-final-bill-recovery.html` |
| 217 | Customer Payments | `executive-customer-payments` | `executive-customer-payments.html` |

The layout URLs are:

```text
<app-base>/Dashboard/Multi?layoutVersionId=213
<app-base>/Dashboard/Multi?layoutVersionId=214
<app-base>/Dashboard/Multi?layoutVersionId=215
<app-base>/Dashboard/Multi?layoutVersionId=216
<app-base>/Dashboard/Multi?layoutVersionId=217
```

## Reused sources

| Version | Existing source reused |
|---|---|
| E-Bill Performance | `dbo.ns_daily_ebnotes()` and `dbo.ns_total_bills_monthly()` through the existing `csr_monthly-ebnotes` source configuration |
| AR Portfolio | `[its].[Aging History TrueDebt]` and `[its].[EOM Buckets TrueDebt]` through the existing aging visual configuration |
| Disconnects / Bankruptcies | `[its].[Disconnects]` plus `dbo.ns_collection_submission_accounts_bankrupt()` |
| Final Bill Recovery | `dbo.ns_collection_submission_accounts_pbi()` through the existing Collection Report configuration |
| Customer Payments | `dbo.ns_daily_cash_by_cycle_view` through the existing Customer Payments configuration |

The separate upload application was used as a read-only reference. No file in that application is changed by this package.

## Data endpoint

All five HTML pages use the same cache-bypassed JSON endpoint with a version key:

```text
GET <app-base>/Dashboard/GetExecutiveVersionData?version=ebill
GET <app-base>/Dashboard/GetExecutiveVersionData?version=ar
GET <app-base>/Dashboard/GetExecutiveVersionData?version=disconnects
GET <app-base>/Dashboard/GetExecutiveVersionData?version=finalbill
GET <app-base>/Dashboard/GetExecutiveVersionData?version=payments
```

## Export and email endpoints

Each endpoint accepts:

```text
format=xlsx|png
email=true|false
```

When `ExecutiveExports:JobKey` is populated, send the same value in the `X-Job-Key` request header.

| Version | Endpoint |
|---|---|
| E-Bill Performance | `GET <app-base>/Dashboard/ExportEbillPerformance` |
| AR Portfolio | `GET <app-base>/Dashboard/ExportArPortfolio` |
| Disconnects / Bankruptcies | `GET <app-base>/Dashboard/ExportDisconnectsBankruptcies` |
| Final Bill Recovery | `GET <app-base>/Dashboard/ExportFinalBillRecovery` |
| Customer Payments | `GET <app-base>/Dashboard/ExportCustomerPaymentsExecutive` |

Examples:

```text
GET <app-base>/Dashboard/ExportEbillPerformance?format=xlsx&email=true
GET <app-base>/Dashboard/ExportEbillPerformance?format=png&email=false
```

Each Excel export contains:

- a summary sheet with KPI values and comparisons;
- chart data tables;
- a rendered PNG for every graph;
- a separate worksheet for every data table.

Each PNG export contains the full version: KPI cards, all graphs, table previews, and notes.

## Installation

1. Deploy the changed application files.
2. Restore NuGet packages because the application now references `ScottPlot` 4.1.74 for server-side PNG and workbook chart rendering.
3. Run `Scripts/EXECUTIVE_213_217_Insert_DashboardLayoutVersion.sql` against `its_dashboard`.
4. Configure `ExecutiveExports` in `appsettings.json`:
   - `JobKey`
   - `Mail:From`
   - `Mail:To` or a per-version `To`
   - optional `Mail:Cc` or per-version `Cc`
   - SMTP values if the defaults are not correct.
5. Recycle the IIS application pool.
6. Test all five JSON endpoints, then test one XLSX and one PNG endpoint.
7. Create a SQL Agent PowerShell step using `Scripts/SQL_AGENT_ExecutiveDashboardExports.ps1` and update the settings at the top of that script.

## SQL Agent behavior

The supplied PowerShell script:

- calls all five endpoints;
- can download XLSX, PNG, or both;
- can ask each endpoint to email XLSX and/or PNG;
- uses Windows credentials by default;
- sends `X-Job-Key` when configured;
- retries failed requests;
- validates XLSX and PNG file signatures so an HTML error response is not accepted as an export.

## AR source-dependent fields

The AR loader does not invent missing values:

- Water/Wastewater renders when the existing aging source exposes those service rows.
- Total arrears customers uses an existing customer-count field when present, otherwise distinct existing account identifiers.
- Average Bill is `Residential/Commercial arrears / Total arrears customers` and remains blank when no existing count or account identifier is exposed.

This keeps the new version additive and avoids creating a replacement reporting object without verified source data.
