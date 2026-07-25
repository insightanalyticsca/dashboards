# Source-to-visual inventory

## 213 - E-Bill Performance

- Current total E-Bill customers, MoM, YoY: derived from existing monthly E-Bill notes.
- Current new E-Bill customers, MoM, YoY: `IsFirstEBill` in the existing notes function.
- Total and new E-Bill percentages: existing monthly bill count denominator.
- Rolling 13-month total/new trends: same monthly source.
- YTD table: previous-year full year, previous-year YTD, current-year YTD, and comparison.

## 214 - AR Portfolio

- Electric Residential and Commercial rolling 13-month aging: existing True Debt source.
- Total Electric AR rolling table: existing aging buckets.
- Residential and Commercial EOM delta tables: existing EOM True Debt source.
- Water/Wastewater: uses existing service rows only when available.
- Customer count and average bill: existing count or account fields only.

## 215 - Disconnects, Reconnects and Bankruptcies

- Disconnect/Reconnect matrix: existing Disconnects source, preserving all source metrics, including a previous-year column when exposed.
- Disconnect YTD pie: existing residential/commercial YTD rows when exposed.
- Bankruptcies rolling 13 months: existing bankruptcy function.
- Current-year latest snapshot and current Residential/Commercial pie: existing latest `date_in`.
- Previous-year total: all previous-year existing rows.

## 216 - Final Bill Collections Recovery - Electric

- Current-year latest `DateIn` by customer type.
- Accounts, balance, post-paid amount, and paid ratio.
- Previous-year all-date total.
- Current snapshot customer-type pie.
- Uses the same existing source as Collection Report; no upload or source object is changed.

## 217 - Customer Payments

- Rolling 13-month payment value by existing payment description.
- Transactions as the secondary line.
- Current month, previous month, same month previous year.
- MoM and YoY comparisons for value and transactions.
