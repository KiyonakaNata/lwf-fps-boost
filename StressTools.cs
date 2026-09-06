// LWF FPS Boost — 負荷テスト（検証用）
//
// 0.25.0 で報告された例外を「わざと」踏むための道具。2つある。
//
//  1. churn（既定キー無し。高負荷テストが中で使う）: Spine のスケルトンだけを場面のデータで直接作って、毎フレーム一定数を捨てて作り直す。
//     SkeletonUpdateSystem への登録・解除がフレームごとに動く状況を作る。ついでに一部は
//     アニメ切替（メインスレッドから AnimationState を触る）と、更新完了コールバック内での
//     SetActive(false)（処理中の登録変更＝遅延リスト経路）を混ぜる。
//     ゲームの使い魔ではないので、経済・セーブ・実績には触らない。見た目に同じ絵が増えるだけ。
//
//  2. stall（既定キー無し。同上）: ワーカー側の SkeletonRenderer.LateUpdateImplementation に Harmony で割り込み、
//     数秒に1回、ワーカーの1タスクだけを 1.2 秒眠らせる。上流の高速化パスは完了待ちを1秒で諦めて
//     進むので、そのあとで目覚めたワーカーが古いカウンタを進める＝カウンタと実態がずれる、というのが
//     0.25.0 の例外の筋書きそのもの。エラー回避処理（LateUpdateGuard）があれば揃うまで待つので「待った回数」が増えるだけで済む。
//     既定 60 秒で自動停止する。
//
//  3. hog（既定キー無し）: 優先度 AboveNormal の空回りスレッドを論理コア数の2倍立てて、
//     Spine のワーカー（通常優先度）を止めようとするもの。実測では Windows の飢餓防止に負けて
//     1秒の停止を作れなかったので、stall に置き換えた。残してあるが既定では効かない。
//     ゲーム全体が固まるので、押す前にセーブしておくこと。
//
// C# 5（csc.exe）でビルドするため、文字列補間・?.・式形式メンバは使えない。

using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using Spine.Unity;
using UnityEngine;

namespace LwfFpsBoost
{
    /// <summary>
    /// stall の割り込み。ワーカースレッドで走る LateUpdateImplementation(calledFromMainThread:false) のうち、
    /// 「弾が込められている」ときの最初の1回だけ眠る。メインスレッド側（true）は触らない。
    /// StallOn が false のときの負担は volatile 読み1回。
    /// </summary>
    [HarmonyPatch(typeof(SkeletonRenderer), "LateUpdateImplementation")]
    internal static class WorkerStallPatch
    {
        // 眠りが明けたスレッドだけ、以後の数体を1体ごとに少し遅らせる。
        // 古いタスクの加算が数十ミリ秒に広がり、メイン側の回収ループのどこかに必ず重なるようにするため
        // （タスク1つは数体しかなく、素直に続けると1〜2ミリ秒で終わってしまい重ならない）。
        [ThreadStatic] private static int _slowCallsLeft;

        [HarmonyPrefix]
        private static void Prefix(SkeletonRenderer __instance, bool calledFromMainThread)
        {
            if (!StressTools.StallOn || calledFromMainThread) { return; }
            if (_slowCallsLeft > 0)
            {
                _slowCallsLeft--;
                Thread.Sleep(StressTools.StallSlowMs);
                return;
            }
            if (StressTools.StallArmed != 1) { return; }
            // 狙いが決まっているときはそのスケルトンの番だけ。無ければ最初に来たもの
            SkeletonRenderer target = StressTools.StallTarget;
            if (target != null && !ReferenceEquals(__instance, target)) { return; }
            if (Interlocked.CompareExchange(ref StressTools.StallArmed, 0, 1) == 1)
            {
                Interlocked.Increment(ref StressTools.StallFired);
                Thread.Sleep(StressTools.StallMs);
                _slowCallsLeft = StressTools.StallSlowCalls;
            }
        }
    }

