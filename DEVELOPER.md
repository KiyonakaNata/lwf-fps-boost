# LWF FPS Boost（FPS改善MOD）— ゲーム作者の方へ

Lazy Witch's Factory の作者に向けた説明です。この Mod が何を見つけ、どう避けていて、本体側ではどう直せるか、をまとめました。
Mod を触る人や spine-unity の利用者にも読める形にしてあります。

## 要旨

- spine-unity 4.3（beta）の `SkeletonUpdateSystem` にあるマルチスレッド更新は、**上流が既定で有効にしている高速化パスに競合バグがあり**、ver0.25.0 で本体が踏んだ `Index was out of range` はそこから出ています。ゲーム側のコードの問題ではありません
- 穴は 2 か所です。(a) `LateUpdateAsync` が 1 秒で「見捨てて」進む高速化パス、(b) worker pool の deque に**メインが投入・ワーカーが取り出し**を同時に掛けていて、同じタスクが 2 度走ることがある（§2b）。(b) は 1 秒の停止が無くても起き、0.25.0 のもう 1 件 `GetMix: from cannot be null` はこちらです
- 本体側の修正は 3 点で、どれも spine-unity の再ビルドで済みます（§4）
- この Mod は暫定の回避策です。spine-unity のコードは差し替えず、Harmony の Postfix 2 つで「見捨てる」を「待つ」に変え、deque の操作 4 つを lock で直列化しています（§5）

## 1. 調べ方について

- 調査と回避には **BepInEx 5 と HarmonyX を使っています**。ゲームのファイルは書き換えていません
- 上流 spine-unity 4.3-beta の `SkeletonUpdateSystem.cs` と `LockFreeWorkStealingWorkerPool.cs` を取り寄せて読み、出荷 DLL がどの `#define` でビルドされているかはリフレクションで確かめました（`WaitForThreadLateUpdateTasks` が無く、`skeletonsLateUpdatedAtTask` がある）
- 例外は自然発生を待たず、ワーカーを故意に遅らせて再現しました（§3）

## 2. 何が起きているか

上流 `SkeletonUpdateSystem.cs` の 45 行目に、既定で有効な `#define DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS // enabled improves performance a bit.` があります。
この分岐の `LateUpdateAsync` は、ワーカーの進捗カウンタ（`skeletonsLateUpdatedAtTask[t]`）を見ながら終わったスケルトンから順にメッシュを回収し、
進みが 1 秒止まると `Internal threading logic error: exited LateUpdate loop after timeout!` を出して**見捨てて戻ります**。

見捨てられたワーカーはそのあと目覚めて、古い担当範囲の添字で `skeletonRenderers[r]` を引き、古いタスク番号のカウンタを進めます。
そのあいだにスケルトンが大量に登録解除されてリストが縮んでいると、

- ワーカー側: `LateUpdateSkeletonsAsyncImpl` の `skeletonRenderers[r]` が範囲外。catch 節でもう一度 `[r]` を引くので二重に落ち、ワーカースレッドが死にます
- メイン側: 次フレームの回収ループが `mainThreadProcessedAtTask[t] + rangeStart` を進めすぎて範囲外。**0.25.0 で報告された位置はこちら**です

実戦での対応物は「一瞬の引っかかり（GC・シェーダコンパイル・読み込み）の直後に、使い魔をまとめて売る／解体する」です。

アニメ更新側（`UpdateAsync`）にも同じ癖があります。`WaitForThreadUpdateTasks` が 1 秒で切れると
`Waiting for updateDone on main thread ran into a timeout` を出して進み、ワーカーは AnimationState を更新し続けます。
0.25.0 のもう 1 件 `ArgumentNullException: from cannot be null`（`AnimationStateData.GetMix` ← `AddEmptyAnimation` ← `SetAnimHold` ← 入力コールバック）は、
その状態でメインスレッドが同じ AnimationState を触ったときに、プールへ返されたばかりの TrackEntry（`animation = null`）を見たか、
スレッド安全でない `Pool<TrackEntry>` に両側から出し入れした結果だと見ています（こちらは再現できていません。§9）。

