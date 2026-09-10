// LWF FPS Boost — タスクキューの直列化（Prefix + Finalizer）
//
// spine-unity 4.3-beta の LockFreeWorkStealingWorkerPool は、タスクの投入（deque.PushTop）をメインスレッドから、
// 取り出し（Pop）をワーカー自身から、横取り（Steal）を他のワーカーから呼ぶ。deque（Chase-Lev）は
// Push と Pop を同じスレッドから呼ぶ前提で、PushTop の注釈にも「他のスレッドが Push/Pop/Steal を呼ぶ前にだけ使う」とある。
// ところがワーカーは前のフェーズのタスクを終えると、そのまま Pop → 他の deque を Steal で巡回しに行き、
// 起こしたイベント（AutoResetEvent）が立ったままなら次の WaitOne も素通りする。メインは updateDone を見た瞬間に
// 次のフェーズのタスクを PushTop し始めるので、同じ deque に PushTop（メイン）と Pop/Steal（ワーカー）が重なる。
//
// 重なったときの壊れ方（findings/queue-race-analysis-2026-09-10.md）:
//   Pop が CAS で top を進める直前に PushTop が古い top を読んでいると、PushTop の「top = t-1」が CAS の結果を上書きし、
//   取り出したはずのタスクが deque に残る → 同じタスクが 2 度走る。
//   Update 側なら同じ AnimationState を 2 スレッドが同時に更新して TrackEntry の鎖とプールが壊れ、
//   あとでメインが SetAnimation した瞬間に GetMix: from cannot be null（2026-09-10 の事故。見捨て 0 回・guard の出番 0 回）。
//   LateUpdate 側なら進捗カウンタが 2 度リセット・2 度加算されてメインが範囲外を引く（0.25.0 の out of range と同じ位置）。
//
// 直し方: deque の PushTop / Push / Pop / Steal を、その deque インスタンスの lock で直列化する。
// 中身のアルゴリズムはそのまま走らせる（Prefix で Monitor.Enter、Finalizer で Monitor.Exit）。本体のコードは差し替えない。
// 1 回の操作は数十 ns、フレームあたりの回数はタスク数＋横取りの空振り（16 スレッドで数百回）なので負担は測れない程度。
//
// 証拠の数字:
//   Contended  … PushTop が lock で待たされた回数。直列化しなければそのまま Pop/Steal と重なっていた回数（Pop/Steal 同士の待ちは数えない。
//                テストの巡回中はそれが億単位になって意味を失うため）
//   DoubleRun  … タスク番号ごとに「投入した回数」と「走り始めた回数」を数え、開始が投入を上回った回数。
//                投入は PushTop の Prefix、開始は Impl 3 つの Prefix で数える。開始は必ず自分の投入より後なので、
//                この数え方には時間差による誤検出が無い（走行中フラグを Postfix で下ろす方式は、ワーカーが Set() の直後に
//                横取りされると次の周回の正当な開始を二重と誤認した。2026-09-10 の A で 1 件）
//
// テスト用の巡回（SpinWorkers）: ワーカーが Pop するたびに起床イベントを立て直し、Pop → 全 deque を Steal → Pop … と
// 回り続けさせる。実戦で数十分に 1 回の重なりを毎フレーム何百回に増やす。回すのは先頭 SpinThreads 本だけ
// （16 本全部を回すと CPU が飽和してメインが遅れ、テストの負荷測定が壊れる）。
//
// deque と pool は generic（LockFreeWorkStealingDeque<LockFreeWorkStealingWorkerPool<SkeletonUpdateRange>.Task>）なので、
// 型は SkeletonUpdateSystem.workerPool のフィールド型 → _taskQueues の要素型、と辿って実行時に決める。
//
// C# 5（csc.exe）でビルドするため、文字列補間・?.・式形式メンバ・ref 戻り値の delegate は使えない。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Spine.Unity;
using UnityEngine;

namespace LwfFpsBoost
{
    using PoolTask = LockFreeWorkStealingWorkerPool<SkeletonUpdateSystem.SkeletonUpdateRange>.Task;

    internal static class QueueGuard
    {
        /// <summary>false のあいだは lock を取らない（本体の実装のまま）。</summary>
        internal static volatile bool Active;

        /// <summary>PushTop が lock で待たされた回数（＝直列化しなければ Pop/Steal と重なっていた回数）。</summary>
        internal static int Contended;
        /// <summary>同じタスクが投入回数より多く走り始めた回数（lock ありなら 0 のはず）。</summary>
        internal static int DoubleRunCount;
        /// <summary>当てたメソッドの数（deque 側・検出側・spin の合計。HUD 用）。</summary>
        internal static int Patched;

