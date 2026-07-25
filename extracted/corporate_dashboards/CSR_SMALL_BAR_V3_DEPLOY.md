# CSR component runtime v3 - compact bar correction

No SQL or appsettings changes are included.

Changed files:

- `Controllers/DashboardController.CsrDefinitions.cs`
- `wwwroot/js/csr-dashboard-runtime.js`
- `wwwroot/custom-html/csr-page.html`
- `wwwroot/custom-html/csr-visual.html`

The correction applies to the shared CSR column/bar classes. It:

- keeps the category axis at the physical bottom/left when values are negative (`axisLine.onZero = false`);
- uses literal multiline category labels with `rotate = 0`;
- suppresses axis titles in compact tiles to recover plot area;
- assigns an actual `barWidth`, not only `barMaxWidth`;
- widens bars and reduces category gaps for charts with few categories;
- uses solid, fully opaque bars in compact tiles;
- increases the minimum visible height of non-zero bars;
- bumps the shared runtime cache key to `executive-dashboard.css?v=20260724-polish-3`.

Build and publish the application normally. Publish to a clean folder, then recycle the app pool.

Browser verification:

```javascript
window.__csrDashboardRuntimeVersion
```

Expected:

```text
executive-dashboard.css?v=20260724-polish-3
```

For visual `4f0c9a7b2e6d8c153a91`:

```javascript
const o = window.__csrLastChartOptions?.['4f0c9a7b2e6d8c153a91'];
({
  rotate: o?.xAxis?.axisLabel?.rotate,
  onZero: o?.xAxis?.axisLine?.onZero,
  barWidth: o?.series?.[0]?.barWidth,
  barMinHeight: o?.series?.[0]?.barMinHeight,
  labels: o?.xAxis?.data
})
```

Expected: `rotate: 0`, `onZero: false`, a numeric `barWidth`, and labels containing line breaks for multiword categories.
