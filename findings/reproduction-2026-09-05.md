# 0.25.0 の例外の再現と、待機パスによる回避の A/B（2026-09-05）

ゲーム ver0.27.0・spine-unity.dll は 0.21 以降と同一（b970de3325ff…）。
`mod/LwfSpineThreading.dll`（当時の名前。2026-09-06 に LwfFpsBoost へ改名）v2.0.0 の自動 A/B（Alt+F9）で取った結果。

## 結論

- 本体の実装（上流の高速化パス `DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS`）は、
  **「ワーカーが1秒以上止まる → メインが見捨てて進む → その直後にスケルトンが大量に登録解除される」**
  の順序で `ArgumentOutOfRangeException: Index was out of range` を起こす。
  ワーカー側（`LateUpdateSkeletonsAsyncImpl`）とメイン側（`LateUpdateAsync`）の両方で出て、
  メイン側の1件でゲームがタイトルへ戻された。**メイン側の位置は 0.25.0 で報告された例外と一致する。**
- 待機パス（`mod/LateUpdateWaitPath.cs`。上流の `!DONT_WAIT…` 側を Harmony Prefix で移植）は、
  同じ順序を同じ回数踏んでも何も起きない。ワーカーが目覚めるまで待つので「見捨てる」が存在しない。

## 結果（Player.log の `[ab]` 行）

```
A[待機パス]   眠らせ 21 / 待機側timeout 21 / 上流timeout  0 / out of range 0 / Spine err  0 / worker exc 0  35.34 ms
B[本体の実装] 眠らせ 23 / 待機側timeout  0 / 上流timeout 23 / out of range 2 / Spine err 25 / worker exc 0  35.55 ms
```

B の例外（抜粋）:

```
ArgumentOutOfRangeException: Index was out of range. ...
  at System.Collections.Generic.List`1[T].get_Item
  at Spine.Unity.SkeletonUpdateSystem.LateUpdateSkeletonsAsyncImpl (SkeletonUpdateRange range, Int32 threadIndex)   ← ワーカー。catch 節でもう一度 [r] を引くので二重に落ち、スレッドが死ぬ
  at LockFreeWorkStealingWorkerPool`1[T].WorkerLoop

ArgumentOutOfRangeException: Index was out of range. ...
  at System.Collections.Generic.List`1[T].get_Item
  at Spine.Unity.SkeletonUpdateSystem.LateUpdateAsync                                                            ← メイン。0.25.0 の報告と同じ位置。ここでタイトルへ
  at Spine.Unity.SkeletonUpdateSystem.LateUpdate