        /// <summary>テスト用: true のあいだ、先頭 SpinThreads 本のワーカーを deque の巡回に張り付かせる。</summary>
        internal static volatile bool SpinWorkers;
        internal static int SpinThreads = 4;
        /// <summary>spin で起床イベントを立て直した回数（HUD 用。回っている証拠）。</summary>
        internal static int SpinSignals;

        private static Type _dequeType;
        private static readonly List<MethodInfo> _dequeMethods = new List<MethodInfo>();
        private static readonly List<MethodInfo> _impls = new List<MethodInfo>();
        private static FieldInfo _fPool, _fQueues, _fEvents;
        private static bool _ready;

        private static Array _spinQueues;            // LockFreeWorkStealingDeque<Task>[] _taskQueues
        private static AutoResetEvent[] _spinEvents; // AutoResetEvent[] _taskAvailable

        // タスク番号ごとの投入回数と開始回数。番号は numThreads × tasksPerThread（16 スレッドで百数十）
        private const int MaxTasks = 4096;
        private static readonly int[] _pushed = new int[MaxTasks];
        private static readonly int[] _started = new int[MaxTasks];

        /// <summary>
        /// 出荷 DLL の worker pool が LockFreeWorkStealingWorkerPool で、deque に PushTop/Pop/Steal があるか確かめる。
        /// 上流がプールを差し替えていたら（LockFreeWorkerPool など）この直列化は当てない。
        /// </summary>
        internal static bool Prepare(out string report)
        {
            _ready = false;
            _dequeMethods.Clear();
            _impls.Clear();
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            Type sys = typeof(SkeletonUpdateSystem);

            FieldInfo poolField = sys.GetField("workerPool", all);
            if (poolField == null)
            {
                report = "SkeletonUpdateSystem.workerPool not found";
                return false;
            }
            Type poolType = poolField.FieldType;
            if (poolType != typeof(LockFreeWorkStealingWorkerPool<SkeletonUpdateSystem.SkeletonUpdateRange>))
            {
                report = "worker pool is " + poolType.Name + ", not LockFreeWorkStealingWorkerPool; queue lock not installed";
                return false;
            }
            FieldInfo queues = poolType.GetField("_taskQueues", all);
            if (queues == null || !queues.FieldType.IsArray)
            {
                report = "LockFreeWorkStealingWorkerPool._taskQueues not found";
                return false;
            }
            _dequeType = queues.FieldType.GetElementType();
            _fPool = poolField;
            _fQueues = queues;
            _fEvents = poolType.GetField("_taskAvailable", all);   // 無ければ spin だけ使えない

            string[] names = new string[] { "PushTop", "Push", "Pop", "Steal" };
            List<string> found = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo m = _dequeType.GetMethod(names[i], all);
                if (m == null) { continue; }
                _dequeMethods.Add(m);
                found.Add(names[i]);
            }
            if (!found.Contains("PushTop") || !found.Contains("Pop") || !found.Contains("Steal"))
            {
                report = _dequeType.Name + " lacks PushTop/Pop/Steal (found: " + string.Join(",", found.ToArray()) + ")";
                return false;
            }

            // 二重実行の検出先（無くても直列化はする）
            string[] implNames = new string[] { "UpdateSkeletonsAsyncSplitImpl", "UpdateSkeletonsAsyncImpl", "LateUpdateSkeletonsAsyncImpl" };
            for (int i = 0; i < implNames.Length; i++)
            {
                MethodInfo m = sys.GetMethod(implNames[i], all);
                if (m != null) { _impls.Add(m); }
            }

            _ready = true;
            report = "queue lock ready (" + _dequeType.Name + ": " + string.Join("/", found.ToArray())
                + "; double-run detector on " + _impls.Count + " impl)";
            return true;
        }

