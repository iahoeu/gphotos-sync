$ErrorActionPreference = "Continue"
$convId = "ea14c032-7e52-43fd-a929-b97d7f583fa9"
$agyDir = Join-Path $env:USERPROFILE ".gemini\antigravity"
$exportDir = Join-Path $PSScriptRoot "_migrace_chatu"

Write-Host "Exportuji data konverzace $convId do $exportDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path "$exportDir\brain" -Force | Out-Null
New-Item -ItemType Directory -Path "$exportDir\conversations" -Force | Out-Null

# 1. Brain folder (logs, transcript.jsonl, artifacts)
$brainSrc = Join-Path $agyDir "brain\$convId"
if (Test-Path $brainSrc) {
    Copy-Item -Path $brainSrc -Destination "$exportDir\brain\$convId" -Recurse -Force
    $transcript = Join-Path $brainSrc ".system_generated\logs\transcript.jsonl"
    if (Test-Path $transcript) {
        Copy-Item -Path $transcript -Destination "$exportDir\transcript.jsonl" -Force
    }
}

# 2. Conversation DB files
Get-ChildItem (Join-Path $agyDir "conversations") -Filter "$convId*" -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination "$exportDir\conversations\" -Force
}

# 3. Conversation summaries index
foreach ($f in @("conversation_summaries.db", "conversation_summaries.db-wal", "conversation_summaries.db-shm", "agyhub_summaries_proto.pb")) {
    $p = Join-Path $agyDir $f
    if (Test-Path $p) {
        Copy-Item -Path $p -Destination "$exportDir\" -Force
    }
}

Write-Host "Hotovo! Všechna data chatu jsou uložena ve složce: $exportDir" -ForegroundColor Green
Write-Host "Nyní stačí zkopírovat celou složku c:\Antigravity\photos na druhý počítač." -ForegroundColor Yellow
