<#
  Đóng gói Scale Scan Lite để cài lên máy trạm.
  Mặc định: self-contained (không cần cài .NET trên máy đích), chạy test trước, kiểm tra file bắt buộc rồi nén zip.

  Dùng:   .\publish.ps1
          .\publish.ps1 -FrameworkDependent   # nhỏ hơn, nhưng máy đích phải cài .NET 8 Desktop Runtime (x64)
          .\publish.ps1 -SkipTests
#>
param(
    [switch]$FrameworkDependent,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "artifacts\publish"
$zip = Join-Path $PSScriptRoot "artifacts\ScaleScanLite-win-x64.zip"

if (-not $SkipTests) {
    Write-Host "== Chạy test ==" -ForegroundColor Cyan
    dotnet test tests/ScaleScanLite.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Test thất bại - dừng đóng gói." }
}

Write-Host "== Publish ==" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
dotnet publish src/ScaleScanLite -c Release -r win-x64 --self-contained $selfContained -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish thất bại." }

Write-Host "== Kiểm tra file bắt buộc ==" -ForegroundColor Cyan
$required = @(
    "ScaleScanLite.exe",
    "ScaleScanLite.Core.dll",
    "Microsoft.Data.SqlClient.dll"
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $out $_)) }
if ($missing) { throw "Thiếu file trong bản publish: $($missing -join ', ')" }
$required | ForEach-Object { Write-Host "  OK  $_" }

Write-Host "== Nén zip ==" -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host ("Xong: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
Write-Host "Giải nén lên máy đích và chạy ScaleScanLite.exe."