        /// <summary>
        /// Harmony を当てる。lockOn なら Active にする（cfg QueueLock=false でも lock・検出・spin のパッチ自体は当てる。
        /// テストで lock の有無を切り替えるのは Active フラグ）。
        /// </summary>
        internal static string Install(Harmony harmony, bool lockOn)
        {
            if (!_ready) { return "not prepared"; }
            BindingFlags mine = BindingFlags.NonPublic | BindingFlags.Static;
            HarmonyMethod lockEnter = new HarmonyMethod(typeof(QueueGuard).GetMethod("LockEnter", mine));
            HarmonyMethod lockEnterPush = new HarmonyMethod(typeof(QueueGuard).GetMethod("LockEnterPush", mine));
            HarmonyMethod lockExit = new HarmonyMethod(typeof(QueueGuard).GetMethod("LockExit", mine));
            for (int i = 0; i < _dequeMethods.Count; i++)
            {
                bool isPush = _dequeMethods[i].Name == "PushTop" || _dequeMethods[i].Name == "Push";
                // 6 引数＝HarmonyX の新しい Patch（5 引数は obsolete でコンパイルエラー）
                harmony.Patch(_dequeMethods[i], isPush ? lockEnterPush : lockEnter, null, null, lockExit, null);
                Patched++;
            }
            Active = lockOn;

            // 検出と spin は失敗しても直列化は生かす
            string extra = "";
            try
            {
                HarmonyMethod countPush = new HarmonyMethod(typeof(QueueGuard).GetMethod("CountPush", mine));
                HarmonyMethod countStart = new HarmonyMethod(typeof(QueueGuard).GetMethod("CountStart", mine));
                HarmonyMethod spin = new HarmonyMethod(typeof(QueueGuard).GetMethod("SpinPrefix", mine));
                for (int i = 0; i < _dequeMethods.Count; i++)
                {
                    string n = _dequeMethods[i].Name;
                    if (n == "PushTop" || n == "Push")
                    {
                        harmony.Patch(_dequeMethods[i], countPush, null, null, null, null);
                        Patched++;
                    }
                    else if (n == "Pop" && _fEvents != null)
                    {
                        harmony.Patch(_dequeMethods[i], spin, null, null, null, null);
                        Patched++;
                    }
                }
                for (int i = 0; i < _impls.Count; i++)
                {
                    harmony.Patch(_impls[i], countStart, null, null, null, null);
                    Patched++;
                }
            }
            catch (Exception e)
            {
                extra = "; detector/spin patch failed: " + e.Message;
            }
            return "patched " + Patched + " methods (" + _dequeType.Name + " " + _dequeMethods.Count
                + " + detector/spin " + (Patched - _dequeMethods.Count) + "), lock=" + (lockOn ? "on" : "off") + extra;
        }

        // ------------------------------------------------------------------
        // Harmony: deque の各操作を、その deque の lock で囲む
        // ------------------------------------------------------------------
        private static void LockEnter(object __instance, ref bool __state)
        {
            if (!Active) { __state = false; return; }
            Monitor.Enter(__instance);
            __state = true;
        }

        private static void LockEnterPush(object __instance, ref bool __state)
        {
            if (!Active) { __state = false; return; }
            if (!Monitor.TryEnter(__instance))
            {
                Interlocked.Increment(ref Contended);
                Monitor.Enter(__instance);
            }
            __state = true;
        }

        private static Exception LockExit(object __instance, bool __state, Exception __exception)
        {
            if (__state) { Monitor.Exit(__instance); }
            return __exception;
        }

        // ------------------------------------------------------------------
        // Harmony: 投入回数と開始回数を数え、開始が投入を上回ったら二重実行
        // ------------------------------------------------------------------
        private static void CountPush(PoolTask item)
        {
            if (item == null) { return; }
            int idx = item.parameters.taskIndex;
            if (idx < 0 || idx >= MaxTasks) { return; }
            Interlocked.Increment(ref _pushed[idx]);
        }

        private static void CountStart(SkeletonUpdateSystem.SkeletonUpdateRange range)
        {
            int idx = range.taskIndex;
            if (idx < 0 || idx >= MaxTasks) { return; }   // -1 はメインスレッドの同期実行（キューを通らない）
            int started = Interlocked.Increment(ref _started[idx]);
            int pushed = Volatile.Read(ref _pushed[idx]);
            if (started - pushed > 0)
            {
                int n = Interlocked.Increment(ref DoubleRunCount);
                if (n <= 3)
                {
                    IncidentLog.Note("queue: task " + idx + " started more often than pushed (" + started + " > " + pushed + ", double run #" + n + ")");
                }
            }
        }

        // ------------------------------------------------------------------
        // Harmony: テスト中は先頭のワーカーを deque の巡回に張り付かせる（Pop の Prefix）
        // ------------------------------------------------------------------
        private static void SpinPrefix(object __instance)
        {
            if (!SpinWorkers) { return; }
            if (_spinEvents == null && !ResolveSpin()) { return; }
            int i = Array.IndexOf(_spinQueues, __instance);
            if (i < 0 || i >= _spinEvents.Length || i >= SpinThreads) { return; }
            Interlocked.Increment(ref SpinSignals);
            _spinEvents[i].Set();
        }

        /// <summary>pool の deque 配列と起床イベント配列を取る。pool は最初の UpdateAsync/LateUpdateAsync で作られる。</summary>
        private static bool ResolveSpin()
        {
            try
            {
                SkeletonUpdateSystem owner = LateUpdateGuard.Owner;
                if (owner == null || _fPool == null || _fEvents == null) { return false; }
                object pool = _fPool.GetValue(owner);
                if (pool == null) { return false; }
                Array queues = _fQueues.GetValue(pool) as Array;
                AutoResetEvent[] events = _fEvents.GetValue(pool) as AutoResetEvent[];
                if (queues == null || events == null) { return false; }
                _spinQueues = queues;
                _spinEvents = events;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
