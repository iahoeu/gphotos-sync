$ErrorActionPreference = "Continue"
$convId = "ea14c032-7e52-43fd-a929-b97d7f583fa9"
$agyDir = Join-Path $env:USERPROFILE ".gemini\antigravity"
$exportDir = Join-Path $PSScriptRoot "_migrace_chatu"

if (-not (Test-Path $exportDir)) {
    Write-Error "Složka $exportDir nebyla nalezena! Nejprve spusťte 1_export_chatu_na_tomto_pc.ps1 na prvním PC."
    exit 1
}

Write-Host "Importuji konverzaci $convId do $agyDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path "$agyDir\brain" -Force | Out-Null
New-Item -ItemType Directory -Path "$agyDir\conversations" -Force | Out-Null

if (Test-Path "$exportDir\brain\$convId") {
    Copy-Item -Path "$exportDir\brain\$convId" -Destination "$agyDir\brain\$convId" -Recurse -Force
}

Get-ChildItem "$exportDir\conversations" -Filter "$convId*" -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination "$agyDir\conversations\" -Force
}

foreach ($f in @("conversation_summaries.db", "conversation_summaries.db-wal", "conversation_summaries.db-shm", "agyhub_summaries_proto.pb")) {
    $src = Join-Path $exportDir $f
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination "$agyDir\" -Force
    }
}

Write-Host "Hotovo! Nyní můžete zapnout Antigravity a otevřít složku c:\Antigravity\photos." -ForegroundColor Green
