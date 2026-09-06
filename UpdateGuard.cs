// LWF FPS Boost — アニメ更新側のエラー回避処理（Postfix）
//
// SkeletonUpdateSystem.UpdateAsync は、ワーカーの完了イベント updateDone[t] を
// WaitForThreadUpdateTasks(numAsyncTasks) で 1 秒ずつ待ち、切れると
// Debug.LogError "Waiting for updateDone on main thread ran into a timeout (task index: N)!" を出して見捨てて進む。
// 見捨てられたワーカーはそのあとも AnimationState を更新し続けるので、同じフレームの後半や次フレームの
// 入力コールバック（PlayerAnimationController.SetAnimHold → AddEmptyAnimation）がメインスレッドから同じ
// AnimationState を触ると、プールに返されたばかりの TrackEntry（animation=null）を見て
// ArgumentNullException: from cannot be null（AnimationStateData.GetMix）になる。0.25.0 で報告されたもう 1 件の例外。
//
// この Postfix は WaitForThreadUpdateTasks が戻ってきた時点で updateDone[t] を見直し、
// 立っていないタスクがあれば揃うまで待つ（上限 10 秒）。「見捨てる」を「待つ」に変えるだけで、本体のコードは差し替えない。
// 分割実行（mainThreadUpdateCallbacks=true）では UpdateAsync の中で何度も呼ばれるが、そのたびに効く。
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
    [HarmonyPatch(typeof(SkeletonUpdateSystem), "WaitForThreadUpdateTasks")]
    internal static class UpdateGuard
    {
        /// <summary>本体が見捨てて進んだのを捕まえて待った回数（HUD 用）。</summary>
        internal static int TimeoutCount;
        /// <summary>待っても 10 秒で揃わず諦めた回数（HUD 用。0 のはず）。</summary>
        internal static int GiveUpCount;
        /// <summary>待った時間の合計と最大（ms。セッション記録用）。</summary>
        internal static long TotalWaitMs;
        internal static int MaxWaitMs;

        private const int WaitSliceMs = 50;
        private const int WaitMaxMs = 10000;
        private static bool _ready;

        internal static bool Prepare(out string report)
        {
            Type t = typeof(SkeletonUpdateSystem);
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo wait = t.GetMethod("WaitForThreadUpdateTasks", all);
            FieldInfo done = t.GetField("updateDone", all);
            if (wait == null || done == null || done.FieldType != typeof(List<ManualResetEventSlim>))
            {
                report = "SkeletonUpdateSystem.WaitForThreadUpdateTasks / updateDone not found";
                _ready = false;
                return false;
            }
            _ready = true;
            report = "update guard ready";
            return true;
        }

        // LateUpdateGuard.Active と同じスイッチで一緒に切れる（A/B が両方を同時に切るため）
        private static void Postfix(SkeletonUpdateSystem __instance, int numAsyncTasks)
        {
            if (!LateUpdateGuard.Active || !_ready) { return; }
            try
            {
                List<ManualResetEventSlim> done = __instance.updateDone;
                int n = Math.Min(numAsyncTasks, done.Count);
                bool anyUnset = false;
                for (int t = 0; t < n; t++) { if (!done[t].IsSet) { anyUnset = true; break; } }
                if (!anyUnset) { return; }

                TimeoutCount++;
                int waited = 0;
                for (int t = 0; t < n; t++)
                {
                    while (!done[t].IsSet && waited < WaitMaxMs)
                    {
                        done[t].Wait(WaitSliceMs);
                        waited += WaitSliceMs;
                    }
                }
                bool stillUnset = false;
                for (int t = 0; t < n; t++) { if (!done[t].IsSet) { stillUnset = true; break; } }
                if (stillUnset)
                {
                    GiveUpCount++;
                    IncidentLog.Note("guard: Update workers still busy after 10 s, gave up");
                    Debug.LogError("[LWF FPS Boost] Update workers still busy after " + (WaitMaxMs / 1000) + " s, giving up");
                    return;
                }
                TotalWaitMs += waited;
                if (waited > MaxWaitMs) { MaxWaitMs = waited; }
                IncidentLog.Note("guard: Update abandoned, waited " + waited + " ms");
                Debug.LogWarning("[LWF FPS Boost] Update abandoned its workers; waited ~" + waited + " ms for them");
            }
            catch (Exception e)
            {
                LateUpdateGuard.Active = false;
                Debug.LogError("[LWF FPS Boost] update guard threw, disabling guards for this session: " + e);
            }
        }
    }
}
