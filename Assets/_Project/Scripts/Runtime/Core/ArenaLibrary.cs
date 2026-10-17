using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Арены из редактора: Resources/Arenas/&lt;имя&gt;.json и index.json — все арены, включённые в генерацию и флаг
    /// «только арены из редактора» (для проверки: тогда арены Тартара в пул этажа не идут).</summary>
    public static class ArenaLibrary
    {
        public const string Folder = "Arenas";
        public const string Prefix = "Editor/";

        [Serializable]
        public sealed class Index
        {
            public string[] arenas = Array.Empty<string>();
            public string[] enabled = Array.Empty<string>();
            public bool onlyEditor;
        }

        public static Index LoadIndex()
        {
            var ta = Resources.Load<TextAsset>(Folder + "/index");
            return ta != null ? JsonUtility.FromJson<Index>(ta.text) ?? new Index() : new Index();
        }

        public static ArenaLayout Get(string name)
        {
            var ta = Resources.Load<TextAsset>(Folder + "/" + name);
            return ta != null ? ArenaLayout.FromJson(ta.text) : null;
        }

        /// <summary>Шаблоны включённых арен, прошедших проверку, и их отражённые копии.</summary>
        public static List<ArenaTemplate> Templates(Index index = null)
        {
            index ??= LoadIndex();
            var list = new List<ArenaTemplate>();
            foreach (var name in index.enabled)
            {
                var a = Get(name);
                if (a == null) continue;
                var t = ArenaCheck.ToTemplate(a, Prefix + name);
                if (t == null) { Debug.LogWarning($"Арена «{name}» не допущена к генерации: есть ошибки (Arena → Arena Editor)."); continue; }
                list.Add(t);
                var m = ArenaCheck.ToTemplate(a.Transposed(), Prefix + name + "/m");
                if (m != null) list.Add(m);
            }
            return list;
        }
    }
}
