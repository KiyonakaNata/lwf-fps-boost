// LWF FPS Boost — 埋め込みの最小スケルトン
//
// テスト（churn）は場面にいるスケルトンを雛形にするが、タイトル画面には Spine のスケルトンが無い。
// そこで、骨 80 本・4×4 の白い四角 80 枚・アニメ 3 種のスケルトンをここで組み立てて雛形にする。
// 重さは実戦の使い魔 1 体ぶんくらいを狙っている（骨 3 本の最小構成では 1500 体でも負荷にならず、
// マルチスレッドの効果が 1.25 倍にしか見えなかった）。
// データは自作（下で組み立てる JSON とアトラス文字列）で、ゲームや Spine のアセットは使わない。
// タイトル画面では色付きで視界内に置き、テスト中と見て分かるようにする（置く側の処理）。
//
// spine-unity の実行時生成 API を使う:
//   SpineAtlasAsset.CreateRuntimeInstance(TextAsset atlas, Texture2D[] textures, Shader shader, bool initialize, ...)
//   SkeletonDataAsset.CreateRuntimeInstance(TextAsset json, AtlasAssetBase atlas, bool initialize, float scale)
//
// C# 5（csc.exe）でビルドするため、文字列補間・?.・式形式メンバは使えない。

using System;
using BepInEx.Logging;
using Spine.Unity;
using UnityEngine;

namespace LwfFpsBoost
{
    internal static class BuiltinSkeleton
    {
        private const string PageName = "lwfstress";

        /// <summary>骨の数。実戦の使い魔に近い重さにするため多めにしてある（骨 1 本につき四角 1 枚）。</summary>
        internal const int BoneCount = 80;

        /// <summary>
        /// Spine 4.x の JSON を組み立てる。root の下に骨を鎖状に BoneCount 本、各骨にスロットと領域 "a"（4×4 の四角）、
        /// アニメ 3 種（回転・拡縮・移動）はそれぞれ全骨にキーを打つ。アニメ更新は骨×タイムライン、
        /// メッシュ生成は四角の枚数に比例するので、この数で使い魔 1 体ぶんくらいの負荷になる。
        /// </summary>
        private static string BuildJson(int bones)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(bones * 400 + 1024);
            sb.Append("{\n\"skeleton\": { \"hash\": \"lwfstress\", \"spine\": \"4.3.0\", \"x\": -200, \"y\": -200, \"width\": 400, \"height\": 400 },\n");

            sb.Append("\"bones\": [ { \"name\": \"root\" }");
            for (int i = 1; i <= bones; i++)
            {
                sb.Append(",\n  { \"name\": \"b").Append(i).Append("\", \"parent\": \"").Append(i == 1 ? "root" : "b" + (i - 1))
                  .Append("\", \"length\": 5, \"x\": 5 }");
            }
            sb.Append(" ],\n");

            sb.Append("\"slots\": [");
            for (int i = 1; i <= bones; i++)
            {
                if (i > 1) { sb.Append(","); }
                sb.Append("\n  { \"name\": \"s").Append(i).Append("\", \"bone\": \"b").Append(i).Append("\", \"attachment\": \"a\" }");
            }
            sb.Append(" ],\n");

            sb.Append("\"skins\": [ { \"name\": \"default\", \"attachments\": {");
            for (int i = 1; i <= bones; i++)
            {
                if (i > 1) { sb.Append(","); }
                sb.Append("\n  \"s").Append(i).Append("\": { \"a\": { \"width\": 4, \"height\": 4 } }");
            }
            sb.Append("\n} } ],\n");

