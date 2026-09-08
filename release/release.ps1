# LWF FPS Boost — 配布用の zip を作る
#
#   powershell -ExecutionPolicy Bypass -File release\release.ps1
#
# 開発用と同じソースから、同じ DLL を作って包むだけ。
# 配布用に機能を削った別ビルドは作らない——手元で動いている物と配った物が
# 別になると、不具合の報告が来たときに再現できなくなるため。
#
# zip は 2 つ作る。手で入れる人向けと、MOD管理ソフト（Thunderstore）向け。
#
# 手で入れる人向けは、渡せば完結する中身にする:
#   LwfFpsBoost.dll
#   README.txt      （zip-README.txt を改名したもの。入れかた・キー・報告に使うファイル）
#   DEVELOPER.md    （ゲーム作者向けの説明。仕組み・再現・本体側の修正案）
#   findings\      （DEVELOPER.md が参照する、自分で書いた記録だけ。上流ソースの写しと移植版は入れない）
#
# ⚠ このファイル自身は UTF-8 (BOM あり) で保存すること。
#   PowerShell 5.1 は BOM なし UTF-8 の .ps1 を ANSI として読み、行継続が壊れる。

$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$root    = Split-Path -Parent $here
$outDir  = Join-Path $root 'dist'
$stage   = Join-Path $outDir 'stage'
$dll     = Join-Path $root 'bin\LwfFpsBoost.dll'
$source  = Join-Path $root 'SpineThreadingMod.cs'

# ---- ソースから版を読む（ここが唯一の出どころ）----
$version = (Select-String -Path $source -Pattern 'PluginVersion\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
if (-not $version) { throw "PluginVersion を読めませんでした: $source" }
Write-Host "版: $version"

# ---- ビルド（配置はしない）----
$newest = (Get-ChildItem (Join-Path $root '*.cs') | Sort-Object LastWriteTime -Descending)[0].LastWriteTime
& (Join-Path $root 'build.ps1') -NoDeploy

if (-not (Test-Path $dll)) { throw "DLL がありません: $dll" }
if ((Get-Item $dll).LastWriteTime -lt $newest) { throw "DLL がソースより古い。ビルドに失敗しています" }

# ---- 並べる ----
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item $dll -Destination $stage -Force
Copy-Item (Join-Path $here 'zip-README.txt') -Destination (Join-Path $stage 'README.txt') -Force
# DEVELOPER.md は findings\ を参照するので、自分で書いた findings だけ一緒に入れ、リンクを zip 内の配置に合わせる
$dev = Get-Content (Join-Path $here 'DEVELOPER.md') -Raw -Encoding UTF8
$dev = $dev.Replace('](../../findings/', '](findings/').Replace('](../', '](')
[System.IO.File]::WriteAllText((Join-Path $stage 'DEVELOPER.md'), $dev, (New-Object System.Text.UTF8Encoding($false)))
$findings = Join-Path (Split-Path -Parent $root) 'findings'          # 開発ツリー（spine_perfindings）
if (-not (Test-Path $findings)) { $findings = Join-Path $root 'findings' }   # 公開 repo（findings が同じ階層）
New-Item -ItemType Directory -Path (Join-Path $stage 'findings') -Force | Out-Null
foreach ($f in @('threading-race-analysis.md','reproduction-2026-09-05.md','incident-sample-2026-09-06.log','games-sample-2026-09-06.log')) {
    $src = Join-Path $findings $f
    if (Test-Path $src) { Copy-Item $src -Destination (Join-Path $stage "findings\$f") -Force }
}
# 上流コードの移植や上流ソースの写しは入れない（Spine Runtimes License）
$banned = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -like 'LateUpdateWaitPath-port*' -or $_.Name -like '*-4.3-beta.cs' }
if ($banned) { throw "配布物に入れてはいけないものがあります: $($banned.Name -join ', ')" }

$zip = Join-Path $outDir "LwfFpsBoost-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host "OK: $zip ($size KB)"

# ---- Thunderstore 用 ----
#   manifest.json / icon.png / README.md   ← thunderstore\ の中身
#   BepInEx/plugins/LwfFpsBoost.dll
# 版は manifest.json にも書く。ソースと食い違ったまま出すと上書きできないので、ここで止める
$tsDir = Join-Path $here 'thunderstore'
$manifest = Join-Path $tsDir 'manifest.json'
$tsVersion = (Select-String -Path $manifest -Pattern '"version_number"\s*:\s*"([^"]+)"').Matches[0].Groups[1].Value
if ($tsVersion -ne $version) { throw "manifest.json の版が違います: $tsVersion (ソースは $version)" }

$tsStage = Join-Path $outDir 'stage-thunderstore'
if (Test-Path $tsStage) { Remove-Item $tsStage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $tsStage 'BepInEx\plugins') -Force | Out-Null
Copy-Item $dll -Destination (Join-Path $tsStage 'BepInEx\plugins') -Force
foreach ($f in @('manifest.json', 'icon.png', 'README.md')) {
    Copy-Item (Join-Path $tsDir $f) -Destination $tsStage -Force
}

$tsZip = Join-Path $outDir "LwfFpsBoost-thunderstore-$version.zip"
if (Test-Path $tsZip) { Remove-Item $tsZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($tsStage, $tsZip)
Remove-Item $tsStage -Recurse -Force

$tsSize = [math]::Round((Get-Item $tsZip).Length / 1KB, 1)
Write-Host "OK: $tsZip ($tsSize KB)"

Write-Host ""
Add-Type -AssemblyName System.IO.Compression
foreach ($z in @($zip, $tsZip)) {
    Write-Host "$(Split-Path $z -Leaf) の中身:"
    $archive = [System.IO.Compression.ZipFile]::OpenRead($z)
    foreach ($entry in $archive.Entries) { Write-Host "  $($entry.FullName)" }
    $archive.Dispose()
}
