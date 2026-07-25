# CSR component rebuild deployment

No SQL script is included or required. Existing SQL layout JSON must remain unchanged.

## Source deployment

1. Back up the repository application folder.
2. Replace the files from the changed-files package, preserving their paths.
3. Run the cleanup script against the repository application folder containing `appsettings.json`, `Controllers`, `Views` and `wwwroot`:

```powershell
powershell -ExecutionPolicy Bypass `
  -File .\tools\CLEAN_LEGACY_CSR_HTML.ps1 `
  -AppRoot "C:\path\to\repo\corporate_dashboards"
```

4. Validate before building:

```powershell
python .\tools\validate_csr_component_architecture.py
node --check .\wwwroot\js\csr-dashboard-runtime.js
node .\tools\validate_csr_runtime.js
```

5. Delete stale `bin` and `obj` folders.
6. Rebuild the .NET 8 application.
7. Publish to a clean output directory rather than overlaying a prior publish.
8. Deploy the clean output and recycle the main dashboard application pool.

## Runtime verification

Inside a CSR frame:

```javascript
window.__csrDashboardRuntimeVersion
```

Expected value:

```text
20260721-csr-components-v2
```

Page frames must use:

```text
/custom-html/csr-page.html?templateId=...
```

Standalone visual frames must use:

```text
/custom-html/csr-visual.html?templateId=...
```

`HtmlFile` values in appsettings must remain plain filenames. Do not append query strings to them.

## Version 192 bar verification

Inside the version 192 page frame:

```javascript
window.__csrLastChartOptions["4f0c9a7b2e6d8c153a91"].xAxis.axisLabel.rotate
```

Expected:

```text
0
```

The x-axis data for that visual should contain literal line breaks for long labels, for example:

```text
Small\nCommercial
Large\nCommercial
```

## Scope

This deployment changes only the CSR dashboard-rendering path. It does not update CX renderers, non-CSR templates, SQL objects or stored layout JSON.
