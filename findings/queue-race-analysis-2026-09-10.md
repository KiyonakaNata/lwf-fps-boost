# タスクキューの競合 — 見捨て 0 回で出た `GetMix: from cannot be null`（2026-09-10）

ゲーム ver0.27.0・spine-unity.dll は 0.21 以降と同一（b970de3325ff…）。Mod v2.1.1（回避処理 2 つあり）で実プレイ中に出た。

## 事故の記録

`LwfFpsBoost-incidents.log` 2026-09-10 12:05:39。特別任務 9.6 分・スケルトン最大 70 体・test=none。

```
guard: lateUpdate waits=0 giveups=0  update waits=0 giveups=0  registered mesh=65 anim=65
ArgumentNullException: from cannot be null.
  Spine.AnimationStateData.GetMix (Animation from, Animation to)
  Spine.AnimationState.NewTrackEntry (int trackIndex, Animation animation, bool loop, TrackEntry last)
  Spine.AnimationState.SetAnimation (int trackIndex, Animation animation, bool loop)
  Animator.Spine.Momoko.PlayerAnimationController.SetAnim (…)
  Input.PlayerAction.PlayerInput.Update ()
```

- **見捨て（上流 timeout）0 回・回避処理の出番 0 回**。Player.log にも `ran into a timeout` は無い
- これまでの筋書き（見捨てられたワーカーが AnimationState を触り続ける）では説明できない
- ゲームはそのままタイトルへ戻された（`end (Interrupted)`）

## 例外が意味すること

`NewTrackEntry` は `last.animation` を `GetMix` に渡す。`last` は `tracks[trackIndex]`（か、その `mixingFrom`）で、
**トラックに繋がったままの TrackEntry の `animation` が null** だったことになる。

`TrackEntry.animation` を null にするのは `TrackEntry.Reset()` だけで、これは `Pool<TrackEntry>.Free()` からしか呼ばれない。
Free は `EventQueue.Drain()` の End / Dispose の処理で呼ばれる。つまり **プールに返された（＝もう使っていないはずの）エントリが、
まだトラックに繋がっていた**。単一スレッドの Spine ではこの状態は作れない（End を積む場所は必ずトラックから外した直後）。

同じ AnimationState を 2 スレッドが同時に `Update()` すると簡単に作れる。

- 両方が `current.trackLast >= trackEnd` を見て `queue.End(current)` を 2 度積む → Drain で 2 度 Free → プール（Stack）に同じエントリが 2 つ
- 以後 `Obtain()` が同じエントリを 2 回返す → 2 つのトラックが同じエントリを共有 → 片方が End で Free（Reset）→ もう片方のトラックには `animation = null` のエントリが残る
- そのトラックに次の `SetAnimation` / `AddAnimation`（`AddEmptyAnimation` も同じ）が来た瞬間に `GetMix` で落ちる

壊れてから落ちるまでに時間差があるので、事故の瞬間のスタックには競合の相手が写らない。

## どこで 2 スレッドが重なるか — worker pool の deque

回避処理は「メインが待たずに進む」経路を全部塞いでいる（見捨て 0 回）ので、重なりは **worker pool の中**で起きている。

`LockFreeWorkStealingWorkerPool`（上流 4.3-beta）の構造:

- スレッドごとに `LockFreeWorkStealingDeque<Task>`（Chase-Lev 型）を 1 本持つ
- **投入**: `EnqueueTask` → `deque.PushTop(task)`。呼ぶのは**メインスレッド**
- **取り出し**: `WorkerLoop` → `deque.Pop()`。呼ぶのは**その deque の持ち主のワーカー**
- **横取り**: 自分の deque が空になったら他の deque を `Steal()` で巡回。呼ぶのは**他のワーカー**
- ワーカーは `AutoResetEvent` で起こされる（`AllowTaskProcessing`）。イベントが立ったままなら `WaitOne` は素通り

deque 側の前提（上流ソースの注釈をそのまま）:

> Requires that Push and Pop are called from the same thread.
> PushTop … must only be called before any other thread calls Push, Pop or Steal.

