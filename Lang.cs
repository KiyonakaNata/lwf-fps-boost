// 画面に出す文字の日英。
//
// ゲームの設定言語に合わせる。Unity Localization は参照に入れていないので反射で読む。
// 読めなければ OS の言語に落とす。
//
// 対で持つのは画面に出す文字だけ。cfg とログは英語のみ（mods/CLAUDE.md）。

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LwfFpsBoost
{
    internal static class Lang
    {
        private static bool _known;
        private static bool _ja;

        /// <summary>いま日本語表示か。</summary>
        internal static bool Ja
        {
            get
            {
                if (!_known) { Refresh(); }
                return _ja;
            }
        }

        /// <summary>言語の切り替えに追いつくため、画面が変わるたびに引き直す。</summary>
        internal static void Refresh()
        {
            _known = true;
            _ja = Detect();
        }

        internal static string T(string ja, string en)
        {
            return Ja ? ja : en;
        }

        private static bool Detect()
        {
            string code = LocaleCode();
            if (!string.IsNullOrEmpty(code))
            {
                return code.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
            }
            try { return Application.systemLanguage == SystemLanguage.Japanese; }
            catch (Exception) { return false; }
        }

        private static string LocaleCode()
        {
            try
            {
                Type settings = AccessTools.TypeByName("UnityEngine.Localization.Settings.LocalizationSettings");
                if (settings == null) { return null; }
                PropertyInfo selected = settings.GetProperty("SelectedLocale",
                    BindingFlags.Public | BindingFlags.Static);
                object locale = selected != null ? selected.GetValue(null, null) : null;
                if (locale == null) { return null; }

                PropertyInfo identifier = locale.GetType().GetProperty("Identifier");
                object id = identifier != null ? identifier.GetValue(locale, null) : null;
                if (id == null) { return null; }

                PropertyInfo code = id.GetType().GetProperty("Code");
                return code != null ? code.GetValue(id, null) as string : null;
            }
            catch (Exception) { return null; }
        }
    }
}
