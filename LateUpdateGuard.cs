// LWF FPS Boost — LateUpdate のエラー回避処理（Postfix）
//
// spine-unity 4.3-beta の SkeletonUpdateSystem.LateUpdateAsync は、ワーカーの進捗カウンタを見ながら
// 終わったスケルトンから順にメッシュを回収し、進みが 1 秒止まると「見捨てて」戻ってしまう
// （Debug.LogError "Internal threading logic error: exited LateUpdate loop after timeout!"）。
// 見捨てられたワーカーはそのあと目覚めて、古い範囲の添字でリストを引き、古いカウンタを進める。
// その直後にスケルトンが大量に登録解除されると、ワーカー側とメイン側の両方で
// ArgumentOutOfRangeException: Index was out of range になる（findings/reproduction-2026-09-05.md）。
//
// この Postfix は本体の実装をそのまま走らせ、戻ってきた時点で
//   1. 進捗カウンタが担当数に届いていないタスクがあるか見る（無ければ何もしない。毎フレームの負担は int の比較だけ）
//   2. あれば、完了イベントを待って全部揃うまでメインスレッドを止める（上限 10 秒）
//   3. 本体が回収しきれなかったスケルトンのメッシュ反映（UpdateMeshAndMaterialsToBuffers）を行う
// つまり「見捨てる」を「待つ」に変えるだけで、本体のコードは差し替えない。
// フレームの終わり（登録解除が走る場所）に着く前にワーカーが全員終わっているので、添字が溢れる状況が消える。
//
// 本体の private に触るのは読み出し中心:
//   skeletonsLateUpdatedAtTask（ワーカーの進捗）/ mainThreadProcessedAtTask（メインの回収数）/
//   taskPartitionsLateUpdate（タスクごとの担当範囲）。lateUpdateWorkAvailable は public。
//
// C# 5（csc.exe）でビルドするため、文字列補間・?.・式形式メンバ・ref 戻り値の delegate は使えない。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace LwfFpsBoost
{
    [HarmonyPatch(typeof(SkeletonUpdateSystem), "LateUpdateAsync")]
    internal static class LateUpdateGuard
    {
        /// <summary>false のあいだは何もしない（本体の実装のまま）。</summary>
        internal static volatile bool Active;

        /// <summary>エラー回避処理が働いたフレーム数（スケルトンが 1 体以上あって確認したもの。HUD 用）。</summary>
        internal static int FramesChecked;
        /// <summary>本体が見捨てて進んだのを捕まえて待った回数（HUD 用）。</summary>
        internal static int TimeoutCount;
        /// <summary>待っても 10 秒で揃わず諦めた回数（HUD 用。0 のはず）。</summary>
        internal static int GiveUpCount;
        /// <summary>待った時間の合計と最大（ms。セッション記録用）。</summary>
        internal static long TotalWaitMs;
        internal static int MaxWaitMs;

        private const int WaitSliceMs = 50;
        private const int WaitMaxMs = 10000;

        private static FieldInfo _fUpdatedAtTask;      // int[]  skeletonsLateUpdatedAtTask
        private static FieldInfo _fProcessedAtTask;    // int[]  mainThreadProcessedAtTask
        private static FieldInfo _fPartitions;         // ExposedList<SkeletonPartitionRange> taskPartitionsLateUpdate
        private static bool _ready;

        /// <summary>登録リスト（負荷テストの狙い決めに使う。未処理なら null）。</summary>
        internal static List<ISkeletonRenderer> RegisteredRendererList
        {
            get
            {
                SkeletonUpdateSystem o = _owner;
                return o == null ? null : o.skeletonRenderers;
            }
        }
        /// <summary>アニメ更新（InUpdate）の登録リスト（負荷テストの狙い決めに使う。未処理なら null）。</summary>
        internal static List<SkeletonAnimationBase> RegisteredAnimationList
        {
            get
            {
                SkeletonUpdateSystem o = _owner;
                return o == null ? null : o.skeletonAnimationsUpdate;
            }
        }
        internal static int RegisteredRenderers
        {
            get { SkeletonUpdateSystem o = _owner; return o == null ? -1 : o.skeletonRenderers.Count; }
        }
        internal static int RegisteredAnimations
        {
            get
            {
                SkeletonUpdateSystem o = _owner;
                if (o == null) { return -1; }
                return o.skeletonAnimationsUpdate.Count + o.skeletonAnimationsFixedUpdate.Count + o.skeletonAnimationsLateUpdate.Count;
            }
        }
        private static SkeletonUpdateSystem _owner;

        /// <summary>
        /// 出荷 DLL が想定どおり（高速化パスでビルドされている）か確かめ、必要なリフレクションを用意する。
        /// 上流が待機パスに戻していたらエラー回避処理をする意味が無いので false を返す。
        /// </summary>
        internal static bool Prepare(out string report)
        {
            Type t = typeof(SkeletonUpdateSystem);
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            _fUpdatedAtTask = t.GetField("skeletonsLateUpdatedAtTask", all);
            _fProcessedAtTask = t.GetField("mainThreadProcessedAtTask", all);
            _fPartitions = t.GetField("taskPartitionsLateUpdate", all);
            MethodInfo waitPathMarker = t.GetMethod("WaitForThreadLateUpdateTasks", all);

            if (_fUpdatedAtTask == null || waitPathMarker != null)
            {
                report = "spine-unity.dll is not built with DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS; upstream seems fixed, guard not installed";
                _ready = false;
                return false;
            }
            if (_fProcessedAtTask == null || _fPartitions == null
                || _fPartitions.FieldType != typeof(ExposedList<SkeletonUpdateSystem.SkeletonPartitionRange>))
            {
                report = "SkeletonUpdateSystem private members not found (mainThreadProcessedAtTask="
                    + (_fProcessedAtTask != null) + ", taskPartitionsLateUpdate=" + (_fPartitions != null) + ")";
                _ready = false;
                return false;
            }
            _ready = true;
            report = "guard ready (threads=" + Environment.ProcessorCount + ")";
            return true;
        }

        // ------------------------------------------------------------------
        // Harmony
        // ------------------------------------------------------------------
        private static void Postfix(SkeletonUpdateSystem __instance)
        {
            if (!Active || !_ready) { return; }
            try
            {
                Run(__instance);
            }
            catch (Exception e)
            {
                Active = false;
                Debug.LogError("[LWF FPS Boost] guard threw, disabling it for this session: " + e);
            }
        }

        private static void Run(SkeletonUpdateSystem inst)
        {
            _owner = inst;
            List<ISkeletonRenderer> renderers = inst.skeletonRenderers;
            if (renderers.Count == 0) { return; }

            int[] updated = _fUpdatedAtTask.GetValue(inst) as int[];
            int[] processed = _fProcessedAtTask.GetValue(inst) as int[];
            ExposedList<SkeletonUpdateSystem.SkeletonPartitionRange> partitions =
                _fPartitions.GetValue(inst) as ExposedList<SkeletonUpdateSystem.SkeletonPartitionRange>;
            if (updated == null || processed == null || partitions == null) { return; }   // まだ一度も並列で走っていない
            FramesChecked++;

            int numTasks = Math.Min(partitions.Count, Math.Min(updated.Length, processed.Length));
            SkeletonUpdateSystem.SkeletonPartitionRange[] items = partitions.Items;

            // 1. 見捨てられたタスクがあるか
            if (!AnyUnfinished(items, updated, numTasks)) { return; }

            // 2. 全部揃うまで待つ（本体は 1 秒で諦めたが、こちらは待つ）
            TimeoutCount++;
            AutoResetEvent signal = inst.lateUpdateWorkAvailable;
            int waited = 0;
            while (AnyUnfinished(items, updated, numTasks) && waited < WaitMaxMs)
            {
                if (signal != null) { signal.WaitOne(WaitSliceMs); } else { Thread.Sleep(WaitSliceMs); }
                waited += WaitSliceMs;
            }
            if (AnyUnfinished(items, updated, numTasks))
            {
                GiveUpCount++;
                IncidentLog.Note("guard: LateUpdate workers still busy after 10 s, gave up");
                Debug.LogError("[LWF FPS Boost] LateUpdate workers still busy after " + (WaitMaxMs / 1000) + " s, giving up");
                return;
            }
            TotalWaitMs += waited;
            if (waited > MaxWaitMs) { MaxWaitMs = waited; }
            IncidentLog.Note("guard: LateUpdate abandoned, waited " + waited + " ms");
            Debug.LogWarning("[LWF FPS Boost] LateUpdate abandoned its workers; waited ~" + waited + " ms for them");

            // 3. 本体が回収しきれなかったぶんのメッシュ反映
            int count = renderers.Count;
            for (int t = 0; t < numTasks; t++)
            {
                SkeletonUpdateSystem.SkeletonPartitionRange p = items[t];
                int start = p.rangeStart + Math.Max(0, processed[t]);
                for (int r = start; r < p.rangeEndExclusive && r < count; r++)
                {
                    ISkeletonRenderer sr = renderers[r];
                    if (sr != null && sr.RequiresMeshBufferAssignmentMainThread)
                    {
                        sr.UpdateMeshAndMaterialsToBuffers();
                    }
                }
                processed[t] = p.rangeEndExclusive - p.rangeStart;
            }
        }

        private static bool AnyUnfinished(SkeletonUpdateSystem.SkeletonPartitionRange[] items, int[] updated, int numTasks)
        {
            for (int t = 0; t < numTasks; t++)
            {
                int countAtTask = items[t].rangeEndExclusive - items[t].rangeStart;
                if (countAtTask <= 0) { continue; }
                if (Volatile.Read(ref updated[t]) < countAtTask) { return true; }
            }
            return false;
        }
    }
}
