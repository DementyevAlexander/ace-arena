using System;
using System.Collections.Generic;

namespace Arena.Core
{
    /// <summary>Тринкет усиливает категорию: свойство, форму или общую механику (docs/trinkets.md).</summary>
    public enum TrinketKind
    {
        Ember, IceShard, LightningRod, RotBone, DoomSand, Mirror, TuningFork, PowderHorn, // свойства
        Quiver, Feather, Compass, Bell, Ward,                                            // формы
        Hourglass, SoulVessel, TimeSand,                                                 // механики
    }

    public enum TrinketGroup { Trait, Form, Mechanic }

    /// <summary>Тринкет в слоте или на пятачке: вид и уровень (I–V).</summary>
    public sealed class Trinket
    {
        public TrinketKind Kind;
        public int Level;

        public Trinket(TrinketKind kind, int level = 1) { Kind = kind; Level = level; }
    }

    /// <summary>Числа тринкетов: прибавка уровня I. На уровнях I–V она умножается на levelMul (1, 2, 2, 3, 4) —
    /// III чисел не добавляет, зато даёт особый эффект (пока не сделан). Все числа — оценка.</summary>
    [Serializable]
    public sealed class TrinketTuning
    {
        public float[] levelMul = { 1f, 2f, 2f, 3f, 4f };
        public int offerCount = 3;           // мини-босс и босс: столько тринкетов на выбор
        public int miniBossLevel = 1;        // уровень предложенных у мини-босса
        public int bossLevel = 2;            // и у босса
        public float traitDropWeight = 3f;   // тринкет свойства: карты с этим свойством выпадают в 3 раза чаще
        public float formDropWeight = 2f;    // тринкет формы: карты этой формы — в 2 раза чаще
        public float minCooldownMul = 0.25f; // перезарядка не короче четверти базовой

        public float ember = 0.4f;           // Уголёк: стаки горения +40%
        public float iceShard = 0.5f;        // Осколок льда: заморозка +0.5 с
        public int lightningRod = 1;         // Громоотвод: +1 перескок
        public float rotBone = 0.1f;         // Гнилая кость: уязвимость +10 п. п.
        public float doomSand = 0.15f;       // Песок приговора: доля +15 п. п.
        public int mirror = 1;               // Зеркальце: +1 отскок
        public float tuningFork = 0.15f;     // Камертон: эхо +15 п. п.
        public float powderHorn = 0.25f;     // Пороховница: радиус взрыва +25%
        public float quiver = 0.15f;         // Колчан: урон снаряда +15%
        public float feather = 0.15f;        // Перо: перезарядка рывка −15%
        public float compass = 0.15f;        // Компас: радиус зоны +15%
        public float bell = 2f;              // Колокольчик: дух живёт +2 с
        public int ward = 1;                 // Щит-оберег: барьер +1 удар
        public float hourglass = 0.1f;       // Песочные часы: окно заряда +0.1 с
        public float soulVessel = 0.05f;     // Сосуд душ: каждая влитая душа +5% урона карты
        public float timeSand = 0.08f;       // Песок времени: перезарядка всех форм −8%

        public float Mul(int level) => levelMul[Math.Clamp(level, 1, levelMul.Length) - 1];
    }

    /// <summary>Каталог тринкетов: категория, тексты, итоговые числа форм с тринкетами и веса выпадения карт.</summary>
    public static class TrinketBook
    {
        public const int Slots = 3, MaxLevel = 5;

        public static readonly TrinketKind[] All = (TrinketKind[])Enum.GetValues(typeof(TrinketKind));

        public static TrinketGroup Group(TrinketKind k) =>
            k <= TrinketKind.PowderHorn ? TrinketGroup.Trait : k <= TrinketKind.Ward ? TrinketGroup.Form : TrinketGroup.Mechanic;

        public static Trait? TraitOf(TrinketKind k) => k switch
        {
            TrinketKind.Ember => Trait.Fire, TrinketKind.IceShard => Trait.Frost, TrinketKind.LightningRod => Trait.Lightning,
            TrinketKind.RotBone => Trait.Rot, TrinketKind.DoomSand => Trait.Doom, TrinketKind.Mirror => Trait.Ricochet,
            TrinketKind.TuningFork => Trait.Echo, TrinketKind.PowderHorn => Trait.Blast, _ => null,
        };

