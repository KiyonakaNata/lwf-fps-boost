// LWF FPS Boost — 1 回の工場ごとの記録（遊んだ結果を投げられる形にする）
//
// 事故が無かった回のデータも欲しい。何回回避処理が働いたか、最も重いフレームはどれくらいか、どんな PC か、を
// 「1 回の工場（ゲーム開始〜終了）」ごとに 1 ブロックで BepInEx/LwfFpsBoost-games.log に追記する。
// タイトル画面で回したテストの結果も、同じファイルに test ブロックとして追記する。
//
// 「ゲーム中か」はゲーム本体の RunHistory.RunRecordingRuntime.HasActiveRun をリフレクションで読む
// （本体は 1 回の工場を run として記録しており、開始・終了の境目と終了理由・run の ID を持っている）。
// コンパイル時には Assembly-CSharp に依存しない。名前が変わって読めなくなったら、InGame シーンの出入りで代用する。
//
// 途中で落ちたときに備えて、ゲーム中は 60 秒ごとに BepInEx/LwfFpsBoost-game-current.txt へ上書きしておき、
// 終了時に games.log へ移す。書式はゲーム作者へそのまま渡せる key=value の行。
//
// C# 5（csc.exe）でビルドするため、文字列補間・?.・式形式メンバは使えない。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace LwfFpsBoost
{
    internal static class GameReport
    {
        internal const string FileName = "LwfFpsBoost-games.log";
        internal const string CurrentFileName = "LwfFpsBoost-game-current.txt";
        private const float SnapshotIntervalSec = 60f;
        private const float PollIntervalSec = 0.5f;

        private static ManualLogSource _log;
        private static string _path = "";
        private static string _currentPath = "";
        private static string _system = "";
        private static string _versions = "";
        private static bool _ready;

        // ---- 本体の run 情報（リフレクション）----
        private static PropertyInfo _pHasActiveRun;      // RunRecordingRuntime.HasActiveRun (static bool)
        private static FieldInfo _fAcceptedEnd;          // RunRecordingRuntime._acceptedEndContext (GameEndContext)
        private static PropertyInfo _pEndReason;         // GameEndContext.Reason
        private static FieldInfo _fHost;                 // RunRecordingRuntime._host
        private static PropertyInfo _pGameMode;          // CurrentGameMode.Get
        private static bool _probeOk;
        private static string _probeReport = "";
        private static string _scene = "";

        // ---- いまの 1 回 ----
        private sealed class Run
        {
            public DateTime start;
            public float startedAt;
            public string mode = "";
            public string runId = "";
            public long frames;
            public double sumMs;
            public float maxMs;
            public long over33;
            public long over100;
            public int maxMesh;
            public int maxAnim;
            public double threadingOffSeconds;
            public int base_lateWaits, base_updWaits, base_giveups, base_incidents;
            public long base_lateWaitMs, base_updWaitMs;
            public int base_logic, base_index, base_null, base_unity, base_spine;
            public List<string> events = new List<string>();
        }
        private static Run _run;
        private static bool _active;
        private static float _nextPoll;
        private static float _nextSnapshot;

        // プラグイン側のカウンタ（毎フレーム渡される）
        private static int _logic, _index, _null, _unity, _spine;

        internal static string Path { get { return _path; } }
        internal static bool ProbeOk { get { return _probeOk; } }
        internal static string ProbeReport { get { return _probeReport; } }

        /// <summary>メインスレッドで呼ぶ（SystemInfo を読む）。</summary>
        internal static void Init(ManualLogSource log, string modVersion, string sceneName)
        {
            _log = log;
            try
            {
                _path = System.IO.Path.Combine(Paths.BepInExRootPath, FileName);
                _currentPath = System.IO.Path.Combine(Paths.BepInExRootPath, CurrentFileName);
            }
            catch (Exception) { _path = FileName; _currentPath = CurrentFileName; }
            try
            {
                _versions = "game=" + Application.version + "  mod=" + modVersion + "  unity=" + Application.unityVersion;
                _system = "os=\"" + SystemInfo.operatingSystem + "\"  cpu=\"" + SystemInfo.processorType.Trim() + "\"  threads=" + SystemInfo.processorCount
                    + "  ram=" + (SystemInfo.systemMemorySize / 1024) + " GB  gpu=\"" + SystemInfo.graphicsDeviceName + "\"";
            }
            catch (Exception e) { _system = "system=unavailable (" + e.Message + ")"; }
            _scene = sceneName ?? "";
            Probe();
            _ready = true;
            _nextPoll = 0f;
        }

        /// <summary>本体の RunHistory を探す。見つからなければ InGame シーンで代用。</summary>
        private static void Probe()
        {
            try
            {
                Assembly game = null;
                Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < all.Length; i++) { if (all[i].GetName().Name == "Assembly-CSharp") { game = all[i]; break; } }
                if (game == null) { _probeReport = "Assembly-CSharp not loaded"; return; }
                BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                Type runtime = game.GetType("RunHistory.RunRecordingRuntime");
                Type endCtx = game.GetType("RunHistory.GameEndContext");
                if (runtime == null || endCtx == null) { _probeReport = "RunHistory types not found"; return; }
                _pHasActiveRun = runtime.GetProperty("HasActiveRun", any);
                _fAcceptedEnd = runtime.GetField("_acceptedEndContext", any);
                _fHost = runtime.GetField("_host", any);
                _pEndReason = endCtx.GetProperty("Reason", any);
                Type[] types = game.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i].Name == "CurrentGameMode") { _pGameMode = types[i].GetProperty("Get", any); break; }
                }
                if (_pHasActiveRun == null || _pHasActiveRun.PropertyType != typeof(bool))
                {
                    _probeReport = "RunRecordingRuntime.HasActiveRun not found";
                    return;
                }
                _probeOk = true;
                _probeReport = "RunRecordingRuntime.HasActiveRun"
                    + (_fAcceptedEnd != null && _pEndReason != null ? " + end reason" : "")
                    + (_fHost != null ? " + run id" : "")
                    + (_pGameMode != null ? " + game mode" : "");
            }
            catch (Exception e)
            {
                _probeOk = false;
                _probeReport = "probe failed: " + e.Message;
            }
        }

        private static bool ReadActive()
        {
            if (_probeOk)
            {
                try { return (bool)_pHasActiveRun.GetValue(null, null); }
                catch (Exception) { _probeOk = false; }
            }
            return _scene == "InGame";
        }

        private static string ReadEndReason()
        {
            if (!_probeOk || _fAcceptedEnd == null || _pEndReason == null) { return _probeOk ? "unknown" : "scene left"; }
            try
            {
                object ctx = _fAcceptedEnd.GetValue(null);
                if (ctx == null) { return "unknown"; }
                object reason = _pEndReason.GetValue(ctx, null);
                return reason == null ? "unknown" : reason.ToString();
            }
            catch (Exception) { return "unknown"; }
        }

        private static string ReadRunId()
        {
            if (!_probeOk || _fHost == null) { return ""; }
            try
            {
                object host = _fHost.GetValue(null);
                if (host == null) { return ""; }
                FieldInfo fService = host.GetType().GetField("_service", BindingFlags.NonPublic | BindingFlags.Instance);
                object service = fService == null ? null : fService.GetValue(host);
                if (service == null) { return ""; }
                FieldInfo fId = service.GetType().GetField("_activeRunId", BindingFlags.NonPublic | BindingFlags.Instance);
                object id = fId == null ? null : fId.GetValue(service);
                return id == null ? "" : id.ToString();
            }
            catch (Exception) { return ""; }
        }

        private static string ReadGameMode()
        {
            if (_pGameMode == null) { return ""; }
            try { object m = _pGameMode.GetValue(null, null); return m == null ? "" : m.ToString(); }
            catch (Exception) { return ""; }
        }

        // ------------------------------------------------------------------
        // プラグインからの入口
        // ------------------------------------------------------------------
        internal static void Scene(string name) { _scene = name ?? ""; }

        internal static void Counters(int logic, int index, int nul, int unity, int spine)
        {
            _logic = logic; _index = index; _null = nul; _unity = unity; _spine = spine;
        }

        /// <summary>毎フレーム（プラグインの Update から）。フレームの集計と、run の開始・終了の検出。</summary>
        internal static void Frame(float ms, float dt, bool threadingOn, int registeredMesh, int registeredAnim)
        {
            if (!_ready) { return; }
            Run r = _run;
            if (r != null)
            {
                r.frames++;
                r.sumMs += ms;
                if (ms > r.maxMs) { r.maxMs = ms; }
                if (ms > 33f) { r.over33++; }
                if (ms > 100f) { r.over100++; }
                if (!threadingOn) { r.threadingOffSeconds += dt; }
                if (registeredMesh > r.maxMesh) { r.maxMesh = registeredMesh; }
                if (registeredAnim > r.maxAnim) { r.maxAnim = registeredAnim; }
            }
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollIntervalSec;
                bool active = ReadActive();
                if (active && !_active) { Begin(); }
                else if (!active && _active) { End(ReadEndReason(), "ended"); }
                _active = active;
            }
            if (_run != null && Time.unscaledTime >= _nextSnapshot)
            {
                _nextSnapshot = Time.unscaledTime + SnapshotIntervalSec;
                WriteCurrent(BuildGame(_run, "running", "running"));
            }
        }

        /// <summary>出来事（テストの結果・中止など）。ゲーム中ならその回のブロックへ、そうでなければ捨てる（テストは TestBlock で別に残す）。</summary>
        internal static void Event(string line)
        {
            Run r = _run;
            if (r == null) { return; }
            r.events.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
            if (r.events.Count > 10) { r.events.RemoveAt(0); }
        }

        /// <summary>タイトル画面のテスト結果を test ブロックとして追記する。</summary>
        internal static void TestBlock(string name, string verdict, string detail)
        {
            if (!_ready) { return; }
            StringBuilder sb = new StringBuilder(512);
            sb.AppendLine("==== test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + name + "  " + verdict);
            sb.AppendLine(_versions);
            sb.AppendLine(_system);
            sb.AppendLine("threading=" + (IncidentLog.ThreadingOn ? "on" : "off") + "  guard=" + (IncidentLog.GuardOn ? "on" : "off") + "  scene=" + _scene);
            if (!string.IsNullOrEmpty(detail)) { sb.AppendLine("detail: " + detail); }
            sb.AppendLine();
            Append(sb.ToString());
        }

        /// <summary>プロセス終了。ゲーム中なら ApplicationQuit として閉じる。</summary>
        internal static void Finish()
        {
            if (!_ready) { return; }
            if (_run != null) { End(_probeOk ? ReadEndReason() : "ApplicationQuit", "quit"); }
            try { if (File.Exists(_currentPath)) { File.Delete(_currentPath); } } catch (Exception) { }
            _ready = false;
        }

        // ------------------------------------------------------------------
        private static void Begin()
        {
            Run r = new Run();
            r.start = DateTime.Now;
            r.startedAt = Time.unscaledTime;
            r.mode = ReadGameMode();
            r.runId = ReadRunId();
            r.base_lateWaits = LateUpdateGuard.TimeoutCount; r.base_lateWaitMs = LateUpdateGuard.TotalWaitMs;
            r.base_updWaits = UpdateGuard.TimeoutCount; r.base_updWaitMs = UpdateGuard.TotalWaitMs;
            r.base_giveups = LateUpdateGuard.GiveUpCount + UpdateGuard.GiveUpCount;
            r.base_incidents = IncidentLog.Count;
            r.base_logic = _logic; r.base_index = _index; r.base_null = _null; r.base_unity = _unity; r.base_spine = _spine;
            _run = r;
            _nextSnapshot = Time.unscaledTime + SnapshotIntervalSec;
            IncidentLog.Note("game: start" + (r.runId.Length > 0 ? " run=" + r.runId : "") + (r.mode.Length > 0 ? " mode=" + r.mode : ""));
            if (_log != null) { _log.LogInfo("[game] start" + (r.runId.Length > 0 ? " run=" + r.runId : "") + (r.mode.Length > 0 ? " mode=" + r.mode : "")); }
        }

        private static void End(string reason, string status)
        {
            Run r = _run;
            if (r == null) { return; }
            if (r.runId.Length == 0) { r.runId = ReadRunId(); }
            _run = null;
            Append(BuildGame(r, reason, status));
            try { if (File.Exists(_currentPath)) { File.Delete(_currentPath); } } catch (Exception) { }
            IncidentLog.Note("game: end (" + reason + ")");
            if (_log != null) { _log.LogInfo("[game] end (" + reason + "), " + r.frames + " frames -> " + FileName); }
        }

        private static string BuildGame(Run r, string reason, string status)
        {
            DateTime now = DateTime.Now;
            double minutes = (Time.unscaledTime - r.startedAt) / 60.0;
            double avg = r.frames > 0 ? r.sumMs / r.frames : 0.0;
            int lateWaits = LateUpdateGuard.TimeoutCount - r.base_lateWaits;
            int updWaits = UpdateGuard.TimeoutCount - r.base_updWaits;
            long lateMs = LateUpdateGuard.TotalWaitMs - r.base_lateWaitMs;
            long updMs = UpdateGuard.TotalWaitMs - r.base_updWaitMs;
            int giveups = LateUpdateGuard.GiveUpCount + UpdateGuard.GiveUpCount - r.base_giveups;

            StringBuilder sb = new StringBuilder(1024);
            sb.AppendLine("==== game " + r.start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + " -> " + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  (" + F(minutes) + " min)  end=" + reason
                + (r.mode.Length > 0 ? "  mode=" + r.mode : "") + (r.runId.Length > 0 ? "  run=" + r.runId : "") + (status != "ended" ? "  status=" + status : ""));
            sb.AppendLine(_versions);
            sb.AppendLine(_system);
            sb.AppendLine("threading=" + (IncidentLog.ThreadingOn ? "on" : "off") + "  guard=" + (IncidentLog.GuardOn ? "on" : "off")
                + "  threading_off_time=" + F(r.threadingOffSeconds) + " s");
            sb.AppendLine("frames=" + r.frames + "  avg=" + F(avg) + " ms  max=" + F(r.maxMs) + " ms"
                + "  over33ms=" + r.over33 + " (" + F(r.frames > 0 ? 100.0 * r.over33 / r.frames : 0.0) + "%)  over100ms=" + r.over100);
            sb.AppendLine("skeletons_max: mesh=" + r.maxMesh + " anim=" + r.maxAnim);
            sb.AppendLine("guard: lateUpdate waits=" + lateWaits + " (total " + lateMs + " ms)  update waits=" + updWaits + " (total " + updMs + " ms)  giveups=" + giveups);
            sb.AppendLine("errors: upstream_timeouts=" + (_logic - r.base_logic) + "  incidents=" + (IncidentLog.Count - r.base_incidents)
                + " (out_of_range=" + (_index - r.base_index) + ", null=" + (_null - r.base_null) + ")"
                + "  unity_errors=" + (_unity - r.base_unity) + "  spine_errors=" + (_spine - r.base_spine));
            if (r.events.Count > 0)
            {
                sb.AppendLine("events:");
                for (int i = 0; i < r.events.Count; i++) { sb.AppendLine("  " + r.events[i]); }
            }
            sb.AppendLine();
            return sb.ToString();
        }

        private const long RotateBytes = 2L * 1024 * 1024;   // 1 ブロック約 700 バイト。ここまで溜まったら .1 に 1 世代残して新しく始める

        private static void Append(string block)
        {
            try
            {
                FileInfo fi = new FileInfo(_path);
                if (fi.Exists && fi.Length >= RotateBytes)
                {
                    string old = _path + ".1";
                    if (File.Exists(old)) { File.Delete(old); }
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, block, new UTF8Encoding(false));
            }
            catch (Exception e) { if (_log != null) { _log.LogWarning("[game] write failed: " + e.Message); } }
        }

        private static void WriteCurrent(string block)
        {
            try { File.WriteAllText(_currentPath, block, new UTF8Encoding(false)); }
            catch (Exception) { }
        }

        private static string F(double v)
        {
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