詳細: [`../findings/threading-race-analysis.md`](findings/threading-race-analysis.md)

### 2b. もう 1 つの穴 — worker pool の deque（2026-09-10）

上の回避処理を 2 つとも入れた Mod v2.1.1 で、実プレイ中（見捨て 0 回）に `GetMix: from cannot be null` が出ました
（`NewTrackEntry` ← `SetAnimation` ← `PlayerAnimationController.SetAnim` ← `PlayerInput.Update`）。
「トラックに繋がったままの TrackEntry の `animation` が null」＝プールに返されたエントリがまだトラックにいる状態で、単一スレッドの Spine では作れません。

原因は `LockFreeWorkStealingWorkerPool` です。スレッドごとの deque（Chase-Lev 型）に、**メインスレッドが `PushTop` で投入し、持ち主のワーカーが `Pop`、他のワーカーが `Steal`** で取り出します。
deque 自身の注釈は「Push と Pop は同じスレッドから」「PushTop は他のスレッドが Push/Pop/Steal を呼ぶ前にだけ」で、pool 側の注釈もそれを認めたうえでイベントの順序に頼っています。
ところがワーカーはタスクを終える（`updateDone.Set()`）と、そのまま `Pop` → 他の deque を `Steal` で一周しに行き、メインは `updateDone` が揃った瞬間に次のフェーズの `PushTop` を始めるので、毎フレーム重なりえます。
重なると `Pop` / `Steal` の CAS を `PushTop` の `top = t-1` が上書きし、**取り出したはずのタスクが deque に残って 2 度走ります**。

- Update 側: 同じ AnimationState を 2 スレッドが同時に `Update()` → `queue.End` が二重 → `Pool.Free` が二重 → 以後 `Obtain` が同じ TrackEntry を 2 回返す → 片方が Free されるともう片方のトラックに `animation = null` が残る → 次の `SetAnimation` / `AddEmptyAnimation` で `GetMix`。壊れてから落ちるまで時間差があるので、スタックには相手が写りません
- LateUpdate 側: 2 度目の実行が進捗カウンタをリセットして二重加算 → メインの回収ループが担当数の 2 倍まで進んで `skeletonRenderers[r]` が範囲外。**0.25.0 と同じ位置で、こちらは 1 秒の停止が要りません**

詳細と interleaving の表: [`../findings/queue-race-analysis-2026-09-10.md`](findings/queue-race-analysis-2026-09-10.md)

## 3. 再現手順

自然発生を待たず、条件を作って踏ませました。鍵は 3 つが揃うことです。

1. ワーカーの 1 タスクが 1 秒以上遅れる → 本体が見捨てて進む
2. 見捨てられたワーカーが目覚めて古いカウンタを進める
3. その直前にスケルトンが大量に登録解除されている

Mod の高負荷テスト（タイトル画面で F9）はこれを自動で作ります。Spine のスケルトンを 600 体作って毎フレーム 15 体入れ替え、
`SkeletonRenderer.LateUpdateImplementation` の Prefix でワーカーの 1 タスクを 1.2 秒眠らせ、最後に眠っている内側で 600 体を一斉解除します。
回避処理を切って同じことをすると（cfg `9. Developer/FullAB=true`）、例外が出ます。

```
回避処理なし  見捨て 23 回 → out of range 1（ワーカー側）。ゲーム内では メイン側の例外も出てタイトルへ戻された
回避処理あり  見捨て 20 回 → 全部待った。例外 0
```

§2b の deque の競合も同じ F9 で踏ませます（v2.2.0）。テスト中は先頭 4 本のワーカーの起床イベントを `Pop` のたびに立て直し、`Pop` → 全 deque を `Steal` → `Pop` … と回り続けさせるので、
メインの `PushTop` との重なりが毎フレーム何百回になります。合否は「重なりを実際に作った（contended > 0）」かつ「同じタスクが 2 度走らなかった（double_run = 0）」です。

