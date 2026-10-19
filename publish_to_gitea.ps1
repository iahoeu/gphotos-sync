param(
    [Parameter(Mandatory = $true)]
    [string]$Version,          # např. "1.1.0"

    [Parameter(Mandatory = $true)]
    [string]$GiteaUrl,         # např. "https://git.mojedomena.cz"

    [Parameter(Mandatory = $true)]
    [string]$Owner,            # např. "jahoda"

    [Parameter(Mandatory = $false)]
    [string]$Repo = "gphotos-sync",

    [Parameter(Mandatory = $true)]
    [string]$ApiToken,         # Gitea Personal Access Token (Settings -> Applications)

    [Parameter(Mandatory = $false)]
    [string]$ReleaseNotes = "Automatická aktualizace aplikace Google Photos Sync."
)

$ErrorActionPreference = "Stop"
$cleanVer = $Version.TrimStart('v', 'V')
$tagName = "v$cleanVer"

Write-Host "=== 1. Nastavuji verzi $cleanVer v projektech ===" -ForegroundColor Cyan
$csprojPaths = @(
    Join-Path $PSScriptRoot "src\GPhotosSync\GPhotosSync.csproj"
    Join-Path $PSScriptRoot "src\GPhotosSync.Installer\GPhotosSync.Installer.csproj"
)

foreach ($p in $csprojPaths) {
    $xml = Get-Content $p -Raw
    $xml = $xml -replace '<Version>[^<]+</Version>', "<Version>$cleanVer</Version>"
    Set-Content -Path $p -Value $xml -Encoding UTF8
}

Write-Host "=== 2. Kompiluji aplikaci a instalátor GPhotosSync_Setup.exe ===" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "build_installer.ps1")

$setupExe = Join-Path $PSScriptRoot "GPhotosSync_Setup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Soubor $setupExe nebyl nalezen!"
}

$baseUrl = $GiteaUrl.TrimEnd('/')
$headers = @{
    "Authorization" = "token $ApiToken"
    "Accept"        = "application/json"
}

Write-Host "=== 3. Vytvářím Release $tagName na Gitea serveru ($baseUrl/$Owner/$Repo) ===" -ForegroundColor Cyan
$releaseBody = @{
    tag_name   = $tagName
    name       = "Google Photos Sync $tagName"
    body       = $ReleaseNotes
    draft      = $false
    prerelease = $false
} | ConvertTo-Json -Depth 5

$createUrl = "$baseUrl/api/v1/repos/$Owner/$Repo/releases"
$releaseResp = Invoke-RestMethod -Uri $createUrl -Method Post -Headers $headers -Body $releaseBody -ContentType "application/json; charset=utf-8"
$releaseId = $releaseResp.id

Write-Host "=== 4. Nahrávám GPhotosSync_Setup.exe k Release ID $releaseId ===" -ForegroundColor Cyan
$uploadUrl = "$baseUrl/api/v1/repos/$Owner/$Repo/releases/$releaseId/assets?name=GPhotosSync_Setup.exe"

$form = @{
    attachment = Get-Item -Path $setupExe
}
Invoke-RestMethod -Uri $uploadUrl -Method Post -Headers $headers -Form $form | Out-Null

Write-Host "Hotovo! Verze $tagName byla úspěšně publikována na Gitea: $baseUrl/$Owner/$Repo/releases/tag/$tagName" -ForegroundColor Green
