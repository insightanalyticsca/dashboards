# CSR PBIP tabs 192-212 — corrected source and semantic implementation

This build reproduces the 21 PBIP report pages as custom HTML/ECharts tabs.

## Source connector

Every SQL-backed semantic table referenced by the supplied PBIP resolves to:

- Server: `app100.camhydro.com`
- Database: `corporate_dashboards`
- Named app connector: `csr_pbip_source`

The CSR source path does not silently fall back to `localhost` or `build`.
The existing non-CSR connectors remain unchanged.

## Corrected semantic behavior

The runtime now reproduces the PBIP logic used by the visuals:

- exact aging bucket membership for `Amount for 31 Plus Days`, `Amount for 90+ Days 2`, and `Amount for 61-90 Days or 90+ Days`;
- measure filters are evaluated after account/category grouping, matching DAX filter context;
- `Paid Ratio = -SUM(Post Paid) / SUM(Balance)`;
- exact metric-name filtering and DAX rounding for `Metrics_Accuracy`, `Metrics_Accuracy_Resid`, and `Metrics_ROC`;
- the calculated `Ebill Account Month` grain and `E-Bill %` denominator logic;
- Mitel renames, long-call flag, and duration conversion;
- aging transaction null replacement and single-space exclusion;
- payment-field renames/null handling/sort order;
- semantic month/year/date hierarchy aliases;
- the `ml_metrics[metrics]` calculated text.

## Source diagnostics

A failed source no longer makes the entire page look like a valid empty result.
The affected visual displays the real SQL connector error and the page shows a
source-failure badge. Successful sources on the same tab continue rendering.

The browser message-channel warning often emitted by extensions is separate from
the report source request. This build does not register an asynchronous extension
message listener or return `true` from one.

## Deployment

1. Publish the full application.
2. Keep the `csr_pbip_source` connection string in `appsettings.json`.
3. Ensure the IIS app-pool identity can read the listed objects and invoke the four table-valued functions.
4. Run `Scripts/CSR_PBIP_192_212_Insert_DashboardLayoutVersion.sql` in `its_dashboard`.
5. Open `/Dashboard/Multi?currentLayoutId=192`.

The source mapping is in `CSR_PBIP_SOURCE_MAP_FIXED.csv`.
Run `Scripts/CSR_PBIP_Source_Validation.sql` against `corporate_dashboards` to verify object access from the SQL identity used for deployment.