```
lock あり  重なり 9688 → 二重実行 0。例外 0
lock なし  重なり 0    → 二重実行 384123。ワーカー内で Dictionary の重複キー（同じ AnimationState を 2 スレッドが同時に Apply）、一斉解除で out of range 1
```

`FullAB=true` の B は deque を壊したまま終わるので、そのあとタイトルに戻ってもワーカーから out of range が出続けます。B を回したら再起動してください。

負荷だけ（CPU を詰まらせる）では 1 秒の停止を作れず、先頭側のタスクを遅らせても添字がリストの中ほどを指すだけで例外にはなりません。
末尾側のタスクを遅らせ、目覚めたあとの加算を数十ミリ秒に引き延ばして、初めて確実に出ました。手順の細部と生ログ:
[`../findings/reproduction-2026-09-05.md`](findings/reproduction-2026-09-05.md)

## 4. 本体側の修正案

どれも spine-unity の再ビルドで済みます（Unity プロジェクト内でソースからビルドされているはずです）。

1. `Assets/Resources/SpineRuntimeSettings.asset` の `useThreadedAnimation` / `useThreadedMeshGeneration` を true に。0.26 以降アセットは入っています
2. spine-unity の `SkeletonUpdateSystem.cs` 45 行目 `#define DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS` を無効化する
3. `LockFreeWorkStealingWorkerPool.cs` のタスク投入をスレッド安全にする。一番小さいのは `LockFreeWorkStealingDeque.cs` の `PushTop` / `Push` / `Pop` / `Steal` を `lock (this)` で囲むこと（この Mod がやっていることと同じ）。
   きちんと直すなら、スレッドごとに `ConcurrentQueue<Task>` を持って横取りは他の queue の `TryDequeue` にするか、`PushTop` を捨ててワーカー自身に `Push` させ、ワーカーの巡回が終わるまでメインが次を投入しないバリアを置く

1 だけだと 0.25.0 と同じ例外を踏みます。2 で待機パス（全タスク投入 → 全部終わるまで待つ → メイン側の処理をまとめて実行）に戻ります。
失うのはコメントどおり「少し」で、**アニメ更新の並列化はこの分岐と無関係なので効果は丸ごと残ります**。
3 が無いと、停止が無くても稀に同じタスクが 2 度走り、`GetMix: from cannot be null` と `Index was out of range` の両方が出ます（§2b）。

`UpdateAsync` 側のタイムアウト（`WaitForThreadUpdateTasks` の 1 秒）は上流の設計そのままですが、切れても進まない（待ち続ける）ようにするのが安全です。
また `LateUpdateSkeletonsAsyncImpl` の catch 節で `instance.skeletonRenderers[r]` を引き直すのは、二重に落ちてスレッドが死ぬので外したほうがよいです。

上流（Esoteric Software）へ報告するなら、この `#define` に `SPINE_DISABLE_*` のような逃げ道が無いこと（プロジェクト側の定義シンボルで切れない）と、
タイムアウト後に進むならカウンタ配列を作り直す必要があること、の 2 点が要点です。

## 5. この Mod がやっていること（暫定回避）

spine-unity のコードは差し替えていません。Harmony の Postfix 2 つで「見捨てる」を「待つ」に変えています。

- `SkeletonUpdateSystem.LateUpdateAsync` の Postfix（`LateUpdateGuard.cs`）: 戻ってきた時点で進捗カウンタが担当数に届いていないタスクがあれば、完了イベントを待って揃うまでメインスレッドを止め（上限 10 秒）、回収し損ねた `UpdateMeshAndMaterialsToBuffers()` を呼ぶ。毎フレームの負担はタスク数ぶんの int 比較（約 120 回）
- `SkeletonUpdateSystem.WaitForThreadUpdateTasks` の Postfix（`UpdateGuard.cs`）: 戻ってきた時点で `updateDone[t]` が立っていなければ揃うまで待つ
- `LockFreeWorkStealingDeque.PushTop` / `Push` / `Pop` / `Steal` の Prefix（`Monitor.Enter`）＋ Finalizer（`Monitor.Exit`）（`QueueGuard.cs`・v2.2.0）: その deque インスタンスの lock で直列化する。中身のアルゴリズムはそのまま。
  lock が塞がっていた回数（contended）と、同じタスクが走っている最中にもう一度始まった回数（double_run。`UpdateSkeletonsAsyncSplitImpl` / `UpdateSkeletonsAsyncImpl` / `LateUpdateSkeletonsAsyncImpl` の Prefix/Postfix）を数えて記録に出す。lock ありなら double_run は 0 のはず