        public static FormKind? FormOf(TrinketKind k) => k switch
        {
            TrinketKind.Quiver => FormKind.Shot, TrinketKind.Feather => FormKind.Dash, TrinketKind.Compass => FormKind.Zone,
            TrinketKind.Bell => FormKind.Summon, TrinketKind.Ward => FormKind.Barrier, _ => null,
        };

        /// <summary>Открыт ли с начала. Открытие покупкой в хабе — когда будут хаб и сохранения; пока открыты все.</summary>
        public static bool UnlockedAtStart(TrinketKind k) => true;

        public static string Name(TrinketKind k) => k switch
        {
            TrinketKind.Ember => "Уголёк", TrinketKind.IceShard => "Осколок льда", TrinketKind.LightningRod => "Громоотвод",
            TrinketKind.RotBone => "Гнилая кость", TrinketKind.DoomSand => "Песок приговора", TrinketKind.Mirror => "Зеркальце",
            TrinketKind.TuningFork => "Камертон", TrinketKind.PowderHorn => "Пороховница", TrinketKind.Quiver => "Колчан",
            TrinketKind.Feather => "Перо", TrinketKind.Compass => "Компас", TrinketKind.Bell => "Колокольчик",
            TrinketKind.Ward => "Щит-оберег", TrinketKind.Hourglass => "Песочные часы", TrinketKind.SoulVessel => "Сосуд душ",
            _ => "Песок времени",
        };

        /// <summary>Категория словом: свойство, форма или механика.</summary>
        public static string Category(TrinketKind k) =>
            TraitOf(k) is Trait t ? FormText.Name(t) : FormOf(k) is FormKind f ? FormText.Name(f) : k switch
            {
                TrinketKind.Hourglass => "Заряд", TrinketKind.SoulVessel => "Вливание", _ => "Перезарядка",
            };

        public static string Roman(int level) => level switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => "V" };

        /// <summary>Что даёт тринкет на этом уровне — числами.</summary>
        public static string Bonus(TrinketKind k, int level, TrinketTuning c)
        {
            float m = c.Mul(level);
            string P(float v) => (v * 100f).ToString("0");
            string N(float v) => v.ToString("0.##");
            return k switch
            {
                TrinketKind.Ember => $"стаки горения +{P(c.ember * m)}%",
                TrinketKind.IceShard => $"заморозка дольше на {N(c.iceShard * m)} с",
                TrinketKind.LightningRod => $"перескоки молнии +{c.lightningRod * (int)m}",
                TrinketKind.RotBone => $"уязвимость от гнили +{P(c.rotBone * m)} п. п.",
                TrinketKind.DoomSand => $"урон приговора +{P(c.doomSand * m)} п. п.",
                TrinketKind.Mirror => $"отскоки рикошета +{c.mirror * (int)m}",
                TrinketKind.TuningFork => $"сила эха +{P(c.tuningFork * m)} п. п.",
                TrinketKind.PowderHorn => $"радиус взрыва +{P(c.powderHorn * m)}%",
                TrinketKind.Quiver => $"урон снаряда +{P(c.quiver * m)}%",
                TrinketKind.Feather => $"перезарядка рывка −{P(Math.Min(1f - c.minCooldownMul, c.feather * m))}%",
                TrinketKind.Compass => $"радиус зоны +{P(c.compass * m)}%",
                TrinketKind.Bell => $"дух живёт дольше на {N(c.bell * m)} с",
                TrinketKind.Ward => $"барьер держит ударов +{c.ward * (int)m}",
                TrinketKind.Hourglass => $"окно заряда шире на {N(c.hourglass * m)} с",
                TrinketKind.SoulVessel => $"каждая влитая душа: урон карты +{P(c.soulVessel * m)}%",
                _ => $"перезарядка всех форм −{P(Math.Min(1f - c.minCooldownMul, c.timeSand * m))}%",
            };
        }

