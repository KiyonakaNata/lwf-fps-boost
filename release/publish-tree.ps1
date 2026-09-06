# LWF FPS Boost — 公開 repo（D:\Wiki\lwf-fps-boost）に出すものだけを並べる
#
#   powershell -ExecutionPolicy Bypass -File release\publish-tree.ps1 [-Dest D:\Wiki\lwf-fps-boost]
#
# 出すもの:  ソース 7 本・build.ps1、release\ の README.md（配布用）・DEVELOPER.md（作者向け）・img\・release.ps1・zip-README.txt
#            findings\ のうち自分で書いた md と記録の実例
# 出さないもの:
#   - findings\LateUpdateWaitPath-port-*.cs         … 上流コードの移植（Spine Runtimes License 上、配れない）
#   - findings\SkeletonUpdateSystem-4.3-beta.cs      … 上流ソースの写し（本家 GitHub を参照させる）
#   - findings\LockFreeWorkStealingWorkerPool-4.3-beta.cs … 同上
#   - findings\v0.25.0-runtime-log.txt / measurement-results.md / static-analysis.md … 手元のパスや調査中の記録
#   - PLAN.md                                        … 内部の作業記録
#   - bin\ dist\                                     … 生成物
# DEVELOPER.md のリンクは公開 repo の配置（findings\ が同じ階層）に合わせて書き換える。
#
# ⚠ このファイル自身は UTF-8 (BOM あり) で保存すること。

param(
    [string]$Dest = "D:\Wiki\lwf-fps-boost"
)
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path   # ...\mod\release
$mod  = Split-Path -Parent $here                          # ...\mod
$proj = Split-Path -Parent $mod                           # ...\spine_perf

if (-not (Test-Path $Dest)) { throw "公開 repo がありません: $Dest（先に git init / clone しておく）" }

function CopyTo($src, $rel) {
    $dst = Join-Path $Dest $rel
    $dir = Split-Path -Parent $dst
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item $src -Destination $dst -Force
}

# ソースとビルド
foreach ($f in @('SpineThreadingMod.cs','LateUpdateGuard.cs','UpdateGuard.cs','IncidentLog.cs','GameReport.cs','StressTools.cs','BuiltinSkeleton.cs','build.ps1')) {
    CopyTo (Join-Path $mod $f) $f
}
# 文書
CopyTo (Join-Path $here 'README.md') 'README.md'
if (Test-Path (Join-Path $here 'README.en.md')) { CopyTo (Join-Path $here 'README.en.md') 'README.en.md' }

# DEVELOPER.md はリンクを公開の配置に合わせる（../findings/ → findings/）
$dev = Get-Content (Join-Path $here 'DEVELOPER.md') -Raw -Encoding UTF8
$dev = $dev.Replace('](../../findings/', '](findings/').Replace('](../', '](')
$devDst = Join-Path $Dest 'DEVELOPER.md'
[System.IO.File]::WriteAllText($devDst, $dev, (New-Object System.Text.UTF8Encoding($false)))

# findings（自分で書いたものだけ）
foreach ($f in @('threading-race-analysis.md','reproduction-2026-09-05.md','incident-sample-2026-09-06.log','games-sample-2026-09-06.log')) {
    $src = Join-Path $proj "findings\$f"
    if (Test-Path $src) { CopyTo $src "findings\$f" }
}

# 画像・release
if (Test-Path (Join-Path $here 'img')) { if (-not (Test-Path (Join-Path $Dest 'img'))) { New-Item -ItemType Directory -Path (Join-Path $Dest 'img') | Out-Null }; Copy-Item (Join-Path $here 'img\*') -Destination (Join-Path $Dest 'img') -Force -Recurse }
foreach ($f in @('release.ps1','zip-README.txt','publish-tree.ps1')) {
    $src = Join-Path $here $f
    if (Test-Path $src) { CopyTo $src "release\$f" }
}

# 出してはいけないものが混ざっていないか
$banned = Get-ChildItem $Dest -Recurse -File | Where-Object { $_.Name -like 'LateUpdateWaitPath-port*' -or $_.Name -like '*-4.3-beta.cs' -or $_.Name -eq 'PLAN.md' }
if ($banned) { throw "公開 repo に出してはいけないものがあります: $($banned.FullName -join ', ')" }

Write-Host "並べました: $Dest"
Get-ChildItem $Dest -Recurse -File | Where-Object { $_.FullName -notmatch '\\\.git\\' } | ForEach-Object { Write-Host "  $($_.FullName.Substring($Dest.Length + 1))" }
