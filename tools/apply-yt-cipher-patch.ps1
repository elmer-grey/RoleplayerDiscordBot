# apply-yt-cipher-patch.ps1
#
# Применяет локальный патч к submodule yt-cipher, чтобы YouTube не блокировал
# скачивание player.js через Cloudflare (исправляет "All clients failed to load the item").
#
# Запускать из корня репо после:
#   git submodule update --init --recursive
#
# Безопасно запускать повторно — если патч уже применён, скрипт завершится
# с соответствующим сообщением и не сделает дубль.

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path "$ScriptDir\.."
$SubmodulePath = Join-Path $RepoRoot "yt-cipher"
$PatchFile = Join-Path $ScriptDir "patches\yt-cipher-cloudflare-ua.patch"
$MarkerFile = ".yt-cipher-patch-applied"
$TargetFile = "src\playerCache.ts"

Write-Host "[apply-yt-cipher-patch] Repo root: $RepoRoot"
Write-Host "[apply-yt-cipher-patch] Submodule:  $SubmodulePath"
Write-Host "[apply-yt-cipher-patch] Patch file: $PatchFile"

if (-not (Test-Path $PatchFile)) {
    Write-Error "Файл патча не найден: $PatchFile"
    exit 1
}

if (-not (Test-Path $SubmodulePath)) {
    Write-Error "Submodule yt-cipher не найден: $SubmodulePath. Запустите 'git submodule update --init --recursive'."
    exit 1
}

Push-Location $SubmodulePath
$origLocation = $RepoRoot

$markerPath = Join-Path $origLocation $MarkerFile
if (Test-Path $markerPath) {
    Pop-Location
    Write-Host "[apply-yt-cipher-patch] Патч уже применён (маркер $MarkerFile существует). Пропускаю."
    exit 0
}

if (-not (Test-Path $TargetFile)) {
    Pop-Location
    Write-Error "Файл $TargetFile не найден в submodule. Возможно версия yt-cipher изменилась и патч нужно обновить."
    exit 1
}

Write-Host "[apply-yt-cipher-patch] Применяю патч через git apply..."
$checkResult = git apply --check $PatchFile 2>&1
if ($LASTEXITCODE -ne 0) {
    Pop-Location
    Write-Error "git apply --check вернул ошибку. Возможно патч уже применён или версия yt-cipher несовместима. Вывод: $checkResult"
    exit 1
}

git apply $PatchFile
if ($LASTEXITCODE -ne 0) {
    Pop-Location
    Write-Error "git apply не смог применить патч."
    exit 1
}

# Маркер чтобы не применить дважды
New-Item -ItemType File -Path $markerPath -Force | Out-Null

$gitignorePath = Join-Path $origLocation ".gitignore"
$markerAlreadyIgnored = $false
if (Test-Path $gitignorePath) {
    $gitignoreContent = Get-Content $gitignorePath -Raw
    $markerAlreadyIgnored = $gitignoreContent.Contains($MarkerFile)
}
if (-not $markerAlreadyIgnored) {
    Add-Content -Path $gitignorePath -Value "`n$MarkerFile"
}

Pop-Location

Write-Host "[apply-yt-cipher-patch] Патч успешно применён к $TargetFile."
Write-Host "[apply-yt-cipher-patch] Запустите deno/yt-cipher: cd yt-cipher && deno run --allow-net --allow-read --allow-write --allow-env server.ts"