    /// <summary>
    /// アニメ更新側の stall。ワーカースレッドで走る SkeletonAnimationBase.UpdateInternalSplit のうち、
    /// 狙いのスケルトンの番だけ眠る。メインスレッド（ManagedThreadId が一致）は触らない。
    /// 目覚めたあとの数体を遅らせ、そのあいだにメインが同じ AnimationState を叩く（StressTools.Tick の hammer）。
    /// </summary>
    [HarmonyPatch(typeof(SkeletonAnimationBase), "UpdateInternalSplit")]
    internal static class AnimStallPatch
    {
        [ThreadStatic] private static int _slowCallsLeft;

        [HarmonyPrefix]
        private static void Prefix(SkeletonAnimationBase __instance)
        {
            if (!StressTools.StallOn) { return; }
            if (Thread.CurrentThread.ManagedThreadId == StressTools.MainThreadId) { return; }
            if (_slowCallsLeft > 0)
            {
                _slowCallsLeft--;
                Thread.Sleep(StressTools.AnimSlowMs);
                return;
            }
            if (StressTools.AnimStallArmed != 1) { return; }
            SkeletonAnimationBase target = StressTools.AnimStallTarget;
            if (target != null && !ReferenceEquals(__instance, target)) { return; }
            if (Interlocked.CompareExchange(ref StressTools.AnimStallArmed, 0, 1) == 1)
            {
                Interlocked.Increment(ref StressTools.StallFired);
                Interlocked.Increment(ref StressTools.AnimStallFired);
                Thread.Sleep(StressTools.StallMs);
                _slowCallsLeft = StressTools.AnimSlowCalls;
            }
        }
    }

    internal sealed class StressTools
    {
        // ---- anim stall（アニメ更新側）----
        internal static int MainThreadId;                        // プラグインの Awake で入れる
        internal static int AnimStallArmed;
        internal static int AnimStallFired;
        internal static volatile SkeletonAnimationBase AnimStallTarget;
        internal static int AnimSlowMs = 10;                     // 目覚めたあと 1 体ごとに遅らせる時間（ms）
        internal static int AnimSlowCalls = 30;                  // それを何体ぶん続けるか（10ms × 30 = 約 300ms、数フレームぶん）
        internal int HammerExceptions;                           // メインから叩いて出た例外の数（HUD 用）
        private bool _nextIsAnim;                                // 周期 stall はメッシュ側とアニメ側を交互に
        private float _hammerUntil;
        private readonly List<SkeletonAnimationBase> _hammer = new List<SkeletonAnimationBase>(32);

        // ---- stall ----
        internal static volatile bool StallOn;
        internal static int StallArmed;          // 1 のとき、次にワーカーへ来た LateUpdateImplementation が眠る
        internal static int StallFired;          // 実際に眠った回数（HUD 用）
        internal static int StallMs = 1200;      // 上流の待ちタイムアウト 1000ms を少し超える
        internal static volatile SkeletonRenderer StallTarget;   // 眠らせる相手（null なら最初に来たワーカー呼び出し）
        internal static int StallSlowMs = 3;     // 眠りが明けたあと、1体ごとに遅らせる時間（ms）
        internal static int StallSlowCalls = 16; // それを何体ぶん続けるか（3ms × 16 = 約 50ms ＝ フレーム1〜2枚ぶん）
        internal bool StallPeriodic = true;      // false なら周期的には撃たない（FireStallOnce で手動）
        internal int StallEveryFrames = 60;      // 何フレームごとに弾を込めるか
        internal int StallBackoff = 12;          // 末尾から何体手前を狙うか（起きたあと残りを数えて添字が溢れるように）
        internal float StallSeconds = 60f;
        private float _stallUntil;
        private int _stallFrame;

        private readonly ManualLogSource _log;
        private readonly System.Random _rng = new System.Random(12345);

        // ---- churn ----
        internal bool ChurnOn;
        internal int ChurnAlive = 300;
        internal int ChurnPerFrame = 15;
        internal float ChurnSpread = 12f;
        internal int Created;
        internal int Destroyed;
        private GameObject _root;
        private SkeletonDataAsset _asset;
        private MeshRenderer _templateRenderer;
        private Vector3 _templateScale = Vector3.one;
        private float _templateZ;
        private bool _builtin;                   // 雛形が埋め込みスケルトン（カメラの視界内に色付きで置き、テスト中と分かるようにする）
        internal bool UsingBuiltin { get { return _builtin; } }
        private Camera _cam;
        private readonly List<string> _animNames = new List<string>();
        private readonly List<GameObject> _alive = new List<GameObject>(512);
        private readonly List<GameObject> _reactivate = new List<GameObject>(32);

