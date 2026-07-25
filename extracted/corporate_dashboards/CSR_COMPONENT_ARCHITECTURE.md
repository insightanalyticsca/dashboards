# CSR component architecture

This rebuild replaces the generated CSR page and visual HTML files with two stable shells and a shared component runtime. It is CSR-only: CX, aging, ITS and every other custom-HTML path remain unchanged.

## Stable runtime files

- `wwwroot/custom-html/csr-page.html` — shell used by all 21 CSR page templates.
- `wwwroot/custom-html/csr-visual.html` — shell used by all 88 standalone CSR visual templates.
- `wwwroot/js/csr-dashboard-runtime.js` — data/model compatibility layer, component registry and all CSR renderers.
- `wwwroot/css/csr-dashboard-runtime.css` — the original CSR visual treatment, externalized from the generated runtime.
- `Controllers/DashboardController.CsrDefinitions.cs` — returns page or visual definitions from the existing appsettings configuration and existing manifest metadata.

## Sources of truth

- Existing SQL layout JSON is unchanged and continues to place dashboard-constructor tiles.
- `appsettings.json` continues to control CSR data sources, page composition, visual type, field roles, filters, sort, top-N, options and default inner geometry.
- `wwwroot/csr/csr-pages.manifest.json` continues to supply aliases, relationships, page dimensions and the original palette order.
- Existing SQL views, functions and live-data endpoints are unchanged.

The only CSR changes in `appsettings.json` are:

- every `csr-page` template uses `csr-page.html`;
- every `csr-visual` template uses `csr-visual.html`.

`tools/csr-template-baseline.json` contains hashes of all 109 original CSR template definitions with `HtmlFile` excluded. The validator fails if a layout, role, filter, style option, data source or other CSR definition changes.

## Actual CSR inventory covered

| Component | Existing CSR count |
|---|---:|
| Slicer | 28 |
| Table | 13 |
| Column chart | 11 |
| Text box | 8 |
| Matrix/pivot | 7 |
| Stacked-column/line combo | 7 |
| Line chart | 6 |
| Multi-row card | 3 |
| Map | 2 |
| Card | 1 |
| Horizontal bar chart | 1 |
| Action button | 1 |

Pie, donut, stacked-column and stacked-horizontal-bar classes are also registered for future CSR definitions without requiring a new HTML file.

## Dedicated visual classes

The runtime has distinct classes for:

- `CsrColumnChartComponent`
- `CsrStackedColumnChartComponent`
- `CsrHorizontalBarChartComponent`
- `CsrStackedHorizontalBarChartComponent`
- `CsrLineChartComponent`
- `CsrStackedColumnLineComboComponent`
- `CsrPieComponent`
- `CsrDonutComponent`
- `CsrTableComponent`
- `CsrMatrixComponent`
- `CsrKpiComponent`
- `CsrSlicerComponent`
- `CsrMapComponent`
- `CsrTextComponent`
- `CsrActionComponent`

For `lineStackedColumnComboChart`, every primary `Y` series is stacked on axis 0 and every `Y2` series is rendered as a line on axis 1. This covers both combo shapes used in the existing CSR versions:

- one `Y` field split by a `Series` role;
- multiple `Y` fields with no `Series` role, including the queue-spectrum visuals.

## Chart behavior

The shared chart layer applies the same behavior to every CSR version:

- category labels always use `rotate: 0`;
- long labels are split at spaces into multiple lines;
- bar axes always include zero;
- line axes scale to their actual range;
- stacked-axis limits use the stack totals rather than individual segments;
- small tiles hide single-series legends, retain a usable plot area and apply minimum visible bar height;
- chart sizing is measured from the actual chart host after it is attached to the DOM;
- `xTitle`, `yTitle` and `y2Title` options are honored;
- charts resize after render and through `ResizeObserver`;
- multi-series palettes retain the original series order, with ordinary red/pink series slots replaced by vivid light blues as requested;
- semantic error/bad-state red remains unchanged.

## Style compatibility

`csr-dashboard-runtime.css` was extracted from the original CSR runtime. Its only intentional style differences are:

- ordinary red/pink accent slots are vivid blue/cyan;
- the theme circle and icon are smaller;
- the visual-type selector typo is corrected so table, matrix and KPI titles use their original full width.

All existing visual positions and dimensions remain in the original appsettings definitions, while SQL-saved visual-layout overrides continue to take precedence at runtime.