本体の private に触るのは読み出しだけです（`skeletonsLateUpdatedAtTask` / `mainThreadProcessedAtTask` / `taskPartitionsLateUpdate` / `updateDone`。deque は型を `workerPool` → `_taskQueues` と辿るだけで、フィールドには触りません）。
起動時に `WaitForThreadLateUpdateTasks` が DLL に**ある**（＝待機パスでビルドされている）と分かったら Postfix は入れません。
つまり §4 の修正が本体に入れば、この Mod は自動的に何もしなくなります。

上流のコードを移植した版（Prefix で `LateUpdateAsync` を丸ごと差し替え）も作って同じ結果を得ましたが、Spine Runtimes License 上配れないので公開していません。

## 6. 設定とキー

cfg は `BepInEx/config/kiyonakanata.lwffpsboost.cfg`。項目は 5 つだけで、テストの数値やキーは cfg に出さず定数にしています。

| 項目 | 既定 | 何のためにあるか |
|---|---|---|
| `1. General/Enabled` | true | false で Mod 全体を止める（BepInEx の作法。DLL を外すのと同じ） |
| `9. Developer/ThreadedAnimation` | true | アニメ更新のマルチスレッドだけを切る。GetMix 系の不具合が出たときに、メッシュ生成側と切り分けるため |
| `9. Developer/ThreadedMeshGeneration` | true | メッシュ生成のマルチスレッドだけを切る。同上 |
| `9. Developer/QueueLock` | true | deque の直列化（§2b）だけを切る。切って回すと記録の `double_run` が増えるかで、競合が実在することを確かめられる。**切るとまれに落ちる**ので配布版では true |
| `9. Developer/ShowInGame` | false | ゲーム内でも表示とキーを有効にする。ゲーム内で F9 / F10 を押すと、場面で一番アニメの多い実際のスケルトン（モモコ・使い魔）を雛形にテストが走る。工場での効果や、実際の使い魔での再現を見たいときに |
| `9. Developer/FullAB` | false | F9 を A/B にする。A（回避処理あり・deque の直列化あり）のあと B（どちらも無し）も回して、例外と二重実行が出ることまで確かめる。**B ではゲームが落ちることがあり、終わったあとも壊れた deque から例外が出続ける**ので、配布版では false。B を回したら再起動する。B の例外はゲームのエラー送信にも乗るので、確認が済んだら戻す |

キーは固定です。

| キー | 動作 | 場面 |
|---|---|---|
| F9 | 高負荷テスト（§3）。合格／不合格／判定不能 | タイトル画面（ShowInGame=true ならゲーム内でも） |
| F10 | マルチスレッド効果検証（§7）。ON → OFF → ON 各 20 秒 | 同上 |
| Shift+F11 | 詳細表示（フレーム時間・登録数・回避処理が待った回数・Unity エラー） | タイトル画面 |
| Shift+F10 | マルチスレッドの手動 ON/OFF（効果検証が中で使う） | タイトル画面 |

テスト中に画面が切り替わったら中止し、マルチスレッドを無効にして、再起動を促します（途中で止めた状態を引きずらないため）。

テストの数値（600 体・毎フレーム 15 体入れ替え・45 秒、効果検証 1500 体・各 20 秒、stall 1.2 秒・60 フレームごと・末尾から 12 体手前、目覚めたあと 3 ms × 16 体）は
`SpineThreadingMod.cs` の Awake に定数で書いてあり、根拠は [`../findings/reproduction-2026-09-05.md`](findings/reproduction-2026-09-05.md) です。

