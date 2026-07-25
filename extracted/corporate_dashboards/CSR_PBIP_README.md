# CSR dashboard component runtime

This application renders the 21 CSR report pages through one shared component runtime. The former generated page files and hash-named visual wrappers have been removed.

## Direct version launch

The existing version IDs 192 through 212 remain unchanged. Open a version with:

```text
/Dashboard/Multi?currentLayoutId=192
```

The mapping remains available in:

```text
wwwroot/csr/csr-version-map.json
wwwroot/csr/csr-version-map.csv
```

## Configuration and layout

- Existing SQL layout JSON is left untouched and continues to position the outer CSR page tiles.
- `Dashboard:CustomHtml:Templates` in `appsettings.json` defines each CSR page, data source, visual, field roles, filters, formatting, and inner position.
- Every CSR page template uses `csr-page.html`.
- Every standalone CSR visual template uses `csr-visual.html`.
- `DashboardController.GetCsrDefinition` merges the configured page and visual definitions with the existing CSR model aliases and relationships.

## Standard components

`csr-dashboard-runtime.js` and `csr-dashboard-runtime.css` provide reusable classes and shared styling for charts, tables, matrices, KPI cards, slicers, maps, text, and actions. It also supplies independent asynchronous loading, table load-more behavior, visual drag/resize, visual fullscreen, theme switching, compact-chart optimization, multiline axis labels, and common palettes.

## Data connectors

The existing source declarations under `Dashboard:CustomHtml:Templates[*].Sources` and the existing live-data endpoint remain in use. SQL tables, views, and parameterless table-valued functions are supported as before.

## Deployment

Rebuild and publish the application normally. No SQL script is required for this CSR refactor, and existing SQL layout records must not be rewritten.
