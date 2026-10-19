param(
    [Parameter(Mandatory = $true)]
    [string]$Version,          # např. "1.0.0" nebo "1.1.0"

    [Parameter(Mandatory = $false)]
    [string]$GiteaUrl = "https://git.iaho.eu",

    [Parameter(Mandatory = $false)]
    [string]$Owner = "iaho",

    [Parameter(Mandatory = $false)]
    [string]$Repo = "gphotos-sync",

    [Parameter(Mandatory = $false)]
    [string]$ApiToken = "",    # Pokud není zadán, načte se z .gitea_token nebo vygeneruje přes SSH docker

    [Parameter(Mandatory = $false)]
    [string]$ReleaseNotes = "Automatická aktualizace aplikace Google Photos Sync."
)

$ErrorActionPreference = "Stop"
$cleanVer = $Version.TrimStart('v', 'V')
$tagName = "v$cleanVer"
$tokenFile = Join-Path $PSScriptRoot ".gitea_token"

if ([string]::IsNullOrWhiteSpace($ApiToken)) {
    if (Test-Path $tokenFile) {
        $ApiToken = (Get-Content $tokenFile -Raw).Trim()
    } else {
        Write-Host "Generuji Gitea API token přes SSH (docker)..." -ForegroundColor Yellow
        $tokenName = "gphotos-sync-publisher-$(Get-Date -Format 'yyyyMMddHHmmss')"
        $out = ssh docker "docker exec -u git gitea gitea admin user generate-access-token --username $Owner --token-name $tokenName --scopes write:repository,write:user --raw" 2>$null
        $ApiToken = ($out | Select-Object -Last 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($ApiToken)) {
            Set-Content -Path $tokenFile -Value $ApiToken -NoNewline
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ApiToken)) {
    throw "Chybí Gitea API token! Zadejte parametr -ApiToken nebo uložte token do .gitea_token"
}

Write-Host "=== 1. Nastavuji verzi $cleanVer v projektech ===" -ForegroundColor Cyan
$csprojPaths = @(
    Join-Path $PSScriptRoot "src\GPhotosSync\GPhotosSync.csproj"
    Join-Path $PSScriptRoot "src\GPhotosSync.Installer\GPhotosSync.Installer.csproj"
)

foreach ($p in $csprojPaths) {
    $xml = Get-Content $p -Raw
    $xml = $xml -replace '<Version>[^<]+</Version>', "<Version>$cleanVer</Version>"
    Set-Content -Path $p -Value $xml -Encoding UTF8 -NoNewline
}

Write-Host "=== 2. Kompiluji aplikaci a instalátor GPhotosSync_Setup.exe ===" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "build_installer.ps1")

$setupExe = Join-Path $PSScriptRoot "GPhotosSync_Setup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Soubor $setupExe nebyl nalezen!"
}

Write-Host "=== 3. Odesílám změny a tag $tagName do Gitea přes SSH ===" -ForegroundColor Cyan
git -C $PSScriptRoot add .
$status = git -C $PSScriptRoot status --porcelain
if ($status) {
    git -C $PSScriptRoot commit -m "Release ${tagName}: $ReleaseNotes"
}
git -C $PSScriptRoot tag -f $tagName
git -C $PSScriptRoot push origin main
git -C $PSScriptRoot push origin $tagName -f

$baseUrl = $GiteaUrl.TrimEnd('/')
$headers = @{
    "Authorization" = "token $ApiToken"
    "Accept"        = "application/json"
}

Write-Host "=== 4. Vytvářím Release $tagName na Gitea serveru ($baseUrl/$Owner/$Repo) ===" -ForegroundColor Cyan

# Pokud release s tímto tagem již existuje, smažeme ho, abychom ho mohli čistě nahradit
try {
    $existing = Invoke-RestMethod -Uri "$baseUrl/api/v1/repos/$Owner/$Repo/releases/tags/$tagName" -Method Get -Headers $headers -ErrorAction SilentlyContinue
    if ($existing -and $existing.id) {
        Invoke-RestMethod -Uri "$baseUrl/api/v1/repos/$Owner/$Repo/releases/$($existing.id)" -Method Delete -Headers $headers | Out-Null
    }
} catch {}

$releaseBody = @{
    tag_name   = $tagName
    target_commitish = "main"
    name       = "Google Photos Sync $tagName"
    body       = $ReleaseNotes
    draft      = $false
    prerelease = $false
} | ConvertTo-Json -Depth 5

$createUrl = "$baseUrl/api/v1/repos/$Owner/$Repo/releases"
$releaseResp = Invoke-RestMethod -Uri $createUrl -Method Post -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($releaseBody)) -ContentType "application/json; charset=utf-8"
$releaseId = $releaseResp.id

Write-Host "=== 5. Nahrávám GPhotosSync_Setup.exe k Release ID $releaseId ===" -ForegroundColor Cyan
$uploadUrl = "$baseUrl/api/v1/repos/$Owner/$Repo/releases/$releaseId/assets?name=GPhotosSync_Setup.exe"

$form = @{
    attachment = Get-Item -Path $setupExe
}
Invoke-RestMethod -Uri $uploadUrl -Method Post -Headers $headers -Form $form | Out-Null

Write-Host "Hotovo! Verze $tagName byla úspěšně publikována na Gitea: $baseUrl/$Owner/$Repo/releases/tag/$tagName" -ForegroundColor Green
