FPS改善MOD（LWF FPS Boost）
※ 人柱版。不具合が出る恐れあり。エラーのデータ収集も兼ねる
使い魔が多い工場で重くなるのを軽くする（フレーム時間が約半分）

────────────────────────────────
入れかた
────────────────────────────────

1. BepInEx 5 を落として、ゲームのフォルダ
   （LazyWitchsFactory.exe と同じ場所）へ展開する

     https://github.com/BepInEx/BepInEx/releases
     BepInEx_win_x64_5.4.x.zip

2. 一度ゲームを起動して終了すると、BepInEx/plugins などが作られる

3. LwfFpsBoost.dll を BepInEx/plugins/ に入れる

4. ゲームを起動する。タイトル画面の左上に案内が出れば動いている（出るのはタイトルだけ）

5. 初回はタイトル画面で F9 を押し、「合格」が出ることを確かめる

MOD管理ソフトを使うなら Thunderstore から

     https://thunderstore.io/c/lazy-witchs-factory/p/KiyonakaNata/LwfFpsBoost/

────────────────────────────────
タイトル画面のキー
────────────────────────────────

F9   高負荷テスト（約 53 秒）→ 合格／不合格／判定不能
F10  マルチスレッド効果検証（約 66 秒）

テスト中は PC が重くなる。
テスト中にタイトルから移動すると中止され、マルチスレッドが無効になる → ゲームを再起動
判定不能 → もう一度 F9
テストでエラーが出たあとは、次にゲームを起動したとき 1 回だけゲームのエラー画面が出る（前回分の再表示）→ 閉じてゲームを再起動

────────────────────────────────
消しかた
────────────────────────────────

BepInEx/plugins/LwfFpsBoost.dll を消す

本体がマルチスレッド化を入れたら、この MOD は何も掛けずに止まる。
タイトル画面に「本体が対応済み。この MOD は不要」と出たら消す

────────────────────────────────

報告・質問は公式 Discord の Mod チャンネルへ。
BepInEx/LwfFpsBoost-games.log（遊んだ結果）を添える。
エラーが出たときは BepInEx/LwfFpsBoost-incidents.log も。

仕組みと再現手順（ゲーム作者向け）は同梱の DEVELOPER.md

────────────────────────────────
English
────────────────────────────────

LWF FPS Boost — threaded Spine animation and mesh generation.
Experimental build. Frame time drops by about half in factories with many familiars.

Install

1. Unpack BepInEx 5 (win x64) into the game folder

     https://github.com/BepInEx/BepInEx/releases

2. Start the game once and quit, so BepInEx/plugins is created

3. Put LwfFpsBoost.dll into BepInEx/plugins/

4. Start the game. The panel shows at the top left of the title screen

With a mod manager, install from Thunderstore

     https://thunderstore.io/c/lazy-witchs-factory/p/KiyonakaNata/LwfFpsBoost/

Keys (title screen)

F9   load test, ~53 s  -> PASS / FAIL / INCONCLUSIVE
F10  speed test, ~66 s

The PC is under heavy load during a test.
Leaving the title screen during a test stops it and turns threading off -> restart the game.
INCONCLUSIVE -> press F9 again.
After a test that produced an error, the game shows its error screen once on the next start
-> close it and restart.

Remove

Delete BepInEx/plugins/LwfFpsBoost.dll
When the game ships threaded Spine updates, this mod applies nothing.
Delete it when the title screen reads "The game runs Spine threaded"

Report in the Mod channel of the official Discord.
Attach BepInEx/LwfFpsBoost-games.log, and LwfFpsBoost-incidents.log when it exists.

DEVELOPER.md in this zip covers the cause, the reproduction, and a fix for the game.

