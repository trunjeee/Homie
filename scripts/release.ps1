# Сборка релиза Homie для GitHub Releases (Velopack).
#
#   .\scripts\release.ps1 1.2.0
#
# 1. Ставит версию в Homie.csproj.
# 2. Скачивает прошлый релиз с GitHub — чтобы собрать дельту (пользователи качают только изменения).
# 3. Собирает Homie и упаковывает: установщик, полный пакет, дельту и файлы releases.*.json.
#
# Потом все файлы из папки releases (кроме старых пакетов прошлых версий) загружаются в новый релиз
# на github.com/trunjeee/Homie/releases/new с тегом v<версия>.
# Утилита vpk: dotnet tool install vpk --tool-path D:\repos\tools\vpk

param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vpk = 'D:\repos\tools\vpk\vpk.exe'
$input = Join-Path $root 'release\vpk-input'
$out = Join-Path $root 'releases'

# 1. Версия
$csproj = Join-Path $root 'Homie.csproj'
$text = [IO.File]::ReadAllText($csproj, [Text.Encoding]::UTF8)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $text, (New-Object Text.UTF8Encoding $false))

# 2. Прошлый релиз — для дельты
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $vpk download github --repoUrl https://github.com/trunjeee/Homie --outputDir $out

# 3. Сборка и упаковка
if (Test-Path $input) { Remove-Item $input -Recurse -Force }
dotnet publish $csproj -c Release -o $input -nologo -v q
& $vpk pack --packId HomieApp --packVersion $Version --runtime win-x64 --packDir $input --mainExe Homie.exe `
    --packTitle Homie --packAuthors trunjeee --icon (Join-Path $root 'Assets\home.ico') --outputDir $out

Write-Host "`nГотово. Загрузи в релиз v$Version эти файлы:" -ForegroundColor Green
Get-ChildItem $out | Where-Object { $_.Name -notmatch '-full\.nupkg$' -or $_.Name -match [regex]::Escape($Version) } |
    ForEach-Object { "  {0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
