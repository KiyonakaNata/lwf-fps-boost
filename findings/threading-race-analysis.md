# 0.25.0 のクラッシュはどこの問題か — SkeletonUpdateSystem を読んだ結果

調査: 2026-08-29
対象: `spine-unity` 4.3-beta の `Runtime/spine-unity/Threading/SkeletonUpdateSystem.cs`
出典: 上流リポジトリ（EsotericSoftware/spine-runtimes, ブランチ `4.3-beta`）の同ファイル。
      写しを `SkeletonUpdateSystem-4.3-beta.cs` に保存。

## 結論（先に）

**ゲーム側のコードの問題ではない。`spine-unity` 4.3-beta の
`DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS` という高速化パス内部のバグ。**

そしてこの `#define` は**ファイル自身が既定で有効にしている**（45行目）。

```csharp
#define DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS // enabled improves performance a bit.
```

作者は `SpineRuntimeSettings.asset` を1個足しただけで、この分岐に触っていない。
**フラグを立てた側の落ち度ではなく、立てた先の beta 実装の穴。**

## 出荷DLLがどちらの分岐でビルドされているか

リフレクションで `Spine.Unity.SkeletonUpdateSystem` のメンバを列挙して確定させた。

| 根拠 | 判定 |
|---|---|
| `WaitForThreadLateUpdateTasks` が**メソッド一覧に無い** | `#if !DONT_WAIT…` 側は未コンパイル |
| `skeletonsLateUpdatedAtTask` / `mainThreadProcessedAtTask` / `lateUpdateWorkAvailable` が**フィールドに有る** | `#if DONT_WAIT…` 側がコンパイルされている |

→ **出荷ビルドは `DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS` 有効**で確定。

## 「ワーカー稼働中にゲーム側が触る」窓は開いていない

当初の推測は外れ。両パスとも**自分の呼び出し内で処理を閉じている**。

### アニメ更新（Update / FixedUpdate / LateUpdate 前半）
`UpdateAsync` は分岐の両方（`UpdateAsyncThreadedCallbacks` /
`UpdateAsyncSplitMainThreadCallbacks`）が末尾で `WaitForThreadUpdateTasks(...)` を呼び、
そのあと `isProcessingAnimations = false` → `FlushSkeletonAnimationListModifications()`。
**戻った時点でワーカーは全員終わっている。**

### メッシュ生成（LateUpdate 後半）
`DONT_WAIT…` は「投げっぱなし」ではなく**協調ドレイン**。
メインスレッドはワーカーの進捗カウンタを見ながら、終わったスケルトンから順に
`UpdateMeshAndMaterialsToBuffers()` を回収し、
`while (anySkeletonsLeft && !timedOut)` で**全部終わるまで抜けない**。

つまり「並列化を有効にするとゲーム側が好きなタイミングで触って壊れる」ではない。
**mod で外からロックを被せる**という発想自体が的外れだった。

## では何が壊れているか

`LateUpdateAsync` の協調ドレインが、**2つのカウンタ配列の整合性だけ**を頼りに
`skeletonRenderers` を直接インデックスしている。

```csharp
// メインスレッド側（723-732行）
while (mainThreadProcessedAtTask[t] < VolatileRead(ref skeletonsLateUpdatedAtTask[t])) {
    int r = mainThreadProcessedAtTask[t] + rendererStartIndex;
    var skeletonRenderer = this.skeletonRenderers[r];   // ★ ここが報告された例外の発生点
    ...
    mainThreadProcessedAtTask[t]++;
}
```

報告された例外はこれと一致する。

```
ArgumentOutOfRangeException: Index was out of range.
  System.Collections.Generic.List`1[T].get_Item
  ← Spine.Unity.SkeletonUpdateSystem.LateUpdateAsync ()
