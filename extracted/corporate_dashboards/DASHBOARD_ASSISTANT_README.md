# Dashboard Assistant — Current Build

Build: `assistant-v8-direct-v217-contract`

Open `corporate_dashboards.sln` from this folder.

The Visual Studio Project profile uses `https://localhost:49582` and opens Version 217 directly. These ports are intentionally different from prior packages so an older clone cannot answer the browser request.

## Version 217 contract

Version 217 bypasses generic dataset matching before any raw SQL metadata is read.

Facts:
- Payment Value
- Transactions

Dimensions:
- Period
- Payment Type

The request:

`how much was paid by credit card since april 2026`

is forced to:
- measure: `payment_value`
- filter: `payment_type = Credit Card`
- period: April 1, 2026 inclusive through the current-month start exclusive
- aggregation: `Sum`
- dimensions: none
- output: metric plus narrative; no graph

The assistant header must show build `assistant-v8-direct-v217-contract`. A client/server build mismatch is blocked with an explicit error.