            sb.Append("\"animations\": {\n");
            // spin: 全骨を回す（位相をずらす）
            sb.Append("  \"spin\": { \"bones\": {");
            for (int i = 1; i <= bones; i++)
            {
                if (i > 1) { sb.Append(","); }
                int phase = (i * 7) % 90;
                sb.Append("\n    \"b").Append(i).Append("\": { \"rotate\": [ { \"value\": ").Append(phase)
                  .Append(" }, { \"time\": 0.5, \"value\": ").Append(phase + 180).Append(" }, { \"time\": 1, \"value\": ").Append(phase + 360).Append(" } ] }");
            }
            sb.Append("\n  } },\n");
            // pulse: 全骨を拡縮
            sb.Append("  \"pulse\": { \"bones\": {");
            for (int i = 1; i <= bones; i++)
            {
                if (i > 1) { sb.Append(","); }
                sb.Append("\n    \"b").Append(i).Append("\": { \"scale\": [ { \"x\": 1, \"y\": 1 }, { \"time\": 0.5, \"x\": 1.8, \"y\": 0.6 }, { \"time\": 1, \"x\": 1, \"y\": 1 } ] }");
            }
            sb.Append("\n  } },\n");
            // wave: 全骨を移動
            sb.Append("  \"wave\": { \"bones\": {");
            for (int i = 1; i <= bones; i++)
            {
                if (i > 1) { sb.Append(","); }
                int amp = 2 + (i % 5);
                sb.Append("\n    \"b").Append(i).Append("\": { \"translate\": [ { \"x\": 0, \"y\": 0 }, { \"time\": 0.5, \"x\": ").Append(amp)
                  .Append(", \"y\": ").Append(-amp).Append(" }, { \"time\": 1, \"x\": 0, \"y\": 0 } ] }");
            }
            sb.Append("\n  } }\n");
            sb.Append("}\n}\n");
            return sb.ToString();
        }

        private const string Atlas =
            PageName + ".png\n" +
            "size: 4, 4\n" +
            "format: RGBA8888\n" +
            "filter: Linear, Linear\n" +
            "repeat: none\n" +
            "a\n" +
            "  rotate: false\n" +
            "  xy: 0, 0\n" +
            "  size: 4, 4\n" +
            "  orig: 4, 4\n" +
            "  offset: 0, 0\n" +
            "  index: -1\n";

        private static readonly string[] ShaderCandidates = {
            "Spine/Skeleton",
            "Universal Render Pipeline/2D/Sprite-Unlit-Default",
            "Universal Render Pipeline/Unlit",
            "Sprites/Default",
            "Unlit/Texture",
            "Hidden/InternalErrorShader"
        };

        private static SkeletonDataAsset _cached;
        private static string _shaderUsed = "";

        internal static string ShaderUsed { get { return _shaderUsed; } }

        /// <summary>組み立てて返す（2 回目以降は同じものを返す）。失敗したら null を返し、理由をログに出す。</summary>
        internal static SkeletonDataAsset Get(ManualLogSource log)
        {
            if (_cached != null) { return _cached; }
            try
            {
                Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                Color32[] px = new Color32[16];
                for (int i = 0; i < px.Length; i++) { px[i] = new Color32(255, 255, 255, 255); }
                tex.SetPixels32(px);
                tex.Apply(false, false);
                tex.name = PageName;                 // アトラスのページ名（拡張子抜き）と一致させる

                Shader shader = null;
                for (int i = 0; i < ShaderCandidates.Length && shader == null; i++)
                {
                    shader = Shader.Find(ShaderCandidates[i]);
                    if (shader != null) { _shaderUsed = ShaderCandidates[i]; }
                }
                if (shader == null)
                {
                    log.LogWarning("[builtin] no shader found, cannot build the test skeleton");
                    return null;
                }

                TextAsset atlasText = new TextAsset(Atlas);
                atlasText.name = PageName + ".atlas";
                SpineAtlasAsset atlas = SpineAtlasAsset.CreateRuntimeInstance(atlasText, new Texture2D[] { tex }, shader, true, null);
                if (atlas == null || atlas.GetAtlas() == null)
                {
                    log.LogWarning("[builtin] atlas creation failed");
                    return null;
                }

                TextAsset json = new TextAsset(BuildJson(BoneCount));
                json.name = PageName;
                SkeletonDataAsset asset = SkeletonDataAsset.CreateRuntimeInstance(json, atlas, true, 0.01f);
                if (asset == null || asset.GetSkeletonData(true) == null)
                {
                    log.LogWarning("[builtin] skeleton data creation failed");
                    return null;
                }
                asset.name = PageName;
                _cached = asset;
                Spine.SkeletonData sd = asset.GetSkeletonData(true);
                log.LogInfo("[builtin] test skeleton ready: shader=" + _shaderUsed
                    + ", bones=" + sd.Bones.Count + ", slots=" + sd.Slots.Count + ", anims=" + sd.Animations.Count);
                return _cached;
            }
            catch (Exception e)
            {
                log.LogWarning("[builtin] building the test skeleton threw: " + e);
                return null;
            }
        }
    }
}