        // ---- hog ----
        internal volatile bool HogOn;
        internal int HogThreads;            // 0 = ProcessorCount * 2
        internal float HogSeconds = 15f;
        private float _hogUntil;
        private int _hogCount;
        private static long _hogSink;

        internal StressTools(ManualLogSource log)
        {
            _log = log;
        }

        // ------------------------------------------------------------------
        // churn
        // ------------------------------------------------------------------
        internal string ToggleChurn()
        {
            if (ChurnOn) { StopChurn(); return "churn: OFF   作成 " + Created + " / 破棄 " + Destroyed; }

            // 雛形は場面で一番アニメの多いスケルトン（電話や看板より使い魔を選ばせる）。一度きりなので探索コストは許容
            SkeletonAnimation template = null;
            int bestScore = -1;
            SkeletonAnimation[] all = UnityEngine.Object.FindObjectsByType<SkeletonAnimation>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                SkeletonAnimation sa = all[i];
                if (sa == null || sa.SkeletonDataAsset == null || sa.Skeleton == null) { continue; }
                Spine.SkeletonData d = sa.Skeleton.Data;
                int score = (d != null ? d.Animations.Count * 1000 + d.Bones.Count : 0);
                if (score > bestScore) { bestScore = score; template = sa; }
            }
            if (template == null || template.SkeletonDataAsset == null)
            {
                // タイトル画面など、場面にスケルトンが無いときは埋め込みの最小スケルトンを雛形にする
                _asset = BuiltinSkeleton.Get(_log);
                if (_asset == null)
                {
                    return "churn: 実行不可   スケルトンを用意できない";
                }
                _templateRenderer = null;
                _templateScale = Vector3.one;
                _templateZ = 0f;
                _builtin = true;
            }
            else
            {
                _asset = template.SkeletonDataAsset;
                _templateRenderer = template.GetComponent<MeshRenderer>();
                _templateScale = template.transform.lossyScale;
                _templateZ = template.transform.position.z;
                _builtin = false;
            }

            _animNames.Clear();
            Spine.SkeletonData data = _asset.GetSkeletonData(true);
            if (data != null)
            {
                foreach (Spine.Animation a in data.Animations) { _animNames.Add(a.Name); }
            }
            if (_animNames.Count == 0)
            {
                return "churn: 実行不可   " + _asset.name + " にアニメ無し";
            }

            _root = new GameObject("LwfSpineStress");
            ChurnOn = true;
            string src = _builtin ? "埋め込み" : _asset.name;
            _log.LogInfo("[stress] churn on: template=" + (_builtin ? "builtin" : _asset.name) + ", anims=" + _animNames.Count + ", alive=" + ChurnAlive + ", perFrame=" + ChurnPerFrame);
            return "churn: ON   " + src + "   " + ChurnAlive + " 体   毎フレーム " + ChurnPerFrame;
        }