## 7. 効果（作成者の環境での一例）

Ryzen 7 5700X（8C/16T）。VSync を外し、マルチスレッド ON → OFF → ON を各 20 秒。

| 場面 | ON | OFF | 1 フレームの短縮 |
|---|---|---|---|
| 工場（モモコ 1500 体） | 21.9 ms | 51.1 ms | 29 ms（2.33 倍） |
| タイトル（テスト用スケルトン 1500 体） | 21.0 ms | 58.4 ms | 37 ms（2.75 倍） |

倍率はその場の他の処理量で変わるので、場面をまたいで比べるなら短縮 ms のほうです。論理スレッド数が少ない PC では効果もそのぶん小さくなります。

## 8. 記録の回収

利用者の環境から 2 つのファイルが回収できます。どちらも `BepInEx/` に自動で書かれ、そのまま転送できる書式（key=value の行）です。

**1 回の工場ごとの記録** `LwfFpsBoost-games.log`（[`GameReport.cs`](GameReport.cs)）。問題が無かった回も含めて、工場を出るたびに 1 ブロック追記します
（落ちたときに備えて、ゲーム中は 60 秒ごとに `-game-current.txt` へ上書き保存）。
「ゲーム中か」は本体の `RunHistory.RunRecordingRuntime.HasActiveRun` をリフレクションで読んで判定し、終了理由（`GameEndReason`）と run の ID も本体のものを載せます。
本体側の RunHistory の記録と ID で突き合わせられます。読めなくなった版では InGame シーンの出入りで代用します。

```
==== game 2026-09-06 01:22:20 -> 01:58:40  (36.4 min)  end=ReturnedToTitle  mode=NormalGame  run=3f2a9c...
game=0.27.0  mod=2.1.0  unity=6000.0.80f1
os="Windows 11  (10.0.26200) 64bit"  cpu="AMD Ryzen 7 5700X 8-Core Processor"  threads=16  ram=31 GB  gpu="..."
threading=on  guard=on  threading_off_time=0 s
frames=129500  avg=16.6 ms  max=412.3 ms  over33ms=1197 (0.9%)  over100ms=12
skeletons_max: mesh=688 anim=688
guard: lateUpdate waits=3 (total 850 ms)  update waits=1 (total 120 ms)  giveups=0
queue: lock=on  contended=12  double_run=0
errors: upstream_timeouts=4  incidents=0 (out_of_range=0, null=0)  unity_errors=2  spine_errors=4
```

`guard:` の waits が「本体が見捨てた回数＝回避処理が働いた回数」、`total` がその回で止めた時間の合計です。
`queue:` の contended が「deque の投入と取り出しが同時に来て lock で待たせた回数」（§2b の競合が実際に開いた回数）、double_run が「同じタスクが同時に走った回数」（lock ありなら 0 のはず）です。`over100ms` と合わせると、
どれくらいの引っかかりがどれくらいの頻度で起きているかが読めます。タイトル画面で回したテストは `==== test …` ブロックとして同じファイルに入ります。

**事故の記録** `LwfFpsBoost-incidents.log`（[`IncidentLog.cs`](IncidentLog.cs)）。Spine 由来の例外・エラーが出たときだけ 1 件ずつ追記します
（Player.log は起動ごとに上書きされ前回分しか残らないため）。日時・ゲーム版・Mod 版・スレッド数・シーン・マルチスレッドと回避処理の状態・テスト中かどうか・
直前 40 行の文脈（上流の timeout、回避処理が待った記録、テストの節目）・例外の本文とスタック。書式の実例: [`../findings/incident-sample-2026-09-06.log`](findings/incident-sample-2026-09-06.log)

## 9. 分かっていないこと

