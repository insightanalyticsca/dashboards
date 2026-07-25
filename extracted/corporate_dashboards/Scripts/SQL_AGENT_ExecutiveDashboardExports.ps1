# SQL Agent / Windows PowerShell 5.1 script.
# Change only the settings in this section.

$BaseUrl = 'https://app100/corporate_dashboards'
$OutputRoot = 'F:\logs\executive_dashboard_exports'
$JobKey = ''
$EmailExcel = $true
$EmailPng = $false
$DownloadExcel = $true
$DownloadPng = $true
$UseDefaultCredentials = $true
$IgnoreCertificateErrors = $false
$RequestTimeoutSeconds = 900
$MaxAttempts = 2

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($IgnoreCertificateErrors) {
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
}

[System.Net.ServicePointManager]::SecurityProtocol =
    [System.Net.SecurityProtocolType]::Tls12

$exports = @(
    [pscustomobject]@{
        Key = 'ebill'
        Endpoint = 'Dashboard/ExportEbillPerformance'
        FileStem = 'Ebill_Performance'
    },
    [pscustomobject]@{
        Key = 'ar'
        Endpoint = 'Dashboard/ExportArPortfolio'
        FileStem = 'AR_Portfolio'
    },
    [pscustomobject]@{
        Key = 'disconnects'
        Endpoint = 'Dashboard/ExportDisconnectsBankruptcies'
        FileStem = 'Disconnects_Bankruptcies'
    },
    [pscustomobject]@{
        Key = 'finalbill'
        Endpoint = 'Dashboard/ExportFinalBillRecovery'
        FileStem = 'Final_Bill_Collections_Recovery'
    },
    [pscustomobject]@{
        Key = 'payments'
        Endpoint = 'Dashboard/ExportCustomerPaymentsExecutive'
        FileStem = 'Customer_Payments'
    }
)

function Invoke-ExecutiveExport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [string]$OutFile,

        [Parameter(Mandatory = $true)]
        [ValidateSet('xlsx', 'png')]
        [string]$ExpectedFormat
    )

    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($JobKey)) {
        $headers['X-Job-Key'] = $JobKey
    }

    $invokeParams = @{
        Uri = $Uri
        Method = 'GET'
        Headers = $headers
        OutFile = $OutFile
        TimeoutSec = $RequestTimeoutSeconds
        UseBasicParsing = $true
    }

    if ($UseDefaultCredentials) {
        $invokeParams['UseDefaultCredentials'] = $true
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Write-Host "[$(Get-Date -Format s)] GET $Uri (attempt $attempt of $MaxAttempts)"
            Invoke-WebRequest @invokeParams | Out-Null

            if (-not (Test-Path -LiteralPath $OutFile)) {
                throw "Endpoint returned without creating '$OutFile'."
            }

            $length = (Get-Item -LiteralPath $OutFile).Length
            if ($length -le 0) {
                throw "Endpoint created an empty file: '$OutFile'."
            }

            $bytes = [System.IO.File]::ReadAllBytes($OutFile)
            if ($ExpectedFormat -eq 'xlsx') {
                if ($bytes.Length -lt 4 -or $bytes[0] -ne 0x50 -or $bytes[1] -ne 0x4B) {
                    throw "The endpoint response is not an XLSX/ZIP file: '$OutFile'."
                }
            }
            elseif ($ExpectedFormat -eq 'png') {
                $pngHeader = @(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
                if ($bytes.Length -lt $pngHeader.Count) {
                    throw "The endpoint response is too short to be a PNG: '$OutFile'."
                }
                for ($i = 0; $i -lt $pngHeader.Count; $i++) {
                    if ($bytes[$i] -ne $pngHeader[$i]) {
                        throw "The endpoint response is not a PNG file: '$OutFile'."
                    }
                }
            }

            Write-Host "[$(Get-Date -Format s)] Saved $OutFile ($length bytes)"
            return
        }
        catch {
            $lastError = $_
            if (Test-Path -LiteralPath $OutFile) {
                Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            }

            if ($attempt -lt $MaxAttempts) {
                Start-Sleep -Seconds 10
            }
        }
    }

    throw "Export failed after $MaxAttempts attempt(s): $Uri. $($lastError.Exception.Message)"
}

$runStamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runFolder = Join-Path $OutputRoot $runStamp
New-Item -ItemType Directory -Path $runFolder -Force | Out-Null

$failures = New-Object System.Collections.Generic.List[string]

foreach ($export in $exports) {
    try {
        if ($DownloadExcel) {
            $xlsxPath = Join-Path $runFolder ($export.FileStem + '.xlsx')
            $emailFlag = if ($EmailExcel) { 'true' } else { 'false' }
            $xlsxUri = '{0}/{1}?format=xlsx&email={2}' -f $BaseUrl.TrimEnd('/'), $export.Endpoint, $emailFlag
            Invoke-ExecutiveExport -Uri $xlsxUri -OutFile $xlsxPath -ExpectedFormat xlsx
        }
        elseif ($EmailExcel) {
            # The endpoint requires an output file even when SQL Agent only needs email delivery.
            $temporaryXlsx = Join-Path $runFolder ($export.FileStem + '_emailed.xlsx')
            $xlsxUri = '{0}/{1}?format=xlsx&email=true' -f $BaseUrl.TrimEnd('/'), $export.Endpoint
            Invoke-ExecutiveExport -Uri $xlsxUri -OutFile $temporaryXlsx -ExpectedFormat xlsx
        }

        if ($DownloadPng) {
            $pngPath = Join-Path $runFolder ($export.FileStem + '.png')
            $pngEmailFlag = if ($EmailPng) { 'true' } else { 'false' }
            $pngUri = '{0}/{1}?format=png&email={2}' -f $BaseUrl.TrimEnd('/'), $export.Endpoint, $pngEmailFlag
            Invoke-ExecutiveExport -Uri $pngUri -OutFile $pngPath -ExpectedFormat png
        }
    }
    catch {
        $message = '{0}: {1}' -f $export.Key, $_.Exception.Message
        $failures.Add($message)
        Write-Error $message
    }
}

if ($failures.Count -gt 0) {
    throw "One or more executive exports failed:`r`n$($failures -join "`r`n")"
}

Write-Host "[$(Get-Date -Format s)] All five executive dashboard exports completed successfully."
Write-Host "Output folder: $runFolder"