        internal void StopChurn()
        {
            ChurnOn = false;
            _alive.Clear();
            _reactivate.Clear();
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);   // 子も一緒に消える
                _root = null;
            }
        }

        // ------------------------------------------------------------------
        // stall
        // ------------------------------------------------------------------
        internal string ToggleStall()
        {
            if (StallOn) { StopStall(); return "stall: OFF   発動 " + StallFired; }
            StallOn = true;
            _stallUntil = Time.unscaledTime + StallSeconds;
            _stallFrame = 0;
            StallArmed = 0;
            _log.LogInfo("[stress] stall on: every " + StallEveryFrames + " frames sleep one worker task " + StallMs + " ms, auto-off in " + StallSeconds + " s");
            return "stall: ON   " + StallEveryFrames + " フレームごと " + StallMs + " ms   " + StallSeconds + " 秒で停止";
        }

        internal void StopStall()
        {
            StallOn = false;
            StallArmed = 0;
            StallTarget = null;
            AnimStallArmed = 0;
            AnimStallTarget = null;
            _hammer.Clear();
            _nextIsAnim = false;
        }

        /// <summary>次の LateUpdate で狙いのスケルトン（無ければ最初のワーカー呼び出し）を1回眠らせる。StallOn のときだけ効く。</summary>
        internal void FireStallOnce()
        {
            if (_nextIsAnim && FireAnimStallOnce()) { _nextIsAnim = false; return; }
            _nextIsAnim = true;
            FireMeshStallOnce();
        }

        /// <summary>メッシュ生成側（LateUpdate）のワーカーを次のフレームで眠らせる。</summary>
        internal void FireMeshStallOnce()
        {
            StallTarget = PickStallTarget();
            StallArmed = 1;
            IncidentLog.Note("stress: mesh stall armed");
        }

        /// <summary>
        /// アニメ更新側（Update）のワーカーを次のフレームで眠らせ、目覚めたあとの範囲をメインスレッドから叩く相手として記録する。
        /// 本体が 1 秒で見捨てて進むと、目覚めたワーカーとメインの SetAnimation が同じ AnimationState を触る
        /// （0.25.0 の GetMix: from cannot be null の筋書き）。
        /// </summary>
        internal bool FireAnimStallOnce()
        {
            List<SkeletonAnimationBase> list = LateUpdateGuard.RegisteredAnimationList;
            if (list == null || list.Count == 0) { return false; }
            int idx = list.Count - 1 - Math.Max(0, StallBackoff);
            if (idx < 0) { idx = 0; }
            AnimStallTarget = list[idx];
            _hammer.Clear();
            for (int i = idx; i < list.Count; i++) { _hammer.Add(list[i]); }
            _hammerUntil = Time.unscaledTime + StallMs / 1000f + 0.8f;   // 眠り + 目覚めたあとしばらく
            AnimStallArmed = 1;
            IncidentLog.Note("stress: anim stall armed (" + _hammer.Count + " targets)");
            return true;
        }

        /// <summary>hammer: 記録したスケルトンの AnimationState をメインスレッドから毎フレーム叩く。</summary>
        private void HammerTick()
        {
            if (_hammer.Count == 0 || Time.unscaledTime >= _hammerUntil) { _hammer.Clear(); return; }
            for (int i = 0; i < _hammer.Count; i++)
            {
                SkeletonAnimation sa = _hammer[i] as SkeletonAnimation;
                if (sa == null || sa.AnimationState == null) { continue; }
                try
                {
                    sa.AnimationState.SetAnimation(0, _animNames[_rng.Next(_animNames.Count)], true);
                    sa.AnimationState.AddEmptyAnimation(0, 0.1f, 0f);
                    sa.AnimationState.SetEmptyAnimation(1, 0.05f);
                    sa.AnimationState.ClearTrack(1);
                }
                catch (Exception e)
                {
                    HammerExceptions++;
                    // Spine 由来として集計に載せる（ワーカーとメインの競合で出た例外）
                    Debug.LogError("[LWF FPS Boost] Spine AnimationState threw on main thread: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// 眠らせる相手を選ぶ。リスト末尾から StallBackoff 体手前。
        /// 本体の実装ではリスト全体がワーカー担当なので、末尾＝最後のタスク。
        /// 最後のタスクが古いカウンタを進めると添字がリストの長さを超える。
        /// </summary>
        private SkeletonRenderer PickStallTarget()
        {
            List<ISkeletonRenderer> list = LateUpdateGuard.RegisteredRendererList;
            if (list == null || list.Count == 0) { return null; }
            int end = list.Count;
            int idx = end - 1 - Math.Max(0, StallBackoff);
            if (idx < 0) { idx = 0; }
            for (int i = idx; i >= 0; i--)
            {
                SkeletonRenderer sr = list[i] as SkeletonRenderer;
                if (sr != null) { return sr; }
            }
            return null;
        }

        /// <summary>毎フレーム（プラグインの Update から）。</summary>
        internal void Tick()
        {
            if (StallOn)
            {
                if (Time.unscaledTime >= _stallUntil)
                {
                    StopStall();
                    _log.LogInfo("[stress] stall auto-off after " + StallSeconds + " s, fired " + StallFired);
                }
                else if (StallPeriodic && ++_stallFrame % Math.Max(1, StallEveryFrames) == 0)
                {
                    FireStallOnce();
                }
                HammerTick();
            }
            if (HogOn && Time.unscaledTime >= _hogUntil)
            {
                StopHog();
                _log.LogInfo("[stress] hog auto-off after " + HogSeconds + " s");
            }
            if (!ChurnOn) { return; }
            if (_root == null) { StopChurn(); return; }   // シーン切替で消えた

            // 前フレームに SetActive(false) したものを戻す（登録し直しの経路）
            for (int i = 0; i < _reactivate.Count; i++)
            {
                GameObject go = _reactivate[i];
                if (go != null) { go.SetActive(true); }
            }
            _reactivate.Clear();

            // 古いものから捨てる
            int toDestroy = _alive.Count >= ChurnAlive ? ChurnPerFrame : 0;
            for (int i = 0; i < toDestroy && _alive.Count > 0; i++)
            {
                GameObject go = _alive[0];
                _alive.RemoveAt(0);
                if (go != null) { UnityEngine.Object.Destroy(go); Destroyed++; }
            }

            // 足りないぶんを作る（立ち上げは速く、以後は入れ替え分だけ）
            int toCreate = Math.Min(ChurnPerFrame * 4, ChurnAlive - _alive.Count);
            Camera cam = Camera.main;
            Vector3 center = cam != null ? cam.transform.position : Vector3.zero;
            center.z = _templateZ;
            _cam = cam;
            for (int i = 0; i < toCreate; i++)
            {
                GameObject go = CreateOne(center);
                if (go == null) { break; }
                _alive.Add(go);
                Created++;
            }

            // 生きているものの一部でアニメを切り替える（メインスレッドから AnimationState を触る）
            int switches = Math.Min(_alive.Count, Math.Max(1, ChurnPerFrame / 2));
            for (int i = 0; i < switches; i++)
            {
                GameObject go = _alive[_rng.Next(_alive.Count)];
                if (go == null || !go.activeSelf) { continue; }
                SkeletonAnimation sa = go.GetComponent<SkeletonAnimation>();
                if (sa == null || sa.AnimationState == null) { continue; }
                try
                {
                    sa.AnimationState.SetAnimation(0, _animNames[_rng.Next(_animNames.Count)], true);
                    if (_rng.Next(4) == 0) { sa.AnimationState.AddEmptyAnimation(0, 0.1f, 0.2f); }
                }
                catch (Exception e)
                {
                    _log.LogWarning("[stress] SetAnimation threw: " + e.Message);
                }
            }
        }

        private GameObject CreateOne(Vector3 center)
        {
            try
            {
                GameObject go = new GameObject("stress");
                go.transform.SetParent(_root.transform, false);
                if (_builtin && _cam != null)
                {
                    // 埋め込みはカメラの視界内にばらまく（タイトル画面で「テスト中」と分かるように）
                    float depth = _cam.orthographic ? Mathf.Max(1f, _cam.nearClipPlane + 1f) : Mathf.Max(2f, _cam.nearClipPlane + 8f);
                    Vector3 v = new Vector3(0.05f + (float)_rng.NextDouble() * 0.9f, 0.08f + (float)_rng.NextDouble() * 0.84f, depth);
                    go.transform.position = _cam.ViewportToWorldPoint(v);
                    go.transform.rotation = _cam.transform.rotation;
                    go.transform.localScale = Vector3.one * 0.35f;
                }
                else
                {
                    go.transform.position = new Vector3(
                        center.x + (float)(_rng.NextDouble() * 2.0 - 1.0) * ChurnSpread,
                        center.y + (float)(_rng.NextDouble() * 2.0 - 1.0) * ChurnSpread,
                        center.z);
                    go.transform.localScale = _templateScale;
                }

                var comps = SkeletonAnimation.AddToGameObject(go, _asset, true);
                SkeletonAnimation sa = comps.skeletonAnimation;
                if (sa == null || sa.Skeleton == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return null;
                }
                MeshRenderer mr = go.GetComponent<MeshRenderer>();
                if (mr != null && _templateRenderer != null)
                {
                    mr.sortingLayerID = _templateRenderer.sortingLayerID;
                    mr.sortingOrder = _templateRenderer.sortingOrder;
                }
                SkeletonRenderer skr = go.GetComponent<SkeletonRenderer>();
                if (skr != null) { skr.updateWhenInvisible = UpdateMode.FullUpdate; }   // 画面外でもメッシュ生成を回す
                sa.timeScale = 0.5f + (float)_rng.NextDouble();
                sa.AnimationState.SetAnimation(0, _animNames[_rng.Next(_animNames.Count)], true);
                if (_builtin)
                {
                    // 色をばらつかせる（白い四角の鎖だと何が起きているか分かりにくい）
                    Color c = Color.HSVToRGB((float)_rng.NextDouble(), 0.6f, 1f);
                    sa.Skeleton.SetColor(c);   // spine-unity の拡張メソッド（SkeletonExtensions）
                }

                // 更新完了コールバック（処理中・メインスレッド）で、ときどき自分を無効化する。
                // isProcessing 中の Unregister は遅延リストに回るので、その経路を叩く
                sa.UpdateComplete += OnUpdateComplete;               // アニメ更新中（isProcessingAnimations）
                sa.OnMeshAndMaterialsUpdated += OnUpdateComplete;    // メッシュ反映中（isProcessingRenderers）
                return go;
            }
            catch (Exception e)
            {
                _log.LogWarning("[stress] spawn threw: " + e.Message);
                return null;
            }
        }

        private void OnUpdateComplete(ISkeletonRenderer renderer)
        {
            if (!ChurnOn || _rng.Next(200) != 0) { return; }
            MonoBehaviour c = renderer.Component;
            if (c == null || c.gameObject == null || !c.gameObject.activeSelf) { return; }
            c.gameObject.SetActive(false);
            _reactivate.Add(c.gameObject);
        }

        // ------------------------------------------------------------------
        // hog
        // ------------------------------------------------------------------
        internal string ToggleHog()
        {
            if (HogOn) { StopHog(); return "hog: OFF"; }

            int n = HogThreads > 0 ? HogThreads : Environment.ProcessorCount * 2;
            HogOn = true;
            _hogCount = n;
            _hogUntil = Time.unscaledTime + HogSeconds;
            for (int i = 0; i < n; i++)
            {
                Thread t = new Thread(HogLoop);
                t.IsBackground = true;
                t.Priority = System.Threading.ThreadPriority.AboveNormal;
                t.Name = "LwfSpineStressHog" + i;
                t.Start();
            }
            _log.LogInfo("[stress] hog on: threads=" + n + ", auto-off in " + HogSeconds + " s");
            return "hog: ON   " + n + " threads   " + HogSeconds + " 秒で停止";
        }

        internal void StopHog()
        {
            HogOn = false;   // ループが抜ける。join は要らない（background）
            _hogCount = 0;
        }

        private void HogLoop()
        {
            long x = 0;
            while (HogOn)
            {
                for (int i = 0; i < 100000; i++) { x += i ^ (x >> 3); }
            }
            Interlocked.Exchange(ref _hogSink, x);
        }

        // ------------------------------------------------------------------
        internal void StopAll()
        {
            StopStall();
            StopHog();
            StopChurn();
        }

        internal string Status()
        {
            string churn = ChurnOn
                ? "churn ON alive " + _alive.Count + " (作成 " + Created + " / 破棄 " + Destroyed + ")"
                : "churn OFF" + (Created > 0 ? " (作成 " + Created + " / 破棄 " + Destroyed + ")" : "");
            string stall = (StallOn ? "stall ON " : "stall OFF ")
                + "発動 " + StallFired + "（anim " + AnimStallFired + "）  叩いて例外 " + HammerExceptions
                + (StallOn ? "  残り " + Math.Max(0f, _stallUntil - Time.unscaledTime).ToString("0") + "s" : "");
            string hog = HogOn
                ? "hog ON " + _hogCount + "本 残り " + Math.Max(0f, _hogUntil - Time.unscaledTime).ToString("0") + "s"
                : "";
            return churn + "   " + stall + (hog.Length > 0 ? "   " + hog : "");
        }
    }
}
