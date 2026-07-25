param(
    [Parameter(Mandatory = $true)]
    [string]$AppRoot
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($AppRoot)
$customHtml = Join-Path $root 'wwwroot\custom-html'
$appsettings = Join-Path $root 'appsettings.json'

if (-not (Test-Path $appsettings)) {
    throw "appsettings.json was not found under: $root"
}
if (-not (Test-Path $customHtml)) {
    throw "wwwroot\custom-html was not found under: $root"
}

$keep = @('csr-page.html', 'csr-visual.html')
$removed = New-Object System.Collections.Generic.List[string]

Get-ChildItem $customHtml -File -Filter '*.html' | ForEach-Object {
    $name = $_.Name
    $isLegacyPage = $name.StartsWith('csr-', [System.StringComparison]::OrdinalIgnoreCase) -and ($keep -notcontains $name)
    $isLegacyVisual = $name -match '^v\d+-[0-9a-f]+\.html$'
    if ($isLegacyPage -or $isLegacyVisual) {
        Remove-Item $_.FullName -Force
        $removed.Add($name)
    }
}

Write-Host "Removed $($removed.Count) legacy CSR HTML file(s)."
$removed | Sort-Object | ForEach-Object { Write-Host "  $_" }
Write-Host 'Kept csr-page.html and csr-visual.html.'
