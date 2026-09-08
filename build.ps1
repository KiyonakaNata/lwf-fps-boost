# LWF FPS Boost — ビルドスクリプト
#
# dotnet SDK が無い環境向け。.NET Framework 同梱の csc.exe（C# 5）で直接コンパイルし、
# 出力をゲームの BepInEx へコピーする。
# ScriptEngine（開発用の再読込）が入っていれば BepInEx\scripts へ、無ければ BepInEx\plugins へ。
# 計測用プラグイン（LwfSpineProfiler.dll）が入っていたら取り除く（二重パッチ防止）。
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoDeploy
#
# ソースは UTF-8。csc.exe は BOM が無いと既定コードページで読むため /codepage:65001 を明示する。
# このファイル自身は UTF-8 (BOM あり) で保存すること（PowerShell 5.1 は BOM なしを ANSI と読む）。

param(
    [switch]$NoDeploy,
    [string]$Game = "D:\SteamLibrary\steamapps\common\Lazy Witch's Factory"
)

$ErrorActionPreference = 'Stop'

$Managed = Join-Path $Game 'LazyWitchsFactory_Data\Managed'
$Core    = Join-Path $Game 'BepInEx\core'
$Plugins = Join-Path $Game 'BepInEx\plugins'
$Scripts = Join-Path $Game 'BepInEx\scripts'
$Csc     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir  = Join-Path $here 'bin'
$outDll  = Join-Path $outDir 'LwfFpsBoost.dll'
$sources = @(
    (Join-Path $here 'SpineThreadingMod.cs'),   # プラグイン本体（フラグ・切替・HUD）
    (Join-Path $here 'LateUpdateGuard.cs'),     # LateUpdateAsync のエラー回避処理（Harmony Postfix）
    (Join-Path $here 'UpdateGuard.cs'),         # UpdateAsync 側（WaitForThreadUpdateTasks）のエラー回避処理
    (Join-Path $here 'IncidentLog.cs'),         # 事故の記録（BepInEx/LwfFpsBoost-incidents.log に追記）
    (Join-Path $here 'GameReport.cs'),          # 1 回の工場ごとの記録（BepInEx/LwfFpsBoost-games.log に追記）
    (Join-Path $here 'StressTools.cs'),         # 負荷テスト churn / stall / hog（検証用）
    (Join-Path $here 'BuiltinSkeleton.cs'),     # タイトル画面用の埋め込み最小スケルトン
    (Join-Path $here 'Lang.cs')                 # 画面に出す文字の日英（ゲームの設定言語に追従）
)

foreach ($p in @($Csc, $Managed, $Core) + $sources) {
    if (-not (Test-Path $p)) { throw "見つかりません: $p" }
}
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$refNames = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'netstandard.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'Unity.InputSystem.dll',
    'spine-unity.dll',
    'spine-csharp.dll'
)
$coreRefNames = @('BepInEx.dll', '0Harmony.dll')

$refs = @()
foreach ($n in $refNames) {
    $p = Join-Path $Managed $n
    if (-not (Test-Path $p)) { throw "参照アセンブリが見つかりません: $p" }
    $refs += "/r:`"$p`""
}
foreach ($n in $coreRefNames) {
    $p = Join-Path $Core $n
    if (-not (Test-Path $p)) { throw "参照アセンブリが見つかりません: $p" }
    $refs += "/r:`"$p`""
}

$cscArgs = @(
    '/nologo', '/noconfig', '/nostdlib+', '/target:library', '/optimize+', '/warn:2',
    '/codepage:65001',
    # ScriptEngine は PDB を必須で読む（無いと SymbolsNotFoundException）
    '/debug:pdbonly',
    "/out:`"$outDll`""
) + $refs + ($sources | ForEach-Object { "`"$_`"" })

Write-Host "ビルド中: $outDll"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Csc
$psi.Arguments = ($cscArgs -join ' ')
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()
if ($stdout) { Write-Host $stdout }
if ($stderr) { Write-Host $stderr }
if ($proc.ExitCode -ne 0) { throw "コンパイルに失敗しました (exit $($proc.ExitCode))" }

Write-Host "OK: $outDll ($((Get-Item $outDll).Length) bytes)"

if (-not $NoDeploy) {
    $hot = Test-Path (Join-Path $Plugins 'ScriptEngine.dll')
    $target = if ($hot) { $Scripts } else { $Plugins }
    if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

    # 計測用プラグインと同居させない（デバッグメニュー開放や計測フックが乗ってしまうため）
    foreach ($dir in @($Plugins, $Scripts)) {
        $profiler = Join-Path $dir 'LwfSpineProfiler.dll'
        if (Test-Path $profiler) {
            Remove-Item $profiler -Force
            Write-Host "計測用プラグインを取り除きました: $profiler"
        }
    }
    # plugins\ に古い実体が残っていると二重に読まれる
    $stale = Join-Path $Plugins 'LwfFpsBoost.dll'
    if ($hot -and (Test-Path $stale)) {
        try {
            Remove-Item $stale -Force
            $stalePdb = Join-Path $Plugins 'LwfFpsBoost.pdb'
            if (Test-Path $stalePdb) { Remove-Item $stalePdb -Force }
            Write-Host "plugins\ の古い実体を取り除きました（scripts\ 側に一本化）"
        } catch {
            Write-Host "!! plugins\ の古い実体を消せません（ゲームが掴んでいます）。一度ゲームを終了してから実行し直してください。"
            exit 2
        }
    }

    try {
        Copy-Item $outDll -Destination $target -Force
        $outPdb = [System.IO.Path]::ChangeExtension($outDll, '.pdb')
        if (Test-Path $outPdb) { Copy-Item $outPdb -Destination $target -Force }
    } catch {
        Write-Host ""
        Write-Host "!! 配置できません。DLL が掴まれています。（ビルド自体は成功しています: $outDll）"
        exit 2
    }

    Write-Host "配置しました: $(Join-Path $target 'LwfFpsBoost.dll')"
    if ($hot) { Write-Host "ゲーム内で F6 を押すと読み直されます（再起動は不要）" }
}
