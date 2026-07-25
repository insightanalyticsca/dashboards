# CSR PBIP exact-layout progressive build

- Versions 192-212 retain the PBIP visualContainer x/y/width/height/z geometry.
- All 28 PBIP slicers are present at their original positions.
- Slicers support search, All, multi-select, and single-select where required by PBIP.
- The iframe and every visual shell paint immediately. Each configured SQL source loads through its own request.
- Tables and chart headers use compact typography.
- Every ECharts value axis uses its current data extent with headroom; no shared fixed Y maximum is imposed.
- Chart grid margins are minimized to maximize vertical plot area.
- Light/dark themes remain available on every version.

Run `Scripts/CSR_PBIP_192_212_Insert_DashboardLayoutVersion_EXACT_ASYNC.sql` after deployment.
