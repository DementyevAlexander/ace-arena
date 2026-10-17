using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Мета-прогресс между забегами — сохраняется (GameController пишет его в meta.json). Коллекция: открытые формы,
    /// свойства и тринкеты — в забеге выпадают только они (карта — если открыты и форма, и свойство). Сардис — мета-валюта,
    /// тёмно-красный камень (Откр. 4:3, 21:20): падает с боссов, не теряется при смерти, тратится у Торговца в хабе.
    /// Летопись: число забегов, побед, лучший этаж, убийства, победы над Мегерой, последние забеги. Найденные связки.</summary>
    [Serializable]
    public sealed class MetaProgress
    {
        /// <summary>Новое сохранение: формы Снаряд, Рывок, Зона × свойства Пламя, Иней, Рикошет, Взрыв — 12 карт; тринкеты закрыты.</summary>
        public static readonly FormKind[] StarterForms = { FormKind.Shot, FormKind.Dash, FormKind.Zone };
        public static readonly Trait[] StarterTraits = { Trait.Fire, Trait.Frost, Trait.Ricochet, Trait.Blast };
        public const int HistorySize = 10;

        public List<int> forms = new();
        public List<int> traits = new();
        public List<int> trinkets = new();
        public List<int> bonds = new();      // связки, которые уже собирались
        public int sardis;
        public int runs, wins, bestFloor, totalKills, megaeraKills;
        public List<RunRecord> history = new(); // последние забеги, новые — первыми

        /// <summary>Есть несохранённые изменения (GameController сохраняет и сбрасывает).</summary>
        [NonSerialized] public bool Dirty;

        public static MetaProgress Starter()
        {
            var m = new MetaProgress();
            foreach (var f in StarterForms) m.forms.Add((int)f);
            foreach (var t in StarterTraits) m.traits.Add((int)t);
            return m;
        }

        /// <summary>Всё открыто — проверки и режимы без сохранения.</summary>
        public static MetaProgress AllUnlocked()
        {
            var m = new MetaProgress();
            foreach (FormKind f in Enum.GetValues(typeof(FormKind))) m.forms.Add((int)f);
            for (int i = 0; i < Traits.Count; i++) m.traits.Add(i);
            foreach (var k in TrinketBook.All) m.trinkets.Add((int)k);
            return m;
        }

        public bool Has(FormKind f) => forms.Contains((int)f);
        public bool Has(Trait t) => traits.Contains((int)t);
        public bool Has(TrinketKind k) => trinkets.Contains((int)k);
        public bool Has(Bond b) => bonds.Contains((int)b);
        public bool Has(Unlockable u) => u.Kind switch
        {
            UnlockKind.Form => Has((FormKind)u.Id), UnlockKind.Trait => Has((Trait)u.Id), _ => Has((TrinketKind)u.Id),
        };

        public void Unlock(Unlockable u)
        {
            var list = u.Kind switch { UnlockKind.Form => forms, UnlockKind.Trait => traits, _ => trinkets };
            if (!list.Contains(u.Id)) list.Add(u.Id);
            Dirty = true;
        }

        /// <summary>Сколько карт можно получить: открытые формы × открытые свойства.</summary>
        public int CardCount => forms.Count * traits.Count;

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static MetaProgress FromJson(string json)
        {
            try
            {
                var m = JsonUtility.FromJson<MetaProgress>(json);
                return m ?? Starter();
            }
            catch (ArgumentException) { return Starter(); }
        }
    }

    /// <summary>Забег в летописи.</summary>
    [Serializable]
    public sealed class RunRecord
    {
        public int floor;      // до какого этажа дошёл (1–5, за кругом — больше)
        public bool won;       // вышел с пятого
        public int kills, rooms;
        public float time;     // секунд
    }

    public enum UnlockKind { Form, Trait, Trinket }

    /// <summary>Что можно открыть у Торговца: форма, свойство или тринкет.</summary>
    public readonly struct Unlockable
    {
        public readonly UnlockKind Kind;
        public readonly int Id;

        public Unlockable(UnlockKind kind, int id) { Kind = kind; Id = id; }

        public string Name => Kind switch
        {
            UnlockKind.Form => FormText.Name((FormKind)Id), UnlockKind.Trait => FormText.Name((Trait)Id), _ => TrinketBook.Name((TrinketKind)Id),
        };

        public string KindName => Kind switch { UnlockKind.Form => "Форма", UnlockKind.Trait => "Свойство", _ => "Тринкет" };

        /// <summary>Цена в Сардисе: свойство 1, форма 2, тринкет 5.</summary>
        public int Price => Kind switch { UnlockKind.Trait => 1, UnlockKind.Form => 2, _ => 5 };
    }

    public static class MetaShop
    {
        /// <summary>Всё, что ещё не открыто: формы, свойства, тринкеты — в этом порядке.</summary>
        public static List<Unlockable> Locked(MetaProgress m)
        {
            var list = new List<Unlockable>();
            foreach (FormKind f in Enum.GetValues(typeof(FormKind))) if (!m.Has(f)) list.Add(new Unlockable(UnlockKind.Form, (int)f));
            for (int i = 0; i < Traits.Count; i++) if (!m.Has((Trait)i)) list.Add(new Unlockable(UnlockKind.Trait, i));
            foreach (var k in TrinketBook.All) if (!m.Has(k)) list.Add(new Unlockable(UnlockKind.Trinket, (int)k));
            return list;
        }
    }
}
