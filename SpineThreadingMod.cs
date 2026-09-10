// LWF FPS Boost
//
// Lazy Witch's Factory（Unity 6000.0.80f1 / spine-unity 4.3.100）で、
// spine-unity 4.3 に元から入っているマルチスレッド更新を有効にする Mod。
//
// -- なぜ必要か ---------------------------------------------------------
// ゲーム内のすべての SkeletonAnimation / SkeletonRenderer は
//   threadedAnimation      = SettingsTriState.UseGlobalSetting
//   threadedMeshGeneration = SettingsTriState.UseGlobalSetting
// になっている。つまり「グローバル設定に従う」状態。
//
// ところがそのグローバル設定の実体である Resources/SpineRuntimeSettings が
// ビルドに含まれていない。Spine.Unity.RuntimeSettings.Instance は
//
//     Resources.Load<RuntimeSettings>("SpineRuntimeSettings")
//       ?? ScriptableObject.CreateInstance<RuntimeSettings>()
//
// という実装で、後者にフォールバックする。RuntimeSettings の .ctor には
// フィールド初期化子が無いため、useThreadedAnimation も
// useThreadedMeshGeneration も bool の既定値 false になる。
//
// 結果、数百体のスケルトンのアニメ評価とメッシュ生成が全部メインスレッド1本で走る。
//
// -- この Mod がやること ------------------------------------------------
// シーンが読み込まれる前に RuntimeSettings の2つのフラグを true にする。
// SkeletonAnimationBase.OnEnable / SkeletonRenderer.OnEnable は
// UsesThreadedAnimation / UsesThreadedMeshGeneration（＝グローバル設定を参照）を見て
// SkeletonUpdateSystem に登録するので、先に立てておけば全インスタンスに効く。
//
// -- v2: LateUpdate のエラー回避処理（LateUpdateGuard.cs）------------------------------
// 出荷 DLL の LateUpdateAsync は上流既定の高速化パス（DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS）で
// ビルドされていて、完了待ちを 1 秒で見捨てて進む。見捨てられたワーカーが目覚めて古い添字でリストを引き、
// その直後にスケルトンが大量に登録解除されると List.get_Item で落ちる（ver0.25.0 の取り下げ原因）。
// v2 では Harmony の Postfix で「見捨てて戻ってきた」直後に全ワーカーの完了を待つ。本体のコードは差し替えない。
// Threading/LateUpdateGuard=false でエラー回避処理を切れる。
// （v2.0.0 は上流の待機パスを移植した Prefix だったが、Spine Runtimes License 上配れないので
//   v2.1.0 でエラー回避処理方式に書き直した。移植版は findings/LateUpdateWaitPath-port-2026-09-05.cs に記録）
//
// -- 表示とテスト ---------------------------------------------------------
// タイトル画面（シーン名 Title）だけ左上に案内を出す。事務所（Office）と工場（InGame）では何も出さず、キーも受け付けない。
//   F9  高負荷テスト … 埋め込みスケルトンで負荷を作り、ワーカーを故意に遅らせて「見捨て → 一斉解除」を踏ませ、
//                             例外が出なければ合格（StressTools.cs / BuiltinSkeleton.cs）
//   F10 マルチスレッド効果検証 … 負荷を揃えて ON → OFF → ON を計り、倍率を出す
// テスト中は「テスト中・残り秒数・重くなる」を明示し、終わったら合否を 1 行で出す。
// フレーム時間や登録数などの数字は Shift+F11 の詳細表示に隔離。開発用のキー（手動切替・churn 単体）は既定で無効。
//
// -- 実測（Ryzen 7 5700X 8C/16T・同一シーンで積み上げ計測）----------------
//   素のゲーム            72.48 ms  13.80 fps
//   +マルチスレッド        36.14 ms  27.67 fps   fps 2.01倍
//   +影OFF                32.21 ms  31.05 fps   fps 2.25倍
//
// 表示崩れ・音の欠落・例外はいずれも確認されなかった。
//
// GPU インスタンシングと SRP Batcher OFF も試したが、
// 効果が合わせて +5% 程度しかなく、インスタンシングは水路や資源オブジェが
// 消える副作用が出たため採用していない。
//
// -- 注意 ---------------------------------------------------------------
// ゲームには Mod 検出とエラーテレメトリがある（Telemetry/ModDetectionRules.cs）。
// BepInEx を入れている時点で modDetected フラグが立ち、エラー報告に載る。
// これは「Mod入り環境のクラッシュ報告を作者が弾く」ための正常な仕組みなので
// 問題ないが、不具合を作者に報告するときは Mod を外してから再現確認すること。
//
// C# 5 コンパイラ（csc.exe / .NET Framework 4.0）でビルドするため、
// 文字列補間・?. 演算子・式形式メンバは使えない。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Spine.Unity;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace LwfFpsBoost
{
    /// <summary>
    /// "Ctrl+Alt+Enter" のような文字列で指定するホットキー。
    /// 修飾キーは指定したものが押されていて、指定していないものが押されていない場合のみ成立する。
    /// </summary>
    internal sealed class Hotkey
    {
        private readonly bool _ctrl;
        private readonly bool _alt;
        private readonly bool _shift;
        private readonly Key _key;
        private readonly string _text;

        private Hotkey(bool ctrl, bool alt, bool shift, Key key, string text)
        {
            _ctrl = ctrl; _alt = alt; _shift = shift; _key = key; _text = text;
        }

        public override string ToString() { return _text; }

        internal static Hotkey Parse(string spec)
        {
            if (string.IsNullOrEmpty(spec)) { return null; }

            bool ctrl = false, alt = false, shift = false;
            string keyName = null;

            string[] parts = spec.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) { continue; }
                string lower = p.ToLowerInvariant();
                if (lower == "ctrl" || lower == "control") { ctrl = true; }
                else if (lower == "alt") { alt = true; }
                else if (lower == "shift") { shift = true; }
                else { keyName = p; }
            }
            if (keyName == null) { return null; }

            try
            {
                Key k = (Key)Enum.Parse(typeof(Key), keyName, true);
                return new Hotkey(ctrl, alt, shift, k, spec.Trim());
            }
            catch (Exception) { return null; }
        }

        internal bool WasPressedThisFrame(Keyboard kb)
        {
            if (kb == null) { return false; }

            bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            bool alt = kb.leftAltKey.isPressed || kb.rightAltKey.isPressed;
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (ctrl != _ctrl || alt != _alt || shift != _shift) { return false; }

            if (kb[_key].wasPressedThisFrame) { return true; }
            if (_key == Key.Enter && kb[Key.NumpadEnter].wasPressedThisFrame) { return true; }
            return false;
        }
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SpineThreadingMod : BaseUnityPlugin
    {
        public const string PluginGuid = "kiyonakanata.lwffpsboost";

        /// <summary>Harmony の ID。読み直し（ScriptEngine の F6）ごとに変える。
        /// 固定にすると、破棄される古いインスタンスの UnpatchSelf() が、
        /// 先に読み込まれた新しいインスタンスのパッチまで剥がしてしまう。
        /// そうなるとスレッド化（Spine のグローバル設定なので生き残る）だけが残り、
        /// 保護の待機パスが消えた状態で走り続けることになる。
        /// 読み直すたびに別のアセンブリになるため静的な連番では 1 に戻って衝突するので、
        /// 引き直しの値を使う。</summary>
        private readonly string _harmonyID = PluginGuid + "." + Guid.NewGuid().ToString("N").Substring(0, 8);
        public const string PluginName = "LWF FPS Boost";
        public const string PluginVersion = "2.2.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _threadedAnimation;
        private ConfigEntry<bool> _threadedMeshGeneration;
        private ConfigEntry<bool> _queueLock;          // タスクキューの直列化（QueueGuard.cs）。切るのは切り分け用

        // ---- 負荷テスト（検証用）----
        private StressTools _stress;
        private Hotkey _keyStressChurn;
        private Hotkey _keyStressStall;
        private Hotkey _keyStressHog;

        // ---- 高負荷テスト（F9）＝ A だけ／開発用 A/B（FullAB=true）----
        // A: エラー回避処理ありで churn + stall → 休止 → B: エラー回避処理なしで churn + stall。
        // それぞれの間に増えたカウンタの差分を取り、終わったら差し替えを戻して結果を HUD とログに出す。
        private Hotkey _keyAutoAB;
        private float _abPhaseSeconds = 30f;
        private int _abPhase;                 // AbIdle … AbDone（下の定数）
        private float _abPhaseEnd;
        private string _abSummary = "";
        private int _abBaseTimeout, _abBaseSpineErr, _abBaseLogic, _abBaseIndex, _abBaseWorkerExc, _abBaseStall, _abBaseNull;
        private string _abResultA = "";
        private string _abVerdict = "";       // 表示用の合否（1 行）
        private string _abVerdictA = "";      // A の合否（FullAB のとき B の結果と並べる）
        // false（既定）: A だけ回して「穴が塞がっているか」を判定する自己診断。ゲームは落ちない
        // true: A のあと B（エラー回避処理なし）も回す開発用の A/B。B ではゲームが例外でタイトルへ戻される
        private bool _abFullAB;

        // ---- マルチスレッド効果検証（F10）----
        // churn で負荷を揃え、マルチスレッド ON → OFF → ON の順に各 PhaseSeconds 計って平均フレーム時間を比べる。
        // ON を前後に置くのは、途中で負荷がずれていないかを見るため。
        private Hotkey _keyPerfAB;
        private float _perfPhaseSeconds = 20f;
        private int _perfPhase;               // 0=停止 1=ON 2=OFF 3=ON 4=完了
        private float _perfPhaseEnd;
        private bool _perfStartedChurn;
        private double _perfOn1, _perfOff, _perfOn2;
        private string _perfSummary = "";
        private int _perfChurnAlive = 1500;       // 測るときだけ churn をこの数にする（60fps の上限を割らせるため）
        private int _perfSavedChurnAlive;
        private int _perfSavedVSync, _perfSavedTargetFps;   // VSync を外して測り、終わったら戻す

        // Unity ログの内訳（0.25.0 の筋書きを名指しで数える）
        private int _logicErrorCount;     // "Internal threading logic error"（上流の完了待ちタイムアウト）
        private int _indexErrorCount;     // "out of range"（カウンタずれの結果）
        private int _nullErrorCount;      // "cannot be null"（GetMix。アニメ更新側の競合の結果）
        private volatile string _lastSpineError = "";

        // ---- LateUpdate のエラー回避処理（Harmony Postfix）----
        private Harmony _harmony;
        private bool _waitPathPatched;
        private string _waitPathReport = "";

        // ---- Unity ログの監視 ----
        // Debug.LogError / 未捕捉例外を数える。BepInEx のディスクログは既定で Unity ログを書かないので、
        // Player.log を開かなくても HUD で分かるようにしておく。ワーカースレッドからも来るので Interlocked。
        private int _errorCount;
        private int _spineErrorCount;
        private volatile string _lastError = "";
        private bool _logHooked;
        private ConfigEntry<bool> _hudOnStart;
        private const int HudFontSize = 16;

        private Hotkey _keyToggleThreading;
        private Hotkey _keyToggleHud;
        private Hotkey _keyToggleDetail;
        private Hotkey _keyResetStats;
        private bool _detail;                 // 詳細表示（開発向けの数字）
        private bool _inGame;                 // InGame シーンにいる（表示とテストはタイトル側だけ）
        private string _abortNotice = "";     // テスト中に画面が移動して中止したときの告知（ゲーム内でも出し続ける）
        private bool _locked;                 // 中止後はこのセッションではテストを受け付けない（再起動を促す）
        private bool _upstreamFixed;          // 本体が自分でマルチスレッドを有効にしている（この MOD は不要）

        private bool _subscribed;
        private bool _threadingOn;
        private int _skeletonCount = -1;

        // ---- フレームタイムの記録 ----
        // HUD が消えていてもここだけは回す。1フレームあたり float の加算1回。
        private const int WindowSize = 3600;          // 60fps で 60 秒ぶん
        private readonly List<float> _frameMs = new List<float>(WindowSize);
        private int _cursor;
        private double _sum;
        private int _count;
        private readonly List<float> _sortScratch = new List<float>(WindowSize);

        // ---- HUD ----
        private bool _hudVisible;
        private const float HudInterval = 0.25f;
        private float _nextHudRebuild;
        private readonly GUIContent _hudContent = new GUIContent("");
        private GUIStyle _style, _shadowStyle;
        private string _message = "";
        private float _messageUntil;

        // ------------------------------------------------------------------
        private void Awake()
        {
            // cfg の説明文は英語で、値の意味だけ（mods/CLAUDE.md の UI 規則: ラベル・値・選択肢以外は書かない）
            // 普段用の設定は Enabled だけ。キー（F9/F10/F11）と文字サイズは固定。ほかは全部 "9. Developer"
            _enabled = Config.Bind("1. General", "Enabled", true, "");
            _keyAutoAB = Hotkey.Parse("F9");
            _keyPerfAB = Hotkey.Parse("F10");
            _keyToggleHud = null;   // 表示はタイトル画面だけで常に出す。切替キーは持たない

            const string Dev = "9. Developer";
            _threadedAnimation = Config.Bind(Dev, "ThreadedAnimation", true, "");
            _threadedMeshGeneration = Config.Bind(Dev, "ThreadedMeshGeneration", true, "");
            _queueLock = Config.Bind(Dev, "QueueLock", true, "");
            _hudOnStart = Config.Bind(Dev, "ShowInGame", false, "");
            _abFullAB = Config.Bind(Dev, "FullAB", false, "true = also run with guard off (may crash the game)").Value;

            // テストの数値は調整済みの定数（cfg には出さない）。根拠は findings/reproduction-2026-09-05.md
            _stress = new StressTools(Logger);
            _stress.ChurnAlive = 600;            // 高負荷テストの体数
            _stress.ChurnPerFrame = 15;          // 毎フレーム入れ替える数
            _stress.ChurnSpread = 12f;           // ゲーム内で置く範囲（ワールド単位）
            _abPhaseSeconds = 45f;               // 高負荷テストの秒数
            _perfChurnAlive = 1500;              // 効果検証の体数（60fps の上限を割らせる）
            _perfPhaseSeconds = 20f;             // 効果検証の各フェーズ
            StressTools.StallMs = 1200;          // 上流の待ちタイムアウト 1000ms を超える
            _stress.StallEveryFrames = 60;
            _stress.StallBackoff = 12;
            StressTools.StallSlowMs = 3;
            StressTools.StallSlowCalls = 16;
            _stress.StallSeconds = 60f;
            _stress.HogThreads = 0;
            _stress.HogSeconds = 15f;

            // 開発用のキーも固定。churn / stall / hog の単体起動と集計リセットはキーを持たない（テストが中で使う部品）
            _keyToggleDetail = Hotkey.Parse("Shift+F11");
            _keyToggleThreading = Hotkey.Parse("Shift+F10");
            _keyResetStats = null;
            _keyStressChurn = null;
            _keyStressStall = null;
            _keyStressHog = null;

            // 表示とテストはタイトル側（InGame 以外）だけ。HUD/VisibleOnStart=true は開発用で、ゲーム内でも許す
            _inGame = IsInGameScene(SceneManager.GetActiveScene().name);
            _hudVisible = !_inGame || _hudOnStart.Value;

            if (!_enabled.Value)
            {
                Logger.LogInfo("[boot] Enabled=false, doing nothing");
                return;
            }

            // 本体が自分でマルチスレッドを有効にしていれば、この MOD の仕事は終わっている。
            // 何も当てずに、消してよいと伝えるだけにする
            if (GameHasThreading())
            {
                _upstreamFixed = true;
                Logger.LogInfo("[boot] the game already runs Spine threaded; this mod is not needed");
                Logger.LogInfo("[boot] delete BepInEx/plugins/LwfFpsBoost.dll");
                SceneManager.sceneLoaded += OnSceneLoaded;
                _subscribed = true;
                return;
            }

            Application.logMessageReceivedThreaded += OnUnityLog;
            _logHooked = true;
            IncidentLog.Init(Logger, PluginVersion);
            IncidentLog.SceneName = SceneManager.GetActiveScene().name;
            GameReport.Init(Logger, PluginVersion, SceneManager.GetActiveScene().name);
            Logger.LogInfo("[game] run detection: " + (GameReport.ProbeOk ? GameReport.ProbeReport : "fallback to InGame scene (" + GameReport.ProbeReport + ")"));
            StressTools.MainThreadId = Thread.CurrentThread.ManagedThreadId;

            InstallWaitPath();
            InstallQueueLock();
            InstallStallPatch();

            ApplyGlobals(_threadedAnimation.Value, _threadedMeshGeneration.Value, "boot");
            _threadingOn = _threadedAnimation.Value || _threadedMeshGeneration.Value;
            IncidentLog.ThreadingOn = _threadingOn;

            SceneManager.sceneLoaded += OnSceneLoaded;
            _subscribed = true;

            Logger.LogInfo("[boot] " + PluginName + " " + PluginVersion
                + "  guard=" + (_waitPathPatched ? "on" : "off")
                + "  queue=" + (QueueGuard.Active ? "lock" : "off")
                + "  keys: " + _keyAutoAB + "=load test, " + _keyPerfAB + "=perf test (title screen only)");
        }

        private void OnApplicationQuit()
        {
            GameReport.Finish();
        }

        private void OnDestroy()
        {
            if (_subscribed)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _subscribed = false;
            }
            if (_logHooked)
            {
                Application.logMessageReceivedThreaded -= OnUnityLog;
                _logHooked = false;
            }
            if (_abPhase != AbIdle && _abPhase != AbDone) { AbFinish("destroyed"); }
            if (_perfPhase != 0 && _perfPhase != 4) { PerfFinish("destroyed"); }
            if (_stress != null) { _stress.StopAll(); }
            // ScriptEngine で読み直したときに二重に当たらないように外す
            LateUpdateGuard.Active = false;
            QueueGuard.Active = false;
            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception e) { Logger.LogWarning("[boot] unpatch failed: " + e.Message); }
                _harmony = null;
            }
        }

        // ------------------------------------------------------------------
        // LateUpdate 待機パス
        // ------------------------------------------------------------------
        private void InstallWaitPath()
        {
            string report;
            if (!LateUpdateGuard.Prepare(out report))
            {
                _waitPathReport = report;
                Logger.LogWarning("[guard] " + report);
                return;
            }
            try
            {
                _harmony = new Harmony(_harmonyID);
                _harmony.PatchAll(typeof(LateUpdateGuard));
                Logger.LogInfo("[guard] patched SkeletonUpdateSystem.LateUpdateAsync (Postfix). " + report);
                string report2;
                if (UpdateGuard.Prepare(out report2))
                {
                    _harmony.PatchAll(typeof(UpdateGuard));
                    Logger.LogInfo("[guard] patched SkeletonUpdateSystem.WaitForThreadUpdateTasks (Postfix). " + report2);
                }
                else
                {
                    Logger.LogWarning("[guard] " + report2);
                }
                LateUpdateGuard.Active = true;
                IncidentLog.GuardOn = true;
                _waitPathPatched = true;
                _waitPathReport = report;
            }
            catch (Exception e)
            {
                _waitPathReport = "Harmony patch failed: " + e.Message;
                Logger.LogError("[guard] " + _waitPathReport);
            }
        }

        // ------------------------------------------------------------------
        // タスクキューの直列化（QueueGuard.cs）
        //   見捨てとは別の穴。回避処理（Postfix 2 つ）が入らなくても当てる
        // ------------------------------------------------------------------
        private bool _queueLockPatched;   // deque の直列化が当たっている（テストの A で ON、B で OFF に切り替える）

        private void InstallQueueLock()
        {
            string report;
            if (!QueueGuard.Prepare(out report))
            {
                Logger.LogWarning("[queue] " + report);
                return;
            }
            if (!_queueLock.Value) { Logger.LogInfo("[queue] QueueLock=false, lock left off (detector and spin still installed)"); }
            try
            {
                if (_harmony == null) { _harmony = new Harmony(_harmonyID); }
                string result = QueueGuard.Install(_harmony, _queueLock.Value);
                _queueLockPatched = _queueLock.Value;
                Logger.LogInfo("[queue] " + result + ". " + report);
            }
            catch (Exception e)
            {
                QueueGuard.Active = false;
                Logger.LogError("[queue] Harmony patch failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // 自動 A/B
        //   run   : churn + 周期 stall を PhaseSeconds 回す
        //   finale: stall を1発だけ撃ち、ワーカーが眠っている 1.2 秒の内側で churn を止めて全部を一斉に登録解除する
        //           （エラー回避処理なしは 1 秒で見捨てて進むので、目覚めたワーカーが縮んだリストを古い添字で引いて溢れる。
        //            エラー回避処理ありは Postfix で目覚めるまで待つので同じ順序でも起きない）
        //   settle: 5 秒待って、遅れて出る例外を集計に入れる
        // ------------------------------------------------------------------
        private const int AbIdle = 0, AbRunA = 1, AbFinaleA = 2, AbSettleA = 3, AbRest = 4,
                          AbRunB = 5, AbFinaleB = 6, AbSettleB = 7, AbDone = 8;
        private int _abFinaleFired0;

        private void AbStart()
        {
            if (!_waitPathPatched)
            {
                Say(Lang.T("高負荷テスト: 実行不可   エラー回避処理: 無効（", "Load test: cannot run   guard: off (") + _waitPathReport + Lang.T("）", ")"));
                return;
            }
            _abSummary = "";
            _abResultA = "";
            _abVerdict = "";
            if (!AbBeginRun(true)) { return; }
            _abPhase = AbRunA;
            if (!_hudVisible) { _hudVisible = true; }   // 経過と結果が見えるように出す。消すのは F9
            if (_skeletonCount < 0) { RefreshSkeletonCount(); }
            Say(Lang.T("高負荷テスト: 開始", "Load test: started"));
        }

        /// <summary>run フェーズに入る。churn と周期 stall を立て、カウンタの基準を取る。</summary>
        private bool AbBeginRun(bool waitPath)
        {
            LateUpdateGuard.Active = waitPath;
            QueueGuard.Active = waitPath && _queueLockPatched;   // B は deque の直列化も切る（double_run が実際に増えることを見せる）
            QueueGuard.SpinWorkers = true;                        // ワーカーを deque の巡回に張り付かせ、PushTop との重なりを毎フレーム作る
            IncidentLog.GuardOn = waitPath;
            IncidentLog.TestPhase = waitPath ? "load test A (guard on)" : "load test B (guard off)";
            IncidentLog.Note(waitPath ? "test: A start (guard on)" : "test: B start (guard off)");
            if (!_stress.ChurnOn)
            {
                string msg = _stress.ToggleChurn();
                if (!_stress.ChurnOn) { Say(msg); AbFinish("cancelled"); return false; }
            }
            AbSnapshot();
            _stress.StallPeriodic = true;
            if (!StressTools.StallOn) { _stress.ToggleStall(); }
            _abPhaseEnd = Time.unscaledTime + _abPhaseSeconds;
            ResetStats();
            Logger.LogInfo("[test] " + (waitPath ? "A start: guard on" : "B start: guard off") + ", churn + stall, " + _abPhaseSeconds + " s");
            return true;
        }

        /// <summary>finale に入る。周期 stall を止め、1 発だけ狙って撃つ。</summary>
        private void AbBeginFinale()
        {
            _stress.StallPeriodic = false;
            if (!StressTools.StallOn) { _stress.ToggleStall(); }
            _abFinaleFired0 = StressTools.StallFired;
            _stress.FireMeshStallOnce();
            _abPhaseEnd = Time.unscaledTime + 3f;   // 3 秒撃てなければ諦めて進む
        }

        private void AbSnapshot()
        {
            _abBaseTimeout = LateUpdateGuard.TimeoutCount + UpdateGuard.TimeoutCount;
            _abBaseWorkerExc = LateUpdateGuard.GiveUpCount + UpdateGuard.GiveUpCount;
            _abBaseNull = _nullErrorCount;
            _abBaseSpineErr = _spineErrorCount;
            _abBaseLogic = _logicErrorCount;
            _abBaseIndex = _indexErrorCount;
            _abBaseStall = StressTools.StallFired;
            _abBaseContended = QueueGuard.Contended;
            _abBaseDoubleRun = QueueGuard.DoubleRunCount;
        }
        private int _abBaseContended, _abBaseDoubleRun;

        private string AbDelta(string label)
        {
            return label
                + " stalls " + (StressTools.StallFired - _abBaseStall)
                + " / guard waits " + (LateUpdateGuard.TimeoutCount + UpdateGuard.TimeoutCount - _abBaseTimeout)
                + " / upstream timeouts " + (_logicErrorCount - _abBaseLogic)
                + " / out of range " + (_indexErrorCount - _abBaseIndex)
                + " / null (GetMix) " + (_nullErrorCount - _abBaseNull)
                + " / spine errors " + (_spineErrorCount - _abBaseSpineErr)
                + " / give-ups " + (LateUpdateGuard.GiveUpCount + UpdateGuard.GiveUpCount - _abBaseWorkerExc)
                + " / queue contended " + (QueueGuard.Contended - _abBaseContended)
                + " / double-run " + (QueueGuard.DoubleRunCount - _abBaseDoubleRun)
                + "  " + F(AvgMs()) + " ms";
        }

        private void AbTick()
        {
            if (_abPhase == AbIdle || _abPhase == AbDone) { return; }

            // finale: 撃てたら、ワーカーが眠っているうちに churn を止めて一斉解除
            if (_abPhase == AbFinaleA || _abPhase == AbFinaleB)
            {
                bool fired = StressTools.StallFired > _abFinaleFired0;
                if (fired || Time.unscaledTime >= _abPhaseEnd)
                {
                    if (!fired) { Logger.LogWarning("[test] finale: stall did not fire within 3 s, continuing"); }
                    _stress.StopStall();
                    if (_stress.ChurnOn) { _stress.ToggleChurn(); }   // ここで全部消える（登録解除はフレーム末）
                    Logger.LogInfo("[test] finale: mass unregister (fired=" + fired + "), settling 5 s");
                    IncidentLog.Note("test: finale, mass unregister (fired=" + fired + ")");
                    _abPhase = (_abPhase == AbFinaleA) ? AbSettleA : AbSettleB;
                    _abPhaseEnd = Time.unscaledTime + 5f;
                }
                return;
            }

            if (Time.unscaledTime < _abPhaseEnd) { return; }

            switch (_abPhase)
            {
                case AbRunA:
                    _abPhase = AbFinaleA; AbBeginFinale(); break;
                case AbSettleA:
                    _abResultA = AbDelta(_abFullAB ? "A[guard on]" : "[guard on]");
                    _abVerdictA = AbVerdict();
                    Logger.LogInfo("[test] " + _abResultA);
                    if (!_abFullAB)
                    {
                        AbFinish("done");
                        _abVerdict = _abVerdictA;
                        _abSummary = _abVerdictA + "\n                 " + _abResultA;
                        Logger.LogInfo("[test] load test: " + _abVerdictA);
                        GameReport.TestBlock("load test", _abVerdictA, _abResultA);
                        Say(Lang.T("高負荷テスト: ", "Load test: ") + _abVerdictA);
                        break;
                    }
                    _abPhase = AbRest; _abPhaseEnd = Time.unscaledTime + 5f;
                    break;
                case AbRest:
                    if (!AbBeginRun(false)) { return; }
                    _abPhase = AbRunB;
                    break;
                case AbRunB:
                    _abPhase = AbFinaleB; AbBeginFinale(); break;
                case AbSettleB:
                    {
                        string b = AbDelta("B[guard off]");
                        int bIndex = _indexErrorCount - _abBaseIndex;
                        Logger.LogInfo("[test] " + b);
                        AbFinish("done");
                        int bNull = _nullErrorCount - _abBaseNull;
                        int bDouble = QueueGuard.DoubleRunCount - _abBaseDoubleRun;
                        _abVerdict = Lang.T("あり: ", "guard on: ") + _abVerdictA + Lang.T("   なし: out of range ", "   guard off: out of range ") + bIndex + "   null " + bNull
                            + Lang.T("   二重実行 ", "   double-run ") + bDouble;
                        _abSummary = _abResultA + "\n                 " + b
                            + (_lastSpineError.Length > 0 ? "\n                 last Spine error: " + Truncate(_lastSpineError, 100) : "");
                        Logger.LogInfo("[test] A/B done. last Spine error: " + _lastSpineError);
                        GameReport.TestBlock("A/B", _abVerdict, _abResultA + "  |  " + b);
                        Say(Lang.T("A/B: 完了", "A/B: done"));
                    }
                    break;
            }
        }

        /// <summary>
        /// 自己診断の判定。穴が塞がっているかは「例外が出ない」だけでは分からない（負荷が足りずに踏まなかっただけかもしれない）ので、
        /// stall が実際に撃てて、エラー回避処理が実際に待った回数もあることを条件にする。
        /// </summary>
        private string AbVerdict()
        {
            int fired = StressTools.StallFired - _abBaseStall;
            int waited = LateUpdateGuard.TimeoutCount + UpdateGuard.TimeoutCount - _abBaseTimeout;
            int index = _indexErrorCount - _abBaseIndex;
            int nul = _nullErrorCount - _abBaseNull;
            int spine = _spineErrorCount - _abBaseSpineErr;
            int logic = _logicErrorCount - _abBaseLogic;
            int giveup = LateUpdateGuard.GiveUpCount + UpdateGuard.GiveUpCount - _abBaseWorkerExc;
            // 上流の "Internal threading logic error" は見捨てた事実の記録で、エラー回避処理が待てば無害。判定からは外し、Spine err からも差し引く
            int spineOther = spine - logic;
            int contended = QueueGuard.Contended - _abBaseContended;
            int doubleRun = QueueGuard.DoubleRunCount - _abBaseDoubleRun;
            if (index > 0 || nul > 0 || spineOther > 0 || giveup > 0 || doubleRun > 0)
            {
                return Lang.T("不合格   out of range ", "FAIL   out of range ") + index + "   null " + nul + Lang.T("   Spine 例外 ", "   Spine exceptions ") + spineOther
                    + Lang.T("   未完了 ", "   give-ups ") + giveup + Lang.T("   二重実行 ", "   double-run ") + doubleRun;
            }
            // 穴 2 つとも「実際に踏ませた」ことを条件にする。deque の重なりは lock が塞がった回数（contended）で分かる
            if (fired == 0 || waited == 0 || (QueueGuard.Active && contended == 0))
            {
                return Lang.T("判定不能   ワーカー遅延 ", "INCONCLUSIVE   worker stalls ") + fired + Lang.T("   対応 ", "   handled ") + waited
                    + Lang.T("   キュー重なり ", "   queue contended ") + contended + Lang.T("   → もう一度 ", "   → run again with ") + _keyAutoAB;
            }
            return Lang.T("合格   例外 0   ワーカー遅延 ", "PASS   exceptions 0   worker stalls ") + fired + Lang.T("   対応 ", "   handled ") + waited
                + Lang.T("   キュー重なり ", "   queue contended ") + contended + Lang.T("   二重実行 0", "   double-run 0");
        }

        private void AbFinish(string how)
        {
            _stress.StopStall();
            _stress.StallPeriodic = true;
            if (_stress.ChurnOn) { _stress.ToggleChurn(); }
            LateUpdateGuard.Active = _waitPathPatched;           // エラー回避処理を戻す
            QueueGuard.SpinWorkers = false;
            QueueGuard.Active = _queueLockPatched;
            IncidentLog.GuardOn = _waitPathPatched;
            IncidentLog.TestPhase = "";
            IncidentLog.Note("test: " + how);
            _abPhase = (how == "done") ? AbDone : AbIdle;
            Logger.LogInfo("[test] " + how + ". guard restored (active=" + LateUpdateGuard.Active + ")");
        }

        private string AbStatus()
        {
            string left = Mathf.CeilToInt(Math.Max(0f, _abPhaseEnd - Time.unscaledTime)) + "s";
            switch (_abPhase)
            {
                // 通常（FullAB=false）は A しか無いので A/B の記号は出さない
                case AbRunA: return (_abFullAB ? Lang.T("A[回避処理あり] ", "A[guard on] ") : Lang.T("負荷 ", "load ")) + left;
                case AbFinaleA: return (_abFullAB ? "A " : "") + Lang.T("一斉解除", "mass unregister");
                case AbSettleA: return (_abFullAB ? "A " : "") + Lang.T("集計 ", "settle ") + left;
                case AbRest: return Lang.T("休止 ", "rest ") + left;
                case AbRunB: return Lang.T("B[回避処理なし] ", "B[guard off] ") + left;
                case AbFinaleB: return Lang.T("B 一斉解除", "B mass unregister");
                case AbSettleB: return Lang.T("B 集計 ", "B settle ") + left;
                case AbDone: return Lang.T("完了", "done");
                default: return Lang.T("停止", "idle");
            }
        }

        // ------------------------------------------------------------------
        // 自動 性能比較
        // ------------------------------------------------------------------
        private void PerfStart()
        {
            if (_abPhase != AbIdle && _abPhase != AbDone)
            {
                Say(Lang.T("マルチスレッド効果検証: 実行不可   高負荷テスト中",
                           "Speed test: cannot run   the load test is running"));
                return;
            }
            _perfSummary = "";
            if (StressTools.StallOn) { _stress.StopStall(); }
            _perfStartedChurn = false;
            _perfSavedChurnAlive = _stress.ChurnAlive;
            _stress.ChurnAlive = Math.Max(_stress.ChurnAlive, _perfChurnAlive);
            if (!_stress.ChurnOn)
            {
                string msg = _stress.ToggleChurn();
                if (!_stress.ChurnOn) { _stress.ChurnAlive = _perfSavedChurnAlive; Say(msg); return; }
                _perfStartedChurn = true;
            }
            // VSync とフレームレート上限を外す（60fps に張り付くと差が見えない）。終わったら戻す
            _perfSavedVSync = QualitySettings.vSyncCount;
            _perfSavedTargetFps = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            if (!_threadingOn) { ToggleThreading(); }
            _perfPhase = 1;
            IncidentLog.TestPhase = "perf ON#1";
            IncidentLog.Note("perf: start");
            _perfPhaseEnd = Time.unscaledTime + _perfPhaseSeconds + 2f;   // 立ち上げの 2 秒は捨てる（ResetStats を遅らせる）
            _perfSkipDone = false;
            ResetStats();
            Logger.LogInfo("[perf] start: " + _stress.ChurnAlive + " skeletons, ON->OFF->ON, " + _perfPhaseSeconds + " s each"
                + ", vsync " + _perfSavedVSync + "->0, targetFps " + _perfSavedTargetFps + "->-1");
            Say(Lang.T("マルチスレッド効果検証: 開始", "Speed test: started"));
        }

        private void PerfTick()
        {
            if (_perfPhase == 0 || _perfPhase == 4) { return; }
            // 各フェーズの最初の 2 秒は切替直後の乱れなので、そこで一度集計を捨てる
            if (!_perfSkipDone && Time.unscaledTime >= _perfPhaseEnd - _perfPhaseSeconds)
            {
                ResetStats();
                _perfSkipDone = true;
            }
            if (Time.unscaledTime < _perfPhaseEnd) { return; }

            double avg = AvgMs();
            switch (_perfPhase)
            {
                case 1:
                    _perfOn1 = avg;
                    Logger.LogInfo("[perf] ON#1  " + F(avg) + " ms  " + F(1000.0 / Math.Max(0.001, avg)) + " fps  (" + _frameMs.Count + " frames)");
                    ToggleThreading();                       // OFF へ
                    PerfNextPhase(2);
                    break;
                case 2:
                    _perfOff = avg;
                    Logger.LogInfo("[perf] OFF   " + F(avg) + " ms  " + F(1000.0 / Math.Max(0.001, avg)) + " fps  (" + _frameMs.Count + " frames)");
                    ToggleThreading();                       // ON へ
                    PerfNextPhase(3);
                    break;
                case 3:
                    _perfOn2 = avg;
                    Logger.LogInfo("[perf] ON#2  " + F(avg) + " ms  " + F(1000.0 / Math.Max(0.001, avg)) + " fps  (" + _frameMs.Count + " frames)");
                    {
                        double on = (_perfOn1 + _perfOn2) / 2.0;
                        double ratio = on > 0.0 ? _perfOff / on : 0.0;
                        double saved = _perfOff - on;
                        // 倍率は「その場の他の処理」に左右される（タイトル画面はゲーム処理が無いので高めに出る）。
                        // 場面をまたいで比べられるのは 1 フレームあたりの短縮 ms なので、そちらを主にする
                        // 画面には値だけ。「タイトルでは倍率が高めに出る」の注記は README に置く（UI 規則）
                        _perfSummary = Lang.T("1 フレーム -", "per frame -") + F(saved)
                            + " ms   OFF " + F(_perfOff) + " ms \u2192 ON " + F(on) + " ms"
                            + "   fps " + F(1000.0 / Math.Max(0.001, _perfOff)) + " \u2192 " + F(1000.0 / Math.Max(0.001, on))
                            + Lang.T("（", " (x") + F(ratio) + Lang.T(" 倍）", ")");
                        Logger.LogInfo("[perf] ON " + F(_perfOn1) + " / OFF " + F(_perfOff) + " / ON " + F(_perfOn2) + " ms; saved " + F(saved) + " ms/frame, x" + F(ratio));
                        GameReport.TestBlock("perf", "saved " + F(saved) + " ms/frame, x" + F(ratio), "ON " + F(_perfOn1) + " / OFF " + F(_perfOff) + " / ON " + F(_perfOn2) + " ms" + (_stress.UsingBuiltin ? " (title, builtin skeletons x" + _stress.ChurnAlive + ")" : " (in game)"));
                    }
                    PerfFinish("done");
                    Say(Lang.T("マルチスレッド効果検証: 完了", "Speed test: done"));
                    break;
            }
        }

        private bool _perfSkipDone;

        private void PerfNextPhase(int phase)
        {
            _perfPhase = phase;
            IncidentLog.TestPhase = phase == 2 ? "perf OFF" : "perf ON#2";
            IncidentLog.Note("perf: phase " + phase);
            _perfPhaseEnd = Time.unscaledTime + _perfPhaseSeconds + 2f;
            _perfSkipDone = false;
            ResetStats();
        }

        private void PerfFinish(string how)
        {
            if (!_threadingOn) { ToggleThreading(); }        // 必ず ON で終わる
            if (_perfStartedChurn && _stress.ChurnOn) { _stress.ToggleChurn(); }
            _stress.ChurnAlive = _perfSavedChurnAlive;
            QualitySettings.vSyncCount = _perfSavedVSync;
            Application.targetFrameRate = _perfSavedTargetFps;
            _perfPhase = (how == "done") ? 4 : 0;
            IncidentLog.TestPhase = "";
            IncidentLog.Note("perf: " + how);
            _perfSkipDone = false;
            Logger.LogInfo("[perf] " + how);
        }

        private string PerfStatus()
        {
            string left = Mathf.CeilToInt(Math.Max(0f, _perfPhaseEnd - Time.unscaledTime)) + "s";
            switch (_perfPhase)
            {
                case 1: return "ON#1 " + left;
                case 2: return "OFF " + left;
                case 3: return "ON#2 " + left;
                case 4: return Lang.T("完了", "done");
                default: return Lang.T("停止", "idle");
            }
        }

        /// <summary>stall の割り込みは常に当てておく（StallOn=false のあいだの負担は volatile 読み1回）。</summary>
        private void InstallStallPatch()
        {
            try
            {
                if (_harmony == null) { _harmony = new Harmony(_harmonyID); }
                _harmony.PatchAll(typeof(WorkerStallPatch));
                _harmony.PatchAll(typeof(AnimStallPatch));
            }
            catch (Exception e)
            {
                Logger.LogError("[stress] stall patch failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // Unity ログの監視（別スレッドからも呼ばれる。Unity API には触らない）
        // ------------------------------------------------------------------
        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) { return; }
            Interlocked.Increment(ref _errorCount);
            IncidentLog.OnUnityLog(condition, stackTrace, type);
            string c = condition ?? "";
            string s = stackTrace ?? "";
            int nl = c.IndexOf('\n');
            string first = nl >= 0 ? c.Substring(0, nl) : c;
            // リリースビルドは LogError にスタックが付かないので、上流のメッセージ文面でも拾う
            if (c.IndexOf("Spine", StringComparison.Ordinal) >= 0 || s.IndexOf("Spine.", StringComparison.Ordinal) >= 0
                || c.IndexOf("Internal threading logic error", StringComparison.Ordinal) >= 0
                || c.IndexOf("updateDone", StringComparison.Ordinal) >= 0 || c.IndexOf("lateUpdateDone", StringComparison.Ordinal) >= 0)
            {
                Interlocked.Increment(ref _spineErrorCount);
                _lastSpineError = first;
            }
            if (c.IndexOf("Internal threading logic error", StringComparison.Ordinal) >= 0
                || c.IndexOf("ran into a timeout", StringComparison.Ordinal) >= 0) { Interlocked.Increment(ref _logicErrorCount); }
            if (c.IndexOf("cannot be null", StringComparison.Ordinal) >= 0) { Interlocked.Increment(ref _nullErrorCount); }
            if (c.IndexOf("out of range", StringComparison.OrdinalIgnoreCase) >= 0) { Interlocked.Increment(ref _indexErrorCount); }
            _lastError = first;
        }

        // ------------------------------------------------------------------
        // 設定の適用
        // ------------------------------------------------------------------
        // Assembly-CSharp に `Scene` 名前空間があるので、型のほうは完全限定で書く
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            // 言語は設定で変えられる。画面が変わるたびに引き直す
            Lang.Refresh();

            if (_upstreamFixed)
            {
                _inGame = IsInGameScene(scene.name);
                _hudVisible = !_inGame || _hudOnStart.Value;
                return;
            }

            // テスト中にシーンが変わったら中止（置いたスケルトンはシーンと一緒に消えている）
            bool aborted = false;
            if (_abPhase != AbIdle && _abPhase != AbDone) { AbFinish("cancelled"); aborted = true; }
            if (_perfPhase != 0 && _perfPhase != 4) { PerfFinish("cancelled"); aborted = true; }
            _inGame = IsInGameScene(scene.name);
            IncidentLog.SceneName = scene.name;
            IncidentLog.Note("scene: " + scene.name);
            GameReport.Scene(scene.name);
            if (aborted)
            {
                // 途中で止めた状態を引きずらないよう、このセッションはマルチスレッドを切って安全側に寄せる（再起動で戻る）
                if (_threadingOn) { ToggleThreading(); }
                _locked = true;
                GameReport.Event("test cancelled by scene change; threading disabled until restart");
                _abortNotice = "\u25a0 " + PluginName
                    + Lang.T(": テスト中止（画面切替）   マルチスレッド: 無効   → ゲームを再起動",
                             ": test stopped (scene change)   threading: off   \u2192 restart the game")
                    + (_keyToggleHud != null ? "   " + _keyToggleHud + Lang.T(": 消す", ": hide") : "");
                Logger.LogInfo("[test] cancelled by scene change (" + scene.name + "). threading disabled, tests locked until restart");
            }
            // 表示はタイトル側だけ。ゲーム内では中止の告知だけを出す（RebuildHud 側で判断）
            _hudVisible = !_inGame || _hudOnStart.Value;
            if (_hudVisible && _skeletonCount < 0) { RefreshSkeletonCount(); }

            // シーンをまたぐと、切替後の状態を保ったまま入れ直す
            ApplyGlobals(_threadingOn && _threadedAnimation.Value,
                         _threadingOn && _threadedMeshGeneration.Value,
                         "scene:" + scene.name);
            _skeletonCount = -1;
            if (_stress != null) { _stress.StopChurn(); }   // 置いたスケルトンはシーンと一緒に消えている
        }

        /// <summary>本体が既にマルチスレッドで回しているか。触る前の値を読む。</summary>
        private static bool GameHasThreading()
        {
            try { return RuntimeSettings.UseThreadedAnimation && RuntimeSettings.UseThreadedMeshGeneration; }
            catch (Exception) { return false; }
        }

        private void ApplyGlobals(bool anim, bool mesh, string reason)
        {
            try
            {
                RuntimeSettings.UseThreadedAnimation = anim;
                RuntimeSettings.UseThreadedMeshGeneration = mesh;
                Logger.LogInfo("[" + reason + "] UseThreadedAnimation="
                    + RuntimeSettings.UseThreadedAnimation
                    + " UseThreadedMeshGeneration="
                    + RuntimeSettings.UseThreadedMeshGeneration);
            }
            catch (Exception e)
            {
                Logger.LogError("[settings] apply failed: " + e);
            }
        }

        /// <summary>
        /// 実行中の切替。すでに有効になっているコンポーネントは OnEnable 時点の
        /// 判定で SkeletonUpdateSystem への登録が済んでいるので、
        /// グローバル設定を変えるだけでは切り替わらない。個別に指定し直す。
        /// 切替時の一度きりなので FindObjectsByType のコストは許容する。
        /// </summary>
        private void ToggleThreading()
        {
            bool on = !_threadingOn;
            bool anim = on && _threadedAnimation.Value;
            bool mesh = on && _threadedMeshGeneration.Value;

            ApplyGlobals(anim, mesh, on ? "toggle:ON" : "toggle:OFF");

            int animChanged = 0, meshChanged = 0, skeletons = 0;
            try
            {
                SettingsTriState animWant = anim ? SettingsTriState.Enable : SettingsTriState.Disable;
                SettingsTriState meshWant = mesh ? SettingsTriState.Enable : SettingsTriState.Disable;

                SkeletonAnimationBase[] anims = UnityEngine.Object.FindObjectsByType<SkeletonAnimationBase>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < anims.Length; i++)
                {
                    if (anims[i] != null && anims[i].ThreadedAnimation != animWant)
                    {
                        anims[i].ThreadedAnimation = animWant;
                        animChanged++;
                    }
                }

                SkeletonRenderer[] rends = UnityEngine.Object.FindObjectsByType<SkeletonRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                skeletons = rends.Length;
                for (int i = 0; i < rends.Length; i++)
                {
                    if (rends[i] != null && rends[i].ThreadedMeshGeneration != meshWant)
                    {
                        rends[i].ThreadedMeshGeneration = meshWant;
                        meshChanged++;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[threading] toggle failed: " + e);
                Say(Lang.T("マルチスレッド切替: 失敗   ", "Threading toggle failed   ") + e.Message);
                return;
            }

            _threadingOn = on;
            IncidentLog.ThreadingOn = on;
            IncidentLog.Note("threading: " + (on ? "on" : "off"));
            _skeletonCount = skeletons;
            ResetStats();

            Logger.LogInfo("[threading] " + (on ? "on" : "off") + " (anim " + animChanged + " / mesh " + meshChanged + " changed)");
            Say(Lang.T("マルチスレッド: ", "Threading: ") + (on ? Lang.T("有効", "on") : Lang.T("無効", "off")) + "   anim " + animChanged + " / mesh " + meshChanged);
        }

        // ------------------------------------------------------------------
        // 毎フレーム
        // ------------------------------------------------------------------
        private void Update()
        {
            if (!_enabled.Value) { return; }

            if (_upstreamFixed)
            {
                if (Time.unscaledTime >= _nextHudRebuild)
                {
                    _nextHudRebuild = Time.unscaledTime + HudInterval;
                    RebuildHud();
                }
                return;
            }

            RecordFrame();
            HandleInput();
            _stress.Tick();
            AbTick();
            PerfTick();

            if (Time.unscaledTime >= _nextHudRebuild)
            {
                _nextHudRebuild = Time.unscaledTime + HudInterval;
                RebuildHud();
            }
        }

        private void RecordFrame()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            GameReport.Counters(_logicErrorCount, _indexErrorCount, _nullErrorCount, _errorCount, _spineErrorCount);
            GameReport.Frame(ms, Time.unscaledDeltaTime, _threadingOn, LateUpdateGuard.RegisteredRenderers, LateUpdateGuard.RegisteredAnimations);
            if (_frameMs.Count < WindowSize)
            {
                _frameMs.Add(ms);
                _sum += ms;
            }
            else
            {
                _sum -= _frameMs[_cursor];
                _sum += ms;
                _frameMs[_cursor] = ms;
                _cursor = (_cursor + 1) % WindowSize;
            }
            _count++;
        }

        private void HandleInput()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) { return; }

            // タイトル画面以外では何も受け付けない。ShowInGame=true のときは開発用に全部許す
            if (_inGame && !_hudOnStart.Value) { return; }
            // 中止後は再起動までテストを受け付けない（表示の切替だけ許す）
            if (_locked)
            {
                if (_keyToggleHud != null && _keyToggleHud.WasPressedThisFrame(kb)) { _hudVisible = !_hudVisible; }
                if ((_keyAutoAB != null && _keyAutoAB.WasPressedThisFrame(kb)) || (_keyPerfAB != null && _keyPerfAB.WasPressedThisFrame(kb)))
                {
                    _hudVisible = true;
                    Say(Lang.T("テスト: 実行不可（画面切替で中止済み）   → ゲームを再起動",
                           "Tests are stopped until restart   \u2192 restart the game"));
                }
                return;
            }

            if (_keyToggleThreading != null && _keyToggleThreading.WasPressedThisFrame(kb))
            {
                ToggleThreading();
            }
            if (_keyToggleHud != null && _keyToggleHud.WasPressedThisFrame(kb))
            {
                _hudVisible = !_hudVisible;
                if (_hudVisible && _skeletonCount < 0) { RefreshSkeletonCount(); }
            }
            if (_keyToggleDetail != null && _keyToggleDetail.WasPressedThisFrame(kb))
            {
                _detail = !_detail;
                if (_detail) { _hudVisible = true; RefreshSkeletonCount(); }
            }
            if (_keyResetStats != null && _keyResetStats.WasPressedThisFrame(kb))
            {
                ResetStats();
                RefreshSkeletonCount();
                Say(Lang.T("集計: リセット", "Counters reset"));
            }
            if (_keyPerfAB != null && _keyPerfAB.WasPressedThisFrame(kb))
            {
                if (_perfPhase != 0 && _perfPhase != 4) { PerfFinish("cancelled"); Say(Lang.T("マルチスレッド効果検証: 中止", "Speed test: stopped")); }
                else { PerfStart(); }
            }
            if (_keyAutoAB != null && _keyAutoAB.WasPressedThisFrame(kb))
            {
                if (_abPhase != AbIdle && _abPhase != AbDone) { AbFinish("cancelled"); Say(Lang.T("高負荷テスト: 中止", "Load test: stopped")); }
                else { AbStart(); }
            }
            // Hotkey は修飾キー完全一致なので F7 単体と Alt+F7 は混ざらない
            if (_keyStressStall != null && _keyStressStall.WasPressedThisFrame(kb))
            {
                Say(_stress.ToggleStall());
            }
            else if (_keyStressHog != null && _keyStressHog.WasPressedThisFrame(kb))
            {
                Say(_stress.ToggleHog());
            }
            else if (_keyStressChurn != null && _keyStressChurn.WasPressedThisFrame(kb))
            {
                Say(_stress.ToggleChurn());
            }
        }

        private void ResetStats()
        {
            _frameMs.Clear();
            _cursor = 0;
            _sum = 0.0;
            _count = 0;
        }

        /// <summary>
        /// スケルトン数の数え直し。FindObjectsByType は重いので定期実行しない。
        /// HUD を開いたとき・切替時・リセット時だけ更新する。
        /// </summary>
        private void RefreshSkeletonCount()
        {
            try
            {
                _skeletonCount = UnityEngine.Object.FindObjectsByType<SkeletonRenderer>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
            }
            catch (Exception)
            {
                _skeletonCount = -1;
            }
        }

        // ------------------------------------------------------------------
        // HUD
        // ------------------------------------------------------------------
        private void Say(string msg)
        {
            _message = msg;
            _messageUntil = Time.unscaledTime + 5f;
        }

        private static string F(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>表示とテストはタイトル画面（シーン名 "Title"）だけ。工場（InGame）も事務所（Office）も「ゲーム内」扱いで何も出さない。</summary>
        private static bool IsInGameScene(string sceneName)
        {
            return sceneName != "Title";
        }

        /// <summary>高負荷テストの残り秒数（このあとの段階も含めた見込み）。</summary>
        private float AbRemaining()
        {
            float left = Math.Max(0f, _abPhaseEnd - Time.unscaledTime);
            float tailB = _abFullAB ? (5f + _abPhaseSeconds + 3f + 5f) : 0f;   // 休止 + B run + finale + settle
            switch (_abPhase)
            {
                case AbRunA: return left + 3f + 5f + tailB;
                case AbFinaleA: return left + 5f + tailB;
                case AbSettleA: return left + tailB;
                case AbRest: return left + _abPhaseSeconds + 3f + 5f;
                case AbRunB: return left + 3f + 5f;
                case AbFinaleB: return left + 5f;
                case AbSettleB: return left;
                default: return 0f;
            }
        }

        private float AbTotalSeconds()
        {
            return _abPhaseSeconds + 3f + 5f + (_abFullAB ? 5f + _abPhaseSeconds + 3f + 5f : 0f);
        }

        /// <summary>マルチスレッド効果検証の残り秒数。</summary>
        private float PerfRemaining()
        {
            float left = Math.Max(0f, _perfPhaseEnd - Time.unscaledTime);
            float one = _perfPhaseSeconds + 2f;
            switch (_perfPhase)
            {
                case 1: return left + one * 2f;
                case 2: return left + one;
                case 3: return left;
                default: return 0f;
            }
        }

        private float PerfTotalSeconds()
        {
            return (_perfPhaseSeconds + 2f) * 3f;
        }

        private double AvgMs()
        {
            return _frameMs.Count > 0 ? _sum / _frameMs.Count : 0.0;
        }

        private double LowMs()
        {
            int n = _frameMs.Count;
            if (n == 0) { return 0.0; }
            _sortScratch.Clear();
            for (int i = 0; i < n; i++) { _sortScratch.Add(_frameMs[i]); }
            _sortScratch.Sort();
            int idx = (int)(n * 0.99);
            if (idx >= n) { idx = n - 1; }
            return _sortScratch[idx];
        }

        private void RebuildHud()
        {
            // タイトル画面以外では何も出さない（中止の告知もタイトルに戻ったときに出す）
            if (_inGame && !_hudOnStart.Value)
            {
                _hudContent.text = "";
                return;
            }
            if (!_hudVisible)
            {
                _hudContent.text = "";
                return;
            }

            if (_upstreamFixed)
            {
                _hudContent.text = "[" + PluginName + " " + PluginVersion + "]   "
                    + Lang.T("本体が対応済み。この MOD は不要   → BepInEx/plugins/LwfFpsBoost.dll を削除",
                             "The game runs Spine threaded   → delete BepInEx/plugins/LwfFpsBoost.dll");
                return;
            }

            StringBuilder sb = new StringBuilder(768);
            bool abRunning = _abPhase != AbIdle && _abPhase != AbDone;
            bool perfRunning = _perfPhase != 0 && _perfPhase != 4;

            string on = Lang.T("有効", "on");
            string off = Lang.T("無効", "off");
            sb.AppendLine("[" + PluginName + " " + PluginVersion + "]"
                + Lang.T("   マルチスレッド: ", "   threading: ") + (_threadingOn ? on : off)
                + Lang.T("   マルチスレッドエラー回避処理: ", "   guard: ")
                + (_waitPathPatched && LateUpdateGuard.Active ? on : off));
            if (_locked)
            {
                // 中止後: 告知と再起動の案内だけ。テストの案内は出さない
                sb.AppendLine(Lang.T("\u25a0 テスト中止（画面切替）   マルチスレッド: 無効   テスト: 再起動まで停止",
                                     "\u25a0 test stopped (scene change)   threading: off   tests: stopped until restart"));
                sb.AppendLine(Lang.T("   → ゲームを再起動", "   \u2192 restart the game")
                    + (_keyToggleHud != null ? "   " + _keyToggleHud + Lang.T(": 表示 OFF", ": hide") : ""));
                if (Time.unscaledTime < _messageUntil && _message.Length > 0) { sb.AppendLine(">> " + _message); }
                _hudContent.text = sb.ToString().TrimEnd('\r', '\n');   // 末尾の改行を落とさないと背景が 1 行余る
                return;
            }

            if (IncidentLog.Count > 0)
            {
                sb.AppendLine(Lang.T("\u25a0 エラー記録: ", "\u25a0 incidents: ") + IncidentLog.Count
                    + Lang.T(" 件   BepInEx/", "   BepInEx/") + IncidentLog.FileName
                    + Lang.T("   最初: ", "   first: ") + Truncate(IncidentLog.First, 80));
            }
            sb.AppendLine(Lang.T("記録: BepInEx/", "log: BepInEx/") + GameReport.FileName
                + Lang.T("（1 回の工場ごとに追記）", " (one line per factory run)")
                + (IncidentLog.Count > 0 ? "   BepInEx/" + IncidentLog.FileName : ""));
            if (abRunning || perfRunning)
            {
                string name = abRunning ? Lang.T("高負荷テスト", "load test") : Lang.T("マルチスレッド効果検証", "speed test");
                int left = Mathf.CeilToInt(abRunning ? AbRemaining() : PerfRemaining());
                bool onTitle = !IsInGameScene(SceneManager.GetActiveScene().name);
                sb.AppendLine("\u25a0 " + name + Lang.T(": 実行中   残り約 ", ": running   ~") + left
                    + Lang.T(" 秒   PC: 高負荷", " s left   CPU: heavy"));
                sb.AppendLine("   " + (onTitle
                        ? Lang.T("タイトル画面を移動すると中止", "leaving the title screen stops it")
                        : Lang.T("画面を切り替えると中止", "changing scene stops it"))
                    + Lang.T("   段階: ", "   phase: ") + (abRunning ? AbStatus() : PerfStatus())
                    + "   " + (abRunning ? _keyAutoAB : _keyPerfAB) + Lang.T(": 中止", ": stop"));
            }
            else
            {
                string keys = "";
                if (_keyAutoAB != null)
                {
                    keys += _keyAutoAB + Lang.T(": 高負荷テスト（約 ", ": load test (~") + Mathf.CeilToInt(AbTotalSeconds())
                        + Lang.T(" 秒）   ", " s)   ");
                }
                if (_keyPerfAB != null)
                {
                    keys += _keyPerfAB + Lang.T(": マルチスレッド効果検証（約 ", ": speed test (~") + Mathf.CeilToInt(PerfTotalSeconds())
                        + Lang.T(" 秒）   ", " s)   ");
                }
                if (_keyToggleHud != null) { keys += _keyToggleHud + Lang.T(": 表示 OFF", ": hide"); }
                sb.AppendLine(keys);
            }
            if (_abPhase == AbDone && _abVerdict.Length > 0)
            {
                sb.AppendLine(Lang.T("高負荷テスト: ", "load test: ") + _abVerdict);
            }
            if (_perfPhase == 4 && _perfSummary.Length > 0)
            {
                sb.AppendLine(Lang.T("マルチスレッド効果検証: ", "speed test: ") + _perfSummary);
            }
            if (Time.unscaledTime < _messageUntil && _message.Length > 0)
            {
                sb.AppendLine(">> " + _message);
            }

            if (_detail)
            {
                double avg = AvgMs();
                double low = LowMs();
                sb.AppendLine(Lang.T("---- 詳細   ", "---- detail   ") + _keyToggleDetail
                    + Lang.T(": 閉じる ----", ": close ----"));
                sb.AppendLine(Lang.T("マルチスレッド : ", "threading   : ") + (_threadingOn ? "ON" : "OFF")
                    + "   (anim=" + RuntimeSettings.UseThreadedAnimation
                    + " mesh=" + RuntimeSettings.UseThreadedMeshGeneration + ")");
                sb.AppendLine(Lang.T("フレーム時間   : ", "frame time  : ") + F(avg) + " ms   1%Low " + F(low) + " ms   "
                    + F(avg > 0.0 ? 1000.0 / avg : 0.0) + " fps   (" + _frameMs.Count + " frames)");
                int regR = LateUpdateGuard.RegisteredRenderers;
                int regA = LateUpdateGuard.RegisteredAnimations;
                sb.AppendLine(Lang.T("スケルトン数   : 登録 mesh ", "skeletons   : registered mesh ") + (regR >= 0 ? regR.ToString() : "?")
                    + " / anim " + (regA >= 0 ? regA.ToString() : "?")
                    + Lang.T("   シーン全体 ", "   in scene ") + (_skeletonCount >= 0 ? _skeletonCount.ToString() : "?"));
                sb.AppendLine(Lang.T("LateUpdate     : ", "LateUpdate  : ")
                    + (_waitPathPatched && LateUpdateGuard.Active
                        ? Lang.T("エラー回避処理あり  checked ", "guard on   checked ") + LateUpdateGuard.FramesChecked
                          + "   caught timeout mesh " + LateUpdateGuard.TimeoutCount + " / anim " + UpdateGuard.TimeoutCount
                          + "   giveup " + (LateUpdateGuard.GiveUpCount + UpdateGuard.GiveUpCount)
                        : Lang.T("エラー回避処理なし  (", "guard off   (") + _waitPathReport + ")"));
                sb.AppendLine(Lang.T("タスクキュー   : ", "task queue  : ")
                    + (QueueGuard.Active
                        ? Lang.T("直列化あり  重なり ", "lock on   contended ") + QueueGuard.Contended
                        : Lang.T("直列化なし  重なり ", "lock off   contended ") + QueueGuard.Contended)
                    + Lang.T("   二重実行 ", "   double-run ") + QueueGuard.DoubleRunCount
                    + (QueueGuard.SpinWorkers ? Lang.T("   巡回 ", "   spin ") + QueueGuard.SpinSignals : ""));
                sb.AppendLine(Lang.T("Unity エラー   : ", "unity errors: ") + _errorCount
                    + Lang.T("   (Spine 由来 ", "   (Spine ") + _spineErrorCount + ")"
                    + (_lastError.Length > 0 ? Lang.T("   最後: ", "   last: ") + Truncate(_lastError, 90) : ""));
                sb.AppendLine(Lang.T("負荷           : ", "stress      : ") + _stress.Status());
                if (_abSummary.Length > 0) { sb.AppendLine(Lang.T("テスト集計     : ", "load test   : ") + _abSummary); }
                if (_perfSummary.Length > 0) { sb.AppendLine(Lang.T("効果検証       : ", "speed test  : ") + _perfSummary); }
            }

            _hudContent.text = sb.ToString().TrimEnd('\r', '\n');   // 末尾の改行を落とさないと背景が 1 行余る
        }

        private void OnGUI()
        {
            // OnGUI は1フレームに複数回呼ばれる。描くのは Repaint のときだけにして、
            // 中身は Update 側で作り置きしたものを使う。
            if (!_enabled.Value || Event.current.type != EventType.Repaint) { return; }
            if (_hudContent.text.Length == 0) { return; }   // 出すものが無ければ描かない（ゲーム内は告知のみ）

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = HudFontSize;
                _style.normal.textColor = Color.white;
                _style.alignment = TextAnchor.UpperLeft;
                _style.richText = false;
                _style.wordWrap = false;

                _shadowStyle = new GUIStyle(_style);
                _shadowStyle.normal.textColor = Color.black;
            }

            Vector2 size = _style.CalcSize(_hudContent);
            Rect r = new Rect(10f, 10f, size.x + 4f, size.y + 4f);

            // 読みやすさのために半透明の黒を敷く
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(r.x - 8f, r.y - 6f, r.width + 16f, r.height + 12f), Texture2D.whiteTexture);
            GUI.color = old;

            GUI.Label(new Rect(r.x - 1f, r.y, r.width, r.height), _hudContent, _shadowStyle);
            GUI.Label(new Rect(r.x + 1f, r.y, r.width, r.height), _hudContent, _shadowStyle);
            GUI.Label(new Rect(r.x, r.y - 1f, r.width, r.height), _hudContent, _shadowStyle);
            GUI.Label(new Rect(r.x, r.y + 1f, r.width, r.height), _hudContent, _shadowStyle);
            GUI.Label(r, _hudContent, _style);
        }
    }
}
