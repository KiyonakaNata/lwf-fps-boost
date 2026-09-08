// LWF FPS Boost — 事故の記録（回収用）
//
// Spine 由来の例外・エラーが出たとき、あとから分析できる形で 1 つのファイルに追記する。
// Unity の Player.log は起動ごとに上書きされ前回分しか残らず、BepInEx の LogOutput.log も上書きなので、
// 「出た」と聞いてから取りに行っても消えていることが多い。
//
// 記録する内容（1 件ごと）:
//   - 日時、ゲーム版、MOD 版、論理スレッド数、シーン、マルチスレッドと回避処理の状態、回避処理の回数、登録スケルトン数
//   - 直前の文脈（上流の timeout・回避処理が待った記録・テストの開始終了など、最新 40 行を時刻付きで）
//   - 例外の本文とスタック
// ファイル: BepInEx/LwfFpsBoost-incidents.log（追記。2 MB を超えたら .1 に 1 世代ローテーション）
//
// Application.logMessageReceivedThreaded はワーカースレッドからも呼ばれるので、ここは Unity API に触らず、
// メインスレッドで更新した静的な状態（シーン名など）だけを読む。ファイル書き込みは lock で直列化する。
//
// C# 5（csc.exe）でビルドするため、文字列補間・?.・式形式メンバは使えない。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace LwfFpsBoost
{
    internal static class IncidentLog
    {
        internal const string FileName = "LwfFpsBoost-incidents.log";
        private const int ContextLines = 40;
        private const long RotateBytes = 2L * 1024 * 1024;

        // 同じ事故が毎フレーム出ることがある（保護が外れたまま走ると 1 秒に 20 件以上）。
        // 1 件ごとに文脈 40 行を書くので、間引かないと数分でファイルが数 MB になり、
        // 書き込みそのものが重さの主因になる。中身は同じなので、詳しく残すのは最初の数件でよい。
        private const int SameMessageReports = 3;
        private const int SessionReports = 200;

        /// <summary>このセッションで起きた件数（HUD 用。間引いた分も数える）。</summary>
        internal static int Count;
        /// <summary>そのうち、詳しく書かずに数だけ数えた件数。</summary>
        internal static int Suppressed;
        /// <summary>このセッションの最初の 1 件の要約（HUD 用）。</summary>
        internal static volatile string First = "";

        // メインスレッドで更新する状態（別スレッドからは読むだけ）
        internal static volatile string SceneName = "";
        internal static volatile bool ThreadingOn;
        internal static volatile bool GuardOn;
        /// <summary>いま走っているテスト（"load test A" / "load test B" / "perf ON#1" …）。無ければ空。</summary>
        internal static volatile string TestPhase = "";

        private static readonly object Lock = new object();
        private static readonly Queue<string> Context = new Queue<string>(ContextLines + 1);
        /// <summary>同じ内容を何件書いたか。件名ごと</summary>
        private static readonly Dictionary<string, int> Written = new Dictionary<string, int>();
        /// <summary>上流の警告を何件積んだか。件名ごと</summary>
        private static readonly Dictionary<string, int> NotedUpstream = new Dictionary<string, int>();
        private static int _writtenTotal;
        private static ManualLogSource _log;
        private static string _path = "";
        private static string _gameVersion = "";
        private static string _modVersion = "";

        internal static string Path { get { return _path; } }

        internal static void Init(ManualLogSource log, string modVersion)
        {
            _log = log;
            _modVersion = modVersion;
            try { _gameVersion = Application.version; } catch (Exception) { _gameVersion = "?"; }
            try { _path = System.IO.Path.Combine(Paths.BepInExRootPath, FileName); }
            catch (Exception) { _path = FileName; }
            try
            {
                if (File.Exists(_path)) { Count = 0; }   // 件数はセッション内。過去分はファイルにある
            }
            catch (Exception) { }
        }

        /// <summary>文脈行を積む（見捨て・待ち・テストの節目など）。どのスレッドからでも可。</summary>
        internal static void Note(string line)
        {
            string stamped = Stamp() + " " + line;
            lock (Lock)
            {
                Context.Enqueue(stamped);
                while (Context.Count > ContextLines) { Context.Dequeue(); }
            }
        }

        /// <summary>
        /// Unity のログを受けて、Spine 由来のエラー・例外を 1 件として記録する。
        /// 上流の timeout メッセージは事故ではなく文脈なので Note に回す。
        /// </summary>
        internal static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) { return; }
            string c = condition ?? "";
            string s = stackTrace ?? "";
            bool upstreamTimeout = c.IndexOf("Internal threading logic error", StringComparison.Ordinal) >= 0
                || c.IndexOf("ran into a timeout", StringComparison.Ordinal) >= 0;
            if (upstreamTimeout)
            {
                // これも毎フレーム出ることがある。時刻を作って行を積むだけでも、
                // 秒あたり数十回なら無駄が積もるので、同じ内容は数件で止める
                string line = FirstLine(c);
                int noted;
                lock (Lock)
                {
                    NotedUpstream.TryGetValue(line, out noted);
                    NotedUpstream[line] = noted + 1;
                }
                if (noted < SameMessageReports) { Note("upstream: " + line); }
                return;
            }
            bool spine = c.IndexOf("Spine", StringComparison.Ordinal) >= 0 || s.IndexOf("Spine.", StringComparison.Ordinal) >= 0
                || c.IndexOf("updateDone", StringComparison.Ordinal) >= 0 || c.IndexOf("lateUpdateDone", StringComparison.Ordinal) >= 0;
            if (!spine) { return; }
            Record(type.ToString(), c, s);
        }

        private static void Record(string kind, string condition, string stackTrace)
        {
            string first = FirstLine(condition);

            // 書くかどうかを先に決める。数えるのは全件、書くのは最初の数件だけ
            bool write;
            bool lastOfKind;
            lock (Lock)
            {
                Count++;
                if (First.Length == 0) { First = first; }
                int seen;
                Written.TryGetValue(first, out seen);
                write = seen < SameMessageReports && _writtenTotal < SessionReports;
                lastOfKind = write && seen == SameMessageReports - 1;
                if (write)
                {
                    Written[first] = seen + 1;
                    _writtenTotal++;
                }
                else
                {
                    Suppressed++;
                }
            }
            if (!write)
            {
                // 文脈にも積まない。同じ行で 40 行が埋まると、直前に何が起きたか分からなくなる
                return;
            }

            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine("==== " + Stamp() + "  " + kind + "  " + first);
            sb.AppendLine("game=" + _gameVersion + "  mod=" + _modVersion + "  threads=" + Environment.ProcessorCount
                + "  scene=" + SceneName + "  threading=" + (ThreadingOn ? "on" : "off") + "  guard=" + (GuardOn ? "on" : "off")
                + "  test=" + (TestPhase.Length > 0 ? TestPhase : "none"));
            sb.AppendLine("guard: lateUpdate waits=" + LateUpdateGuard.TimeoutCount + " giveups=" + LateUpdateGuard.GiveUpCount
                + "  update waits=" + UpdateGuard.TimeoutCount + " giveups=" + UpdateGuard.GiveUpCount
                + "  registered mesh=" + LateUpdateGuard.RegisteredRenderers + " anim=" + LateUpdateGuard.RegisteredAnimations);
            sb.AppendLine("-- context (oldest first)");
            lock (Lock)
            {
                foreach (string line in Context) { sb.AppendLine(line); }
            }
            sb.AppendLine("-- message");
            sb.AppendLine(condition);
            if (!string.IsNullOrEmpty(stackTrace))
            {
                sb.AppendLine("-- stack");
                sb.AppendLine(stackTrace.TrimEnd());
            }
            sb.AppendLine();

            if (lastOfKind)
            {
                sb.AppendLine("-- note");
                sb.AppendLine("last full report for this message; further ones are counted only (see the totals at the end of the game)");
                sb.AppendLine();
            }

            lock (Lock)
            {
                try
                {
                    RotateIfLarge();
                    File.AppendAllText(_path, sb.ToString(), new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    if (_log != null) { _log.LogWarning("[incident] write failed: " + e.Message); }
                }
            }
            if (_log != null)
            {
                _log.LogError("[incident] " + kind + ": " + first + "  -> " + FileName
                    + (lastOfKind ? "  (last full report for this message)" : ""));
            }
            Note("incident: " + first);
        }

        private static void RotateIfLarge()
        {
            FileInfo fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < RotateBytes) { return; }
            string old = _path + ".1";
            if (File.Exists(old)) { File.Delete(old); }
            File.Move(_path, old);
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            string first = nl >= 0 ? s.Substring(0, nl) : s;
            return first.Length > 160 ? first.Substring(0, 160) + "…" : first;
        }
    }
}