- `GetMix: from cannot be null` という**文言そのもの**はテストでは出していません。F9 で作れるのは原因（同じタスクが 2 度走る。lock なしで二重実行 384123 回）と、その直接の結果（Dictionary の重複キー・out of range）までで、
  `GetMix` は壊れたプールがあとでトラックに残す `animation = null` から出るため、壊れてから落ちるまでに時間差があります。原因を塞いだ根拠は「lock ありで重なり 9688 回に対し二重実行 0」（§3）です
- 実プレイでの `contended`（重なりが実際に起きた回数）と `double_run`（0 のはず）の数字はこれから集めます。分かっている原因で塞いでいないものはありません
- 上流が直したかは `spine-unity.dll` のハッシュで追っています（0.21〜0.27 は同一 `b970de3325ff…`）

## 10. 資料

- [`../findings/threading-race-analysis.md`](findings/threading-race-analysis.md) … 原因の確定（上流ソースの読み・出荷 DLL の分岐の確認）
- [`../findings/reproduction-2026-09-05.md`](findings/reproduction-2026-09-05.md) … 再現手順・A/B の生ログ・性能
- [`../findings/queue-race-analysis-2026-09-10.md`](findings/queue-race-analysis-2026-09-10.md) … worker pool の deque の競合（§2b）。例外の意味・重なりの表・直し方
- 参照した上流ソース（spine-runtimes 4.3-beta）: [SkeletonUpdateSystem.cs](https://github.com/EsotericSoftware/spine-runtimes/blob/4.3-beta/spine-unity/Assets/Spine/Runtime/spine-unity/Threading/SkeletonUpdateSystem.cs) / [LockFreeWorkStealingWorkerPool.cs](https://github.com/EsotericSoftware/spine-runtimes/blob/4.3-beta/spine-unity/Assets/Spine/Runtime/spine-unity/Threading/LockFreeWorkStealingWorkerPool.cs) / [LockFreeWorkStealingDeque.cs](https://github.com/EsotericSoftware/spine-runtimes/blob/4.3-beta/spine-unity/Assets/Spine/Runtime/spine-unity/Threading/LockFreeWorkStealingDeque.cs) / [spine-csharp AnimationState.cs](https://github.com/EsotericSoftware/spine-runtimes/blob/4.3-beta/spine-csharp/src/AnimationState.cs)（`EventQueue.Drain` と `Pool<TrackEntry>`）
- [`../findings/incident-sample-2026-09-06.log`](findings/incident-sample-2026-09-06.log) … 事故記録の実例（テストで出したもの）
- ソース: [`SpineThreadingMod.cs`](SpineThreadingMod.cs)（本体・表示・テスト）、[`LateUpdateGuard.cs`](LateUpdateGuard.cs) / [`UpdateGuard.cs`](UpdateGuard.cs)（回避処理）、[`QueueGuard.cs`](QueueGuard.cs)（deque の直列化）、[`StressTools.cs`](StressTools.cs) / [`BuiltinSkeleton.cs`](BuiltinSkeleton.cs)（テスト）、[`IncidentLog.cs`](IncidentLog.cs)（記録）。ビルドは `build.ps1`（csc.exe / C# 5、Assembly-CSharp には依存しない）

## 11. 経緯

- 2026-08-28（ver0.24.1）: フラグを立てるだけの v1 を作り、ファム 688 体でフレーム時間 51.0 → 26.4 ms を計測。作者へ報告
- ver0.25.0: 本体が同じフラグを立てて配信 → `Index was out of range` の報告で取り下げ
- 2026-08-29〜30: 上流ソースを読んで原因を高速化パスと確定。「Mod では塞げない」と判断してクローズ
- 2026-09-05（ver0.27.0）: 再現に成功。上流の待機パスの移植で回避を実証、ライセンス上配れないので Postfix に書き直し（移植版は非公開）
- 2026-09-06: アニメ更新側にも Postfix。タイトル画面で完結するテストと事故記録を付けて配布の形に
- 2026-09-10: v2.1.1 で見捨て 0 回のまま `GetMix: from cannot be null`。worker pool の deque にメインとワーカーが同時に触る競合（§2b）と特定し、v2.2.0 で deque の操作を lock で直列化
