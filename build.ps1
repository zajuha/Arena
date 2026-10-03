<#
.SYNOPSIS
    Скрипт автоматической сборки, тестирования, публикации single-file .NET 8 и компиляции установщика Inno Setup 6.
.DESCRIPTION
    1. Запускает модульные тесты xUnit (WinRepair.Tests).
    2. Выполняет self-contained публикацию WinRepair.App в один исполняемый файл для win-x64.
    3. Компилирует скрипт installer/WinRepair.iss через ISCC.exe (Inno Setup 6).
    4. Помещает готовый установщик в каталог dist/WinRepair-Setup-<Version>.exe (и копию dist/setup.exe).
#>

[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

$PublishDir = Join-Path $RepoRoot "publish\$Runtime"
$DistDir    = Join-Path $RepoRoot "dist"
$IssFile    = Join-Path $RepoRoot "installer\WinRepair.iss"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " Сборка «Ремонт и очистка Windows» (WinRepair) v$Version" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# Шаг 1: Запуск unit-тестов xUnit
if (-not $SkipTests) {
    Write-Host "`n[1/4] Запуск модульных тестов (WinRepair.Tests)..." -ForegroundColor Yellow
    dotnet test "$RepoRoot\WinRepair.Tests\WinRepair.Tests.csproj" `
        -c $Configuration `
        --nologo `
        --verbosity normal

    if ($LASTEXITCODE -ne 0) {
        throw "Модульные тесты завершились с ошибкой (код $LASTEXITCODE). Сборка прервана."
    }
    Write-Host "Все тесты успешно пройдены." -ForegroundColor Green
}

# Шаг 2: Публикация self-contained single-file сборки WinRepair.App
Write-Host "`n[2/4] Публикация автономной сборки WinRepair.App ($Runtime)..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}

dotnet publish "$RepoRoot\WinRepair.App\WinRepair.App.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "Ошибка публикации dotnet publish (код $LASTEXITCODE)."
}

$PublishedExe = Join-Path $PublishDir "WinRepair.App.exe"
if (-not (Test-Path $PublishedExe)) {
    throw "Результирующий исполняемый файл не найден: $PublishedExe"
}
Write-Host "Исполняемый файл успешно опубликован: $PublishedExe" -ForegroundColor Green

# Шаг 3: Поиск компилятора Inno Setup 6 (ISCC.exe)
Write-Host "`n[3/4] Поиск компилятора Inno Setup 6 (ISCC.exe)..." -ForegroundColor Yellow
$IsccCandidates = @(
    "ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$IsccPath = $null
foreach ($candidate in $IsccCandidates) {
    $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        $IsccPath = $cmd.Source
        break
    }
    elseif (Test-Path $candidate) {
        $IsccPath = $candidate
        break
    }
}

if ($null -eq $IsccPath) {
    throw "Не найден компилятор Inno Setup 6 (ISCC.exe). Установите Inno Setup 6 (https://jrsoftware.org/isdl.php) или добавьте ISCC.exe в PATH."
}

Write-Host "Используется ISCC: $IsccPath" -ForegroundColor Green

# Шаг 4: Компиляция установщика .iss -> dist/WinRepair-Setup-<Version>.exe
Write-Host "`n[4/4] Компиляция установщика Inno Setup..." -ForegroundColor Yellow
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

& $IsccPath `
    "/DMyAppVersion=$Version" `
    "/DPublishDir=$PublishDir" `
    "/DOutputDistDir=$DistDir" `
    $IssFile

if ($LASTEXITCODE -ne 0) {
    throw "Ошибка компиляции установщика ISCC.exe (код $LASTEXITCODE)."
}

$VersionedSetup = Join-Path $DistDir "WinRepair-Setup-$Version.exe"
$CanonicalSetup = Join-Path $DistDir "setup.exe"

if (Test-Path $VersionedSetup) {
    Copy-Item -Path $VersionedSetup -Destination $CanonicalSetup -Force
}

Write-Host "`n=================================================================" -ForegroundColor Green
Write-Host " Сборка успешно завершена!" -ForegroundColor Green
Write-Host " Установщик (версионный): $VersionedSetup" -ForegroundColor Green
Write-Host " Установщик (setup.exe):  $CanonicalSetup" -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
