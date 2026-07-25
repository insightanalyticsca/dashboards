# CSR PBIP versions 192-212 with light/dark themes

## Theme behavior

- The floating moon/sun button is present on `Dashboard/Multi` for every version.
- Theme selection is stored in browser local storage under `its-dashboard-csr-theme`.
- Switching versions 192-212 preserves the selected theme.
- Every CSR iframe receives the current theme after load and whenever it changes.
- ECharts axes, legends, grids, tooltips, series palette, tables, cards, slicers, maps, and page surfaces are all theme-aware.
- Standalone CSR HTML files display their own toggle when opened outside the app.

## Version deployment

Run:

`Scripts/CSR_PBIP_192_212_Insert_DashboardLayoutVersion.sql`

The script targets the existing table only and inserts/updates IDs 192-212 with:

- `UserName = __csr_pbip__`
- `Page = Multi`
- one full-tab custom HTML layout per PBIP page

Direct example:

`/Dashboard/Multi?currentLayoutId=192`

The SQL refuses to overwrite an ID owned by another user/page.