```

## Postfix 版（v2.1.0・`mod/LateUpdateGuard.cs`）でも同じ結果（同日）

移植版（Prefix で `LateUpdateAsync` を丸ごと差し替え）は Spine Runtimes License 上配れないので、
本体の実装をそのまま走らせ、「見捨てて戻ってきた」直後に Postfix で全ワーカーの完了を待つ方式に書き直した。
本体のコードは含まない。移植版は `LateUpdateWaitPath-port-2026-09-05.cs` として記録だけ残す。

```
A[見張りあり] 眠らせ 21 / 見張りが待った 21 / 上流timeout 21 / out of range 0 / Spine err 21 / 諦め 0  35.45 ms
B[見張りなし] 眠らせ 23 / 見張りが待った  0 / 上流timeout 23 / out of range 1 / Spine err 24 / 諦め 0  32.57 ms
```

- A でも上流の `Internal threading logic error`（見捨てた記録）は出る。見張りが毎回それを捕まえて待つので例外にならない
- 見張りの毎フレームの負担は、タスク数ぶんの int 比較（120 回程度）だけ
- B は今回ワーカー側の 1 件のみ（メイン側の例外は出るかどうかが時間次第）。差は明確

## タイトル画面でも同じ（同日・埋め込みスケルトン）

場面にスケルトンが無いタイトル画面では、MOD が組み立てる最小スケルトン（`mod/BuiltinSkeleton.cs`。骨 3 本・4×4 の白い四角・アニメ 3 種、
Spine/Skeleton シェーダ）を雛形にして画面外で回す。結果は同じ。

```
自己診断（FullAB=false）: OK: stall 21 回を見張りが 21 回待ち、例外 0
A[見張りあり] 眠らせ 19 / 見張りが待った 19 / 上流timeout 19 / out of range 0 / Spine err 19 / 諦め 0  29.05 ms
B[見張りなし] 眠らせ 23 / 見張りが待った  0 / 上流timeout 23 / out of range 1 / Spine err 24 / 諦め 0  30.60 ms
```

- 例外の再現に骨の数や見た目は関係ない。鍵は「見捨て → 目覚め → 一斉解除」の順序だけ
- これで起動確認・穴が塞がっているかの確認・穴が実在することの確認が、セーブを開かずにタイトルで完結する

## アニメ更新側（GetMix: from cannot be null）への対処と、再現の試み（2026-09-06）

- 対処: `WaitForThreadUpdateTasks` に Postfix（`mod/UpdateGuard.cs`）。戻ってきた時点で `updateDone[t]` が立っていなければ揃うまで待つ
- 再現の道具: `SkeletonAnimationBase.UpdateInternalSplit` の Prefix でワーカーを 1.2 秒眠らせ、目覚めたあと 30 体を 10 ms ずつ遅らせつつ、
  その範囲のスケルトンにメインから `SetAnimation` / `AddEmptyAnimation` / `SetEmptyAnimation` / `ClearTrack` を毎フレーム叩く。周期 stall はメッシュ側と交互

```
A[guard on]  stalls 20 / guard waits 20 / upstream timeouts 20 / out of range 0 / null (GetMix) 0 / give-ups 0
B[guard off] stalls 22 / guard waits  0 / upstream timeouts 57 / out of range 1 / null (GetMix) 0 / give-ups 0
```

- アニメ側の見捨て（`ran into a timeout`）は B で 19 行。見捨てられたスレッドが遅いまま次のタスクも拾うので連鎖し、上流 timeout の合計は 57
- A では全部の見捨てを Postfix が捕まえた（Update 側 9・LateUpdate 側 11）
- **GetMix の null は再現せず**。窓は「メインが `tracks[i]` から `.next` を辿る最中に、ワーカーの `Drain` がそのエントリを `Pool` に返す」か
  「スレッド安全でない `Pool<TrackEntry>` に両側から同時に出し入れして壊れる」の数マイクロ秒で、メッシュ側のように順序で作れる性質ではない。
  実戦の報告は、見捨てが長く続く間に入力とワーカーが何千回も重なった結果と見る
- 判断: 再現はここまで。対処は前提条件（メインが進んでいる間にワーカーが AnimationState を触る）を消すもので、
  B で 57 回起きた見捨てが A で 0 回になっていることを根拠にする。高負荷テストにはアニメ側の stall を残し、Postfix が働くこと（待った回数）を毎回確かめる

## 事故記録の実例（2026-09-06）

`incident-sample-2026-09-06.log` は、タイトル画面で回した A/B の B フェーズで出た `Index was out of range` を IncidentLog が記録したもの。
実プレイの事故ではない。文脈 40 行に stall の周期と最後の一斉解除が並び、その 0.2 秒後に例外、という筋書きがそのまま読める。
B では上流が `skeletonAnimations never called UpdateInternal before!` も連発する（見捨てたあとメインが分割実行の続きに入り、未開始のスケルトンに当たる）。A では出ない。

## 実プレイの記録の実例（2026-09-06）

`games-sample-2026-09-06.log` は GameReport が書いた実プレイ 1 回ぶん（特別任務・30.7 分・スケルトン最大 103 体）。
平均 16.7 ms（VSync 60fps）、最悪 200 ms が 4 回、見捨て 0・回避処理の出番 0・例外 0。終了理由 `DefeatTimeUp` と run の ID は本体の RunHistory から取れている。

## 性能（自動 性能比較・Alt+F10・同日）

待機パス込みの v2 でも、マルチスレッドの効果は v1 の計測と同程度に残る。
churn 1500 体（モモコ）で負荷を揃え、VSync とフレームレート上限を外して ON → OFF → ON 各 20 秒（最初の 2 秒は捨てる）。

```
ON#1  21.86 ms  45.75 fps  (915 frames)
OFF   51.12 ms  19.56 fps  (391 frames)
ON#2  22.03 ms  45.4  fps  (908 frames)
→ マルチスレッドで fps 2.33 倍
```

- 前後の ON が 0.2 ms しか違わないので、負荷は安定していた
- churn 600 体・VSync 有効のまま測ると ON が 16.7 ms（60 fps 上限）に張り付いて 1.45 倍にしか見えない。測るときは上限を外すこと

## 手順（自動 A/B が中でやっていること）

1. **churn**（F7）: 場面で一番アニメの多いスケルトン（モモコ・112 種）のデータで Spine のスケルトンだけを 600 体作り、
   毎フレーム 15 体を捨てて作り直す。一部はアニメ切替と、更新完了コールバック内の SetActive(false) も混ぜる
2. **stall**（Alt+F7）: `SkeletonRenderer.LateUpdateImplementation` の Prefix で、60 フレームごとに
   「ワーカー担当範囲の末尾から 12 体手前」のスケルトンの番だけ 1.2 秒眠る。目覚めたスレッドは以後 16 体を 1 体 3ms ずつ遅らせる
   （古い加算をメイン側の回収ループに重ねるため）
3. 各フェーズの**締め**: stall を 1 発撃ち、ワーカーが眠っている内側で churn を止めて 600 体を一斉に登録解除。5 秒待って集計
4. A は待機パス有効、B は `LateUpdateWaitPath.Active=false`（Prefix が本体に任せる）。終了後は A に戻す

## 何が起きているか（B）

- メインは `lateUpdateWorkAvailable.WaitOne(1000)` が切れると `Internal threading logic error: exited LateUpdate loop after timeout!` を出して**進む**
- 眠っていたスレッドの待ち行列は他のスレッドが横取りするので、次フレームの同じ添字のタスクは別スレッドで走る（`LockFreeWorkStealingWorkerPool.WorkerLoop` の Steal）
- 一斉解除で `skeletonRenderers` が縮んだあと、目覚めたスレッドは**古い範囲**の `r` で `instance.skeletonRenderers[r]` を引く → ワーカー側の例外
- 同時に古い加算が `skeletonsLateUpdatedAtTask[t]` に乗り、メインの回収ループが `mainThreadProcessedAtTask[t] + rangeStart` を
  リストの長さの先まで進める → メイン側の例外（報告と同じ位置）
- 眠りが 1.2 秒でも、目覚めたあとの加算が 1〜2ms で終わると回収ループに重ならず溢れない。
  最初の 2 回の試行（計 32 回の timeout）で 0 件だったのはこのため。加算を引き延ばして初めて出た

## 効かなかったもの

- **hog**（優先度 AboveNormal の空回りスレッド ×32）: Windows の飢餓防止に負けて、ワーカーを 1 秒止められなかった
- **先頭側のタスクを眠らせる**: 古い加算の添字がリストの中ほどを指すので、他人の描画を触るだけで例外にならない

## 実戦での対応物

「一瞬の引っかかり（GC・シェーダコンパイル・読み込み）の直後に、使い魔をまとめて売る／解体する」。
どちらも通常プレイで起きる。0.25.0 の報告が「入力操作の直後」だったのとも矛盾しない。

## 上流への報告に使える形

- 再現: `SkeletonUpdateSystem.LateUpdateAsync` でワーカー1つが 1 秒以上遅れ、その直後に `skeletonRenderers` が縮むと
  `mainThreadProcessedAtTask[t] + rangeStart` が `skeletonRenderers.Count` を超える
- 直し: `#define DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS` を切る（待機パスへ）か、
  タイムアウト後に進むならカウンタ配列を作り直して古い加算を無効化する。
  ついでに `LateUpdateSkeletonsAsyncImpl` の catch 節で `instance.skeletonRenderers[r]` を引き直すのをやめる（二重に落ちてスレッドが死ぬ）
