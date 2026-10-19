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
        $out = ssh docker "docker exec -u git gitea gitea admin user generate-access-token --username $Owner --token-name $tokenName --scopes write:repository --raw" 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "Generování Gitea API tokenu přes SSH selhalo (exit code: $LASTEXITCODE)."
        }
        $ApiToken = ($out | Select-Object -Last 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($ApiToken)) {
            Set-Content -Path $tokenFile -Value $ApiToken -NoNewline
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ApiToken)) {
    throw "Chybí Gitea API token! Zadejte parametr -ApiToken nebo uložte token do .gitea_token"
}

$existingLocalTag = git -C $PSScriptRoot tag -l $tagName
if ($LASTEXITCODE -ne 0) {
    throw "Kontrola git tagů selhala (exit code: $LASTEXITCODE)."
}
if (-not [string]::IsNullOrWhiteSpace($existingLocalTag)) {
    throw "Git tag '$tagName' již v repozitáři existuje! Zadejte nové číslo verze."
}

$baseUrl = $GiteaUrl.TrimEnd('/')
$headers = @{
    "Authorization" = "token $ApiToken"
    "Accept"        = "application/json"
}

try {
    $existingRemoteRelease = Invoke-RestMethod -Uri "$baseUrl/api/v1/repos/$Owner/$Repo/releases/tags/$tagName" -Method Get -Headers $headers -ErrorAction SilentlyContinue
    if ($existingRemoteRelease -and $existingRemoteRelease.id) {
        throw "Release s tagem '$tagName' již na Gitea serveru existuje! Zadejte nové číslo verze."
    }
} catch {
    if ($_.Exception.Message -like "*již na Gitea serveru existuje*") {
        throw
    }
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
if ($LASTEXITCODE -ne 0) {
    throw "Skript build_installer.ps1 skončil s chybou (exit code: $LASTEXITCODE)."
}

$setupExe = Join-Path $PSScriptRoot "GPhotosSync_Setup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Soubor $setupExe nebyl nalezen!"
}

Write-Host "=== 3. Odesílám změny a tag $tagName do Gitea přes SSH ===" -ForegroundColor Cyan
git -C $PSScriptRoot add -- src/ build_installer.ps1 publish_to_gitea.ps1 README.md .gitignore
if ($LASTEXITCODE -ne 0) { throw "Příkaz 'git add' selhal (exit code: $LASTEXITCODE)." }

if (Test-Path (Join-Path $PSScriptRoot "tests")) {
    git -C $PSScriptRoot add -- tests/
    if ($LASTEXITCODE -ne 0) { throw "Příkaz 'git add tests/' selhal (exit code: $LASTEXITCODE)." }
}
$status = git -C $PSScriptRoot status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0) { throw "Příkaz 'git status' selhal (exit code: $LASTEXITCODE)." }

if ($status) {
    git -C $PSScriptRoot commit -m "Release ${tagName}: $ReleaseNotes"
    if ($LASTEXITCODE -ne 0) { throw "Příkaz 'git commit' selhal (exit code: $LASTEXITCODE)." }
}
git -C $PSScriptRoot tag $tagName
if ($LASTEXITCODE -ne 0) { throw "Příkaz 'git tag $tagName' selhal (exit code: $LASTEXITCODE)." }

git -C $PSScriptRoot push origin main
if ($LASTEXITCODE -ne 0) { throw "Příkaz 'git push origin main' selhal (exit code: $LASTEXITCODE)." }

git -C $PSScriptRoot push origin $tagName
if ($LASTEXITCODE -ne 0) { throw "Příkaz 'git push origin $tagName' selhal (exit code: $LASTEXITCODE)." }

Write-Host "=== 4. Vytvářím koncept (draft) Release $tagName na Gitea serveru ($baseUrl/$Owner/$Repo) ===" -ForegroundColor Cyan

$releaseBody = @{
    tag_name         = $tagName
    target_commitish = "main"
    name             = "Google Photos Sync $tagName"
    body             = $ReleaseNotes
    draft            = $true
    prerelease       = $false
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

Write-Host "=== 6. Publikuji finální Release $tagName ===" -ForegroundColor Cyan
$publishBody = @{
    draft = $false
} | ConvertTo-Json -Depth 3
$patchUrl = "$baseUrl/api/v1/repos/$Owner/$Repo/releases/$releaseId"
Invoke-RestMethod -Uri $patchUrl -Method Patch -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($publishBody)) -ContentType "application/json; charset=utf-8" | Out-Null

Write-Host "Hotovo! Verze $tagName byla úspěšně publikována na Gitea: $baseUrl/$Owner/$Repo/releases/tag/$tagName" -ForegroundColor Green