        /// <summary>Особый эффект уровня III. Пока не сделан — в игре помечен.</summary>
        public static string Special(TrinketKind k) => k switch
        {
            TrinketKind.Ember => "умерший с горением поджигает врагов в 2 ю",
            TrinketKind.IceShard => "заморозка с 2 стаков, а не с 3",
            TrinketKind.LightningRod => "перескоки без потери урона",
            TrinketKind.RotBone => "заражённые получают урон от заражения соседей",
            TrinketKind.DoomSand => "приговор срабатывает через 2 с",
            TrinketKind.Mirror => "каждый отскок +20% урона",
            TrinketKind.TuningFork => "эхо повторяется второй раз",
            TrinketKind.PowderHorn => "взрыв поджигает",
            TrinketKind.Quiver => "каждый 4-й выстрел — сразу очередь",
            TrinketKind.Feather => "конец рывка оставляет зону-след",
            TrinketKind.Compass => "две зоны одновременно",
            TrinketKind.Bell => "дух, исчезая, взрывается",
            TrinketKind.Ward => "поглощённый удар отражается во врага",
            TrinketKind.Hourglass => "усиление ×2.5 вместо ×2",
            TrinketKind.SoulVessel => "каждая вторая душа даёт +1 ячейку",
            _ => "убийство сбрасывает 1 с перезарядки",
        };

        /// <summary>Итоговые числа форм: копия базы, к которой прибавлены все тринкеты (null-слоты пропускаются).</summary>
        public static FormTuning Apply(FormTuning baseCfg, IEnumerable<Trinket> trinkets, TrinketTuning c)
        {
            var f = baseCfg.Clone();
            foreach (var t in trinkets)
            {
                if (t == null) continue;
                float m = c.Mul(t.Level);
                int n = (int)m;
                float Cd(float v) => Math.Max(c.minCooldownMul, 1f - v * m);
                switch (t.Kind)
                {
                    case TrinketKind.Ember: f.burn *= 1f + c.ember * m; break;
                    case TrinketKind.IceShard: f.freezeTime += c.iceShard * m; break;
                    case TrinketKind.LightningRod: f.chainTargets += c.lightningRod * n; break;
                    case TrinketKind.RotBone: f.rotVuln += c.rotBone * m; break;
                    case TrinketKind.DoomSand: f.doomFraction += c.doomSand * m; break;
                    case TrinketKind.Mirror: f.ricochetBounces += c.mirror * n; break;
                    case TrinketKind.TuningFork: f.echoFraction += c.tuningFork * m; break;
                    case TrinketKind.PowderHorn: f.blastRadius *= 1f + c.powderHorn * m; break;
                    case TrinketKind.Quiver: f.shotDamage *= 1f + c.quiver * m; break;
                    case TrinketKind.Feather: f.dashCooldown *= Cd(c.feather); break;
                    case TrinketKind.Compass: f.zoneRadius *= 1f + c.compass * m; break;
                    case TrinketKind.Bell: f.summonLife += c.bell * m; break;
                    case TrinketKind.Ward: f.barrierHits += c.ward * n; break;
                    case TrinketKind.Hourglass:
                        float w = c.hourglass * m;
                        f.shotChargeWindow += w; f.dashChargeWindow += w; f.zoneChargeWindow += w; f.summonChargeWindow += w; f.barrierChargeWindow += w;
                        break;
                    case TrinketKind.SoulVessel: break; // урон карты — SoulDamageMul
                    case TrinketKind.TimeSand:
                        float k = Cd(c.timeSand);
                        f.shotInterval *= k; f.dashCooldown *= k; f.zoneCooldown *= k; f.summonCooldown *= k; f.barrierCooldown *= k;
                        break;
                }
            }
            return f;
        }

        /// <summary>Сосуд душ: множитель урона карты за её влитые души.</summary>
        public static float SoulDamageMul(SkillCard card, IEnumerable<Trinket> trinkets, TrinketTuning c)
        {
            if (card == null || card.Souls.Count == 0) return 1f;
            foreach (var t in trinkets)
                if (t != null && t.Kind == TrinketKind.SoulVessel) return 1f + c.soulVessel * c.Mul(t.Level) * card.Souls.Count;
            return 1f;
        }

        /// <summary>Вес свойства при выпадении карты: ×traitDropWeight за тринкет этого свойства.</summary>
        public static float TraitWeight(Trait trait, IEnumerable<Trinket> trinkets, TrinketTuning c)
        {
            foreach (var t in trinkets) if (t != null && TraitOf(t.Kind) == trait) return c.traitDropWeight;
            return 1f;
        }

        /// <summary>Множитель веса формы при выпадении карты: ×formDropWeight за тринкет этой формы.</summary>
        public static float FormWeight(FormKind form, IEnumerable<Trinket> trinkets, TrinketTuning c)
        {
            foreach (var t in trinkets) if (t != null && FormOf(t.Kind) == form) return c.formDropWeight;
            return 1f;
        }
    }
}