pool 側もそれを知っている（`AllowTaskProcessing` の注釈）:

> This limitation comes from LockFreeWorkStealingDeque requiring the same thread calling Push and Pop, which would not be the case here.

つまり **PushTop（メイン）と Pop/Steal（ワーカー）が重ならないことを、イベントの順序だけで担保しようとしている**。ところが:

1. ワーカーはタスクの最後で `updateDone[t].Set()` してから、ループに戻って `Pop()` → 空なら他の deque を `Steal()` で一周する
2. メインは `updateDone` が揃った瞬間に次のフェーズへ進み、`PushTop` を始める（分割実行なら同じフレーム内で何度も。LateUpdate の DONT_WAIT パスも、全部終わったと見た瞬間に抜ける）
3. ワーカーが忙しい間に `AllowTaskProcessing` が来ていると AutoResetEvent が立ったままで、次の `WaitOne` を素通りして 1 と同じ巡回にすぐ入る

→ **同じ deque に対して PushTop と Pop / Steal が毎フレーム何度も重なりうる**。

## 重なると何が壊れるか（Pop と PushTop の例）

deque に要素が 1 つ（`top = T-1, bottom = T`）。持ち主が Pop、メインが 2 つ目を PushTop。

| 持ち主ワーカー `Pop` | メイン `PushTop` |
|---|---|
| `b = bottom - 1 = T-1; bottom = T-1` | |
| `t = top = T-1; size = 0` | `t = top = T-1` を読む |
| `o = Get(T-1)` = X | |
| `CAS(top, T-1 → T)` 成功。X を返す | |
| `bottom = T` | `Put(T-2, Y); top = T-2`（**CAS の結果を素の書き込みで上書き**） |

結果 `top = T-2, bottom = T` で deque には Y と **X がもう一度**入っている。X は 2 度走る。

- **Update のタスクなら**: 同じ範囲のスケルトンの `AnimationState.Update / Apply` を 2 スレッドが同時に回す → 上の壊れ方。しかも `updateDone` は 1 度目の完了で立つので、メインは待たずに進み、**2 度目の実行がゲームの `Update()`（`PlayerInput.Update` → `SetAnimation`）と同時に走る**
- **LateUpdate のタスクなら**: 2 度目の実行が `skeletonsLateUpdatedAtTask[t] = 0` でカウンタをリセットし、2 度加算する → メインの回収ループが `mainThreadProcessedAtTask[t] + rangeStart` を担当数の 2 倍まで進めて `skeletonRenderers[r]` が範囲外。**0.25.0 で報告された位置と同じ**で、こちらは 1 秒の停止が無くても出る

Steal と PushTop の重なりも同じ形（Steal の CAS を PushTop の `top = t-1` が上書き）。

Pop と PushTop が deque の空の状態で重なると「投入したタスクが消える」こともあり、そのときは `updateDone` が立たず 1 秒の timeout として見える（回避処理が待つ側で捕まえる）。

## 直し方（Mod v2.2.0・`mod/QueueGuard.cs`）

deque の `PushTop` / `Push` / `Pop` / `Steal` を Harmony の Prefix（`Monitor.Enter`）と Finalizer（`Monitor.Exit`）で囲み、
**その deque インスタンスの lock で直列化する**。中身のアルゴリズムはそのまま。本体のコードは差し替えない。

- 1 回の操作は数十 ns。フレームあたりの回数はタスク数＋横取りの空振り（16 スレッドで数百回）で、負担は測れない程度
- lock が塞がっていた回数（**contended**）を数える。直列化しなければそのまま重なっていた回数で、競合が実在する証拠になる
- 同じタスクが走っている最中にもう一度始まったら数える検出（**double_run**）を `UpdateSkeletonsAsyncSplitImpl` / `UpdateSkeletonsAsyncImpl` / `LateUpdateSkeletonsAsyncImpl` に掛ける。lock ありなら 0 のはず。cfg `9. Developer/QueueLock=false` で lock だけ切って回すと、この数が実際に増えるかで裏が取れる
- 数字は HUD の詳細（Shift+F11）「タスクキュー」行、事故記録のヘッダ `queue:` 行、工場ごとの記録の `queue:` 行に出る