```

`LateUpdateAsync` が**直接** `List<T>` を引くのはこの1か所だけ。
`r` が `skeletonRenderers.Count` を超えた＝カウンタが実態とずれた、ということ。

### ずれる余地が実際にある

1. **カウンタを2か所からリセットしている。**
   メインスレッドが投入前に `skeletonsLateUpdatedAtTask[t] = 0`（654行）、
   **ワーカーがタスク開始時にもう一度 `= 0`**（1012行）。
   後者はタスクが実際に拾われた時点なので、投入からの遅延が読めない。

2. **配列は一度しか確保されず、リサイズされない。**
   ```csharp
   if (skeletonsLateUpdatedAtTask == null) {
       skeletonsLateUpdatedAtTask = new int[numAsyncTasks];
       mainThreadProcessedAtTask  = new int[numAsyncTasks];
   }
   ```
   `numAsyncTasks` は `skeletonRenderers.Count` から毎フレーム計算される。
   **使い魔が増えてタスク数が増えても配列は初回のまま。**

3. **`volatile` が効いていない。**
   宣言は `volatile protected int[] skeletonsLateUpdatedAtTask;` で、
   volatile なのは**配列参照であって要素ではない**。
   要素の読みは `VolatileRead` で守っているが、1012行の `= 0` は素の書き込み。

4. **タスクオブジェクトを使い回している。**
   `genericSkeletonTasks[range.taskIndex]` の `parameters` を毎フレーム上書きする方式で、
   この配列は**アニメ更新パスとメッシュ生成パスで共用**。

### もう1件（`GetMix: from cannot be null`）について

アニメ更新は上記のとおり呼び出し内で待ち切るので、
**「入力コールバックがワーカーと衝突した」では説明がつかない**。
`mainThreadUpdateCallbacks = true` のときに走る `UpdateAsyncSplitMainThreadCallbacks` は、
`splitUpdateMethod[i]`（CoroutineIterator）で**アニメ更新を途中まで進めた状態でメインスレッドに戻し**、
コールバックを処理してから再開する、という分割実行をしている。
この「途中状態」の間に `AnimationState` を触ると `TrackEntry` の鎖が中途半端に見えうる。

こちらは**未確定**。`SkeletonAnimationBase.UpdateExternal` と
`UpdateSkeletonsMainThreadSplit` を読まないと断定できない。ただし
**どちらに転んでも spine-unity 側の分割実行の話**で、ゲーム側のロック不足ではない。

## mod で直せるか

**直せない。** 外からロックを被せる話ではなく、
`LateUpdateAsync` とワーカー実装の**中の整合性**の問題なので、
塞ぐには当該メソッド群を丸ごと差し替えるしかない。

小さく見える唯一の手は「高速化パスを切って待機パスに戻す」だが、
**待機パス側の `WaitForThreadLateUpdateTasks` は出荷DLLにコンパイルされていない**ので
呼ぶ先が無い。自分で書けば、それは mod ではなく
**1,000行のロックフリー実装の fork を IL で保守する**ことになる。

## `WaitForThreadLateUpdateTasks` とは（＝落ちている側の関数）

「全ワーカーが終わるまで待つだけ」の関数。`lateUpdateDone[t]` を1秒タイムアウトで待つループ。

```csharp
#if !DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS
private void WaitForThreadLateUpdateTasks (int numAsyncTasks) {
    for (int t = 0; t < numAsyncTasks; ++t) {
        bool success = lateUpdateDone[t].Wait(1000);
        if (!success) Debug.LogError("...Waiting for lateUpdateDone ... timeout...");
    }
}
#endif
```

| | 待機パス | 高速化パス（出荷側） |
|---|---|---|
| 手順 | 全タスク投入 → **全部終わるまでブロック** → メイン側の処理をまとめて実行 | 全タスク投入 → **終わったスケルトンから順に**メイン側の処理を回収 |
| 得 | — | ワーカー稼働中にメインの仕事を重ねられる |
| 要るもの | 完了イベントだけ | **進捗カウンタの帳簿**（＝今回壊れた箇所） |

**DLL に無いのは「作者が入れ忘れた」のではなく、define が有効だとコンパイラが落とすから。**

## 上流はこのスイッチにだけ逃げ道を用意していない

冒頭の define ブロックを見ると、他のスイッチには公式の無効化手段がある。

```csharp
#if !SPINE_DISABLE_THREADING
#define USE_THREADED_SKELETON_UPDATE          // ← プロジェクト側 define で切れる
#endif
#if !SPINE_DISABLE_LOAD_BALANCING
#define ENABLE_WORK_STEALING                  // ← 切れる
#endif

#define READ_VOLATILE_ONCE                    // ← 無条件
#define DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS    // ← 無条件。逃げ道なし
```

→ **Player Settings のスクリプト定義シンボルでは切れない。** ソースを直接編集するしかない。
上流に「`SPINE_DISABLE_LATEUPDATE_FAST_PATH` のような逃げ道が欲しい」と出す価値がある。

なお spine-unity は Unity プロジェクト内で**ソースからビルドされる**（asmdef → `spine-unity.dll`）ので、
**作者は上流を待たずに自分で直せる**。45行目をコメントアウトして再ビルドするだけ。

## 作者に伝えるなら

「クラッシュします」より、**どのスイッチを戻せばよいか**まで書けるのが今回の収穫。

- 原因は `spine-unity` の `SkeletonUpdateSystem.cs` 45行目の
  `#define DONT_WAIT_FOR_ALL_LATEUPDATE_TASKS`。
- これを**無効化して spine-unity を再ビルド**すれば、
  `WaitForThreadLateUpdateTasks` を使う素直な待機パスに戻る。
- コメントに `// enabled improves performance a bit.` とあるとおり、
  失うのは「少し」。**アニメ更新の並列化（効果の約半分）はこの分岐と無関係なので丸ごと残る。**
- 上流（EsotericSoftware）に報告する価値もある。4.3-beta の既定値の問題なので。

### 効果の内訳（`measurement-results.md` より・945体）

| | OFF | ON | 差 |
|---|---|---|---|
| アニメ更新（メイン） | 22.10 ms | 3.64 ms | −18.5 ms |
| メッシュ生成（メイン） | 20.48 ms | 4.57 ms | −15.9 ms |

高速化パスを切ってもメッシュ生成の並列化自体は残るので、
失うのは待機方式の差分だけ。