deque と pool は generic（`LockFreeWorkStealingDeque<LockFreeWorkStealingWorkerPool<SkeletonUpdateRange>.Task>`）なので、
型は `SkeletonUpdateSystem.workerPool` のフィールド型 → `_taskQueues` の要素型と辿って実行時に決め、pool が別物なら何もしない。

## 狙って起こす（F9 高負荷テストに追加・同日）

競合の窓は「ワーカーが Pop / Steal を回している最中にメインが PushTop する」瞬間なので、テスト中は先頭 4 本のワーカーの
起床イベントを Pop のたびに立て直し、Pop → 全 deque を Steal → Pop … と回り続けさせる（`QueueGuard.SpinWorkers`）。
実戦で数十分に 1 回の重なりが、毎フレーム何百回になる。合否は「contended > 0（重なりを実際に作った）かつ double_run = 0」。

タイトル画面・埋め込みスケルトン 600 体・45 秒 ×2（`FullAB=true` で B も回したもの。Player.log の `[test]` 行）:

```
A[guard on]  stalls 20 / guard waits 20 / upstream timeouts 20 / out of range 0 / null 0 / spine errors 20 / give-ups 0 / queue contended 9688 / double-run 0       36.91 ms
B[guard off] stalls 21 / guard waits  0 / upstream timeouts 53 / out of range 1 / null 0 / spine errors 63 / give-ups 0 / queue contended    0 / double-run 384123  34.95 ms
```

- B ではワーカー内で `Dictionary: An item with the same key has already been added`（`AnimationState.AnimationsChanged` → `ComputeHold`。同じ AnimationState を 2 スレッドが同時に `Apply` した結果）が出て、
  finale の一斉解除で `LateUpdateSkeletonsAsyncImpl` の out of range。テスト後にタイトルへ戻っても同じ out of range がワーカーから出続ける（壊れた deque はそのまま）ので、B を回したら再起動
- 初版の検出（走行中フラグを Postfix で下ろす）は A で 1 件誤認した。ワーカーが `updateDone.Set()` の直後に横取りされて Postfix が遅れると、次の周回の正当な開始を二重と数える。
  「投入回数（PushTop の Prefix）より開始回数（Impl の Prefix）が多い」方式に変えて 0 になった
- 16 本全部を回すと CPU が飽和してメインが遅れ、contended が 2.5 億になって意味を失う。4 本に絞り、contended は PushTop が待たされた回数だけ数える

## 本体側（spine-unity）での直し方

- 一番小さいのは上と同じ「deque の操作を lock で囲む」か、`PushTop` を捨てて**ワーカー自身が自分の deque に Push する**形にする（Chase-Lev の前提に戻す）
- あるいは `System.Collections.Concurrent.ConcurrentQueue<Task>` をスレッドごとに持ち、横取りは他の queue の `TryDequeue` で行う。どのスレッドから呼んでもよい
- `AutoResetEvent` を「フェーズ開始」の合図に使うなら、ワーカーが巡回を終えてから次の合図を待つ**バリア**が要る。今はワーカーの巡回が終わる前にメインが次を投入できる

## 経緯の訂正

- `threading-race-analysis.md`（2026-08-29）の「そんな窓は開いていない（アニメ更新は呼び出し内で待ち切る）」は、**pool の中で同じタスクが 2 度走る**経路を見ていなかった。`WaitForThreadUpdateTasks` が待つのは 1 度目の完了で、2 度目は待たれない
- `reproduction-2026-09-05.md` の「GetMix の null は再現せず」は、stall（1 秒の停止）で作れる競合ではないため。こちらは µs の重なりで、負荷が高いほど（ワーカーが忙しく AutoResetEvent が立ったままになりやすいほど）起きやすい
- 0.25.0 の `Index was out of range` は、stall 経路（再現済み）と、この経路（stall 不要）の両方で同じ位置に出る
