using System;
using System.Collections.Generic;

namespace Arena.Core
{
    /// <summary>Форма скилла — что он физически: снаряд, рывок, зона, призыв, барьер.</summary>
    public enum FormKind { Shot, Dash, Zone, Summon, Barrier }

    /// <summary>Свойство («душа») скилла. Карта-донор при слиянии отдаёт своё свойство основной карте, форма остаётся от основной.</summary>
    public enum Trait
    {
        Fire, Frost, Lightning, Rot, Doom,         // статусы
        Ricochet, Split, Pierce, Return, Echo,     // геометрия
        Pull, Push, Blast, Reap, Grow,             // сила
        Homing, Orbit, Trigger,                    // траектория и спуск (Noita, Nova Drift) — в конце, номера прежних не сдвигаются
        Kinetic, Snipe, Siphon,                    // разгон, дальнобой, поглощение (Nova Drift: Kinetic Boost, Snipe, Siphon)
    }

    public static class Traits
    {
        /// <summary>Сколько всего свойств — под веса выпадения и перебор.</summary>
        public static readonly int Count = Enum.GetValues(typeof(Trait)).Length;
    }

    /// <summary>Связка — два свойства на одной карте (своё или влитые души) дают третий эффект, как синергии Nova Drift.
    /// Работает, пока на карте есть оба свойства; уровень свойств на связку не влияет.</summary>
    public enum Bond
    {
        Napalm,          // Пламя + Взрыв
        Icebreaker,      // Иней + Отталкивание
        Contagion,       // Молния + Гниль
        DoubleDoom,      // Приговор + Эхо
        Implosion,       // Притяжение + Взрыв
        Shrapnel,        // Рикошет + Раздвоение
        ChainDetonation, // Жатва + Взрыв
        Superconductor,  // Иней + Молния
        ThermalShock,    // Пламя + Иней
        SmartRicochet,   // Наведение + Рикошет
        Millstone,       // Орбита + Пронзание
        Purification,    // Поглощение + Пламя
        Hunter,          // Дальнобой + Наведение
    }

    public static class Bonds
    {
        public static readonly Bond[] All = (Bond[])Enum.GetValues(typeof(Bond));

        public static (Trait a, Trait b) Pair(Bond b) => b switch
        {
            Bond.Napalm => (Trait.Fire, Trait.Blast),
            Bond.Icebreaker => (Trait.Frost, Trait.Push),
            Bond.Contagion => (Trait.Lightning, Trait.Rot),
            Bond.DoubleDoom => (Trait.Doom, Trait.Echo),
            Bond.Implosion => (Trait.Pull, Trait.Blast),
            Bond.Shrapnel => (Trait.Ricochet, Trait.Split),
            Bond.ChainDetonation => (Trait.Reap, Trait.Blast),
            Bond.Superconductor => (Trait.Frost, Trait.Lightning),
            Bond.ThermalShock => (Trait.Fire, Trait.Frost),
            Bond.SmartRicochet => (Trait.Homing, Trait.Ricochet),
            Bond.Millstone => (Trait.Orbit, Trait.Pierce),
            Bond.Purification => (Trait.Siphon, Trait.Fire),
            _ => (Trait.Snipe, Trait.Homing),
        };

        public static string Name(Bond b) => b switch
        {
            Bond.Napalm => "Напалм", Bond.Icebreaker => "Ледокол", Bond.Contagion => "Зараза", Bond.DoubleDoom => "Двойной приговор",
            Bond.Implosion => "Схлопывание", Bond.Shrapnel => "Шрапнель", Bond.ChainDetonation => "Цепная детонация",
            Bond.Superconductor => "Сверхпроводник", Bond.ThermalShock => "Тепловой удар", Bond.SmartRicochet => "Умный рикошет",
            Bond.Millstone => "Жернова", Bond.Purification => "Очищение", _ => "Охотник",
        };

        public static string Text(Bond b) => b switch
        {
            Bond.Napalm => "взрывы карты поджигают всех задетых",
            Bond.Icebreaker => "заиндевевший или замёрзший враг бьётся об стену или врага втрое сильнее",
            Bond.Contagion => "молния карты заражает гнилью всех, в кого попала",
            Bond.DoubleDoom => "приговор срабатывает второй раз через 1 с (50%)",
            Bond.Implosion => "взрыв шире в 1.5 раза и стягивает выживших к центру",
            Bond.Shrapnel => "отскок снаряда выпускает осколок (50%)",
            Bond.ChainDetonation => "враг, добитый картой, взрывается — взрыв может добить следующих",
            Bond.Superconductor => "молния по заиндевевшему или замёрзшему врагу — урон ×2",
            Bond.ThermalShock => "поджог замёрзшего врага раскалывает лёд: весь запас горения — сразу",
            Bond.SmartRicochet => "отскок от стены летит в ближайшего врага",
            Bond.Millstone => "орбита шире в 1.5 раза и бьёт каждого вдвое чаще",
            Bond.Purification => "добивание горящего врага лечит 4% здоровья",
            _ => "снаряд с наведением считает путь для дальнобоя вдвое",
        };

        public static bool Has(SkillCard c, Bond b)
        {
            var (x, y) = Pair(b);
            return c != null && c.Has(x) && c.Has(y);
        }

        /// <summary>Связки, которые открыты на карте.</summary>
        public static List<Bond> Of(SkillCard c)
        {
            var list = new List<Bond>();
            foreach (var b in All) if (Has(c, b)) list.Add(b);
            return list;
        }

        /// <summary>Связки, которые откроются, если влить в карту это свойство (их ещё нет).</summary>
        public static List<Bond> Opens(SkillCard c, Trait soul)
        {
            var list = new List<Bond>();
            if (c == null) return list;
            foreach (var b in All)
            {
                if (Has(c, b)) continue;
                var (x, y) = Pair(b);
                if ((x == soul && c.Has(y)) || (y == soul && c.Has(x))) list.Add(b);
            }
            return list;
        }
    }


    /// <summary>Редкость карты. Одна и та же карта бывает разной редкости и отличается только числом вливаний
    /// (ячеек душ): редкая — 2, эпическая — 3, легендарная — 4.</summary>
    public enum Rarity { Rare, Epic, Legendary }

    public static class Rarities
    {
        public static int Cells(Rarity r) => r switch { Rarity.Rare => 2, Rarity.Epic => 3, _ => 4 };

        public static string Name(Rarity r) => r switch { Rarity.Rare => "Редкая", Rarity.Epic => "Эпическая", _ => "Легендарная" };

        /// <summary>Редкость по весам (по порядку Rare, Epic, Legendary); веса не обязаны давать в сумме 100.</summary>
        public static Rarity Roll(Random rng, float[] weights)
        {
            float sum = 0f;
            foreach (var w in weights) sum += Math.Max(0f, w);
            if (sum <= 0f) return Rarity.Rare;
            double x = rng.NextDouble() * sum;
            for (int i = 0; i < weights.Length && i <= (int)Rarity.Legendary; i++)
            {
                x -= Math.Max(0f, weights[i]);
                if (x < 0) return (Rarity)i;
            }
            return Rarity.Rare;
        }
    }

    /// <summary>Карта скилла: форма, своё свойство, редкость и влитые души — столько, сколько ячеек даёт редкость.
    /// Каждая душа занимает ячейку, одинаковые свойства складываются в уровень.</summary>
    public sealed class SkillCard
    {
        /// <summary>Больше ячеек не бывает (легендарная) — под столько размечен интерфейс.</summary>
        public const int MaxSoulCells = 4;

        public FormKind Form;
        public Trait? Own;
        public Rarity Rarity;
        public readonly List<Trait> Souls = new();

        public SkillCard(FormKind form, Trait? own = null, Rarity rarity = Rarity.Rare) { Form = form; Own = own; Rarity = rarity; }

        /// <summary>Сколько душ можно влить: зависит от редкости.</summary>
        public int SoulCells => Rarities.Cells(Rarity);

        public int Level(Trait t)
        {
            int n = Own == t ? 1 : 0;
            foreach (var s in Souls) if (s == t) n++;
            return n;
        }

        public bool Has(Trait t) => Level(t) > 0;
        public bool CanFuse => Souls.Count < SoulCells;

        /// <summary>Свойства без повторов, в порядке получения.</summary>
        public List<Trait> Traits
        {
            get
            {
                var list = new List<Trait>();
                if (Own.HasValue) list.Add(Own.Value);
                foreach (var s in Souls) if (!list.Contains(s)) list.Add(s);
                return list;
            }
        }

        public string Name => Own.HasValue ? $"{FormText.Name(Form)} · {FormText.Name(Own.Value)}" : FormText.Name(Form);
    }

    /// <summary>Числа форм и свойств. База — Hades II (Мелиноя): снаряд — атака факелов Игния (WeaponTorch, ProjectileTorchBall),
    /// рывок — WeaponBlink, зона — круг каста (WeaponCast), статусы — Scorch Гестии (BurnEffect), Hitch Геры (DamageShareEffect),
    /// заморозка Деметры (ChillEffect). Источники: PlayerWeapons.sjson, PlayerProjectiles.sjson, EffectData.lua, TraitData_Hestia.lua.
    /// Расстояния × HadesScale.Unit, урон × HadesScale.Hp — как у посоха: враги умирают за то же число попаданий, что в Hades.
    /// Где в Hades прямого аналога нет (призыв, барьер, геометрия), числа — оценка, это отмечено.</summary>
    [Serializable]
    public sealed class FormTuning
    {
        /// <summary>Копия — под итоговые числа с тринкетами (TrinketBook.Apply), база не меняется.</summary>
        public FormTuning Clone() => (FormTuning)MemberwiseClone();

        const float U = HadesScale.Unit;
        const float D = HadesScale.Hp;
        const float F = 1f / 60f;

        // ---------- Снаряд: атака факелов (WeaponTorch → ProjectileTorchBall) ----------
        public float shotInterval = 0.38f;        // Cooldown между выстрелами (у факелов — шаг очереди)
        public float shotMoveSlow = 0.35f;        // TorchAttackChargeSpeed / TorchAttackFireSpeed: скорость × 0.35
        public float shotFireSlowTime = 0.1f;     // … 0.1 с после каждого выстрела
        public float shotDamage = 30f * D;        // Damage 30
        public float shotSpeed = 1250f * U;       // Speed 1250
        public float shotAccel = -450f * U;       // Acceleration −450 — снаряд тормозит
        public float shotFuse = 0.74f;            // TotalFuse — ≈ 6.1 юнита полёта
        public float shotRadius = 50f * U;        // Points (25, ±50), (−75, 0)
        public float shotStun = 0.3f;             // OnHitStun 0.3
        // наведение — как у Адамантовой рельсы Hades (GunWeapon, тоже очередь при удержании): AutoLockRange 710,
        // AutoLockArcDistance 360 — во все стороны, AutoLockWithMouse не выключен — с мышью тоже наводится
        public float shotLockRange = 710f * U;
        public float shotLockArc = 180f;          // ± от направления прицела; 180 — во все стороны
        public bool shotLockWithMouse = true;
        public float shotBounceFuse = 0.4f;       // оценка: рикошет продлевает полёт (JumpSpeedMultiplier 0.8 в Hades)
        // заряд (атакующий — стоишь): отпустил в окне — очередь из двух снарядов (урон ×2)
        public float shotChargeTime = 0.6f;       // порог: столько держать до окна (у лука Hades ChargeTime 0.6)
        public float shotChargeWindow = 0.2f;     // окно после порога — только в нём усиление (у лука «идеальное» окно 0.15)
        public float shotChargeMove = 0f;         // скорость бега во время заряда: 0 — стоит, 1 — полная
        public int shotVolley = 1;                // + столько снарядов очереди при усилении
        public float shotVolleyInterval = 0.1f;

        // ---------- Заряд удержанием — общие правила ----------
        // Нажал и отпустил — обычное применение; держишь — применяется при отпускании (как лук Hades, FireOnRelease).
        // Усиление (×2) — только если отпустил в окне после порога; раньше порога или после окна — обычное применение.
        // Порог, окно и скорость бега во время заряда — в разделе каждой формы: атакующие (снаряд, зона, призыв) — стоишь,
        // защитные (рывок, барьер) — ходишь на полной скорости.

        // ---------- Рывок: WeaponBlink ----------
        public float dashRange = 320f * U;        // WeaponRange 320
        public float dashTime = 0.2f;             // BlinkDuration
        public float dashCooldown = 0.74f;        // ClipRegenInterval — с конца рывка
        public float dashInvuln = 16 * F;         // неуязвимость после выстрела рывка
        public float dashTouch = 0.6f;            // радиус касания врагов по пути (сквозь врагов — NoUnitCollision)
        public float dashPierceDamage = 30f * D;  // Пронзание: урон удара из рывка (WeaponStaffDash 30)
        public float dashPierceStun = 0.5f;
        public float dashPierceRange = 1.3f;
        public float dashLavaReach = 400f / 300f; // рывок Загрея (RushWeapon): WeaponRange 300, BlinkMaxRange 400 — если рывок
                                                  // кончается в лаве, он тянется до берега, но не дальше ×4/3 (оценка смысла BlinkMaxRange)
        // заряд (защитный — ходишь): отпустил в окне — дальность ×2 (прыжок через широкую лаву)
        public float dashChargeTime = 0.6f;
        public float dashChargeWindow = 0.2f;
        public float dashChargeMove = 1f;
        public float dashChargeRange = 1f;        // дальность ×(1 + это) при усилении

        // ---------- Зона: круг каста (WeaponCast) с уроном ----------
        public float zoneRadius = 2f;             // оценка: круг каста в Hades — 430 (3.26 ю) вокруг себя, наш ставится в точку прицела
        public float zoneLife = 3f;               // Fuse 3
        public float zoneCooldown = 0.2f;         // Cooldown 0.2 и ActiveProjectileCap 1: новая — когда прежняя исчезла
        public float zoneTick = 0.5f;             // оценка
        public float zoneDamage = 5f * D;         // оценка: каст в Hades не бьёт, здесь — слабый урон каждые 0.5 с
        public float zoneSlow = 0.5f;             // оценка: каст в Hades держит врагов (ImpactSlow × 0.05), здесь — замедление
        public float zoneCastRange = 6f;          // дальность постановки
        // наведение — как у каста Hades (RangedWeapon): AutoLockRange 1000, AutoLockArcDistance 75, AutoLockWithMouse = false —
        // с мышью зона ставится ровно в курсор
        public float zoneLockRange = 1000f * U;
        public float zoneLockArc = 75f;
        public float zoneRiftWidth = 0.7f;        // Пронзание: полуширина разлома
        // заряд (атакующий — стоишь): отпустил в окне — площадь ×2 (радиус ×√2)
        public float zoneChargeTime = 0.6f;
        public float zoneChargeWindow = 0.2f;
        public float zoneChargeMove = 0f;
        public float zoneChargeArea = 1f;         // площадь ×(1 + это) при усилении

        // ---------- Призыв: дух-слуга (оценка; фамильяры Hades II — PostAttackCooldown 4.5 с) ----------
        public float summonLife = 8f;
        public float summonCooldown = 10f;        // с момента призыва
        public float summonFireInterval = 0.8f;
        public float summonDamage = 10f * D;      // ≈ треть снаряда факела
        public float summonRange = 6f;
        public float summonOffset = 1.2f;         // держится сбоку-сзади героя
        public float summonSpeed = 5f;
        public float summonRamDamage = 20f * D;   // Пронзание: таран (ProjectileTorchOrbit 20)
        public float summonRamInterval = 1.1f;
        // заряд (атакующий — стоишь): отпустил в окне — дух живёт ×2
        public float summonChargeTime = 0.6f;
        public float summonChargeWindow = 0.2f;
        public float summonChargeMove = 0f;
        public float summonChargeLife = 1f;       // время жизни ×(1 + это) при усилении

        // ---------- Барьер (оценка; у Мелинои блока нет) ----------
        public float barrierLife = 2f;
        public float barrierCooldown = 8f;        // с конца барьера
        public int barrierHits = 3;               // прочность: столько ударов поглощает
        public float barrierRadius = 0.9f;
        // заряд (защитный — ходишь): отпустил в окне — прочность ×2 (3 → 6 ударов)
        public float barrierChargeTime = 0.6f;
        public float barrierChargeWindow = 0.2f;
        public float barrierChargeMove = 1f;
        public int barrierChargeHits = 3;         // + столько ударов при усилении

        // ---------- Свойства ----------
        public float burn = 30f * D;              // Scorch Гестии: атака даёт 30 стаков (TraitData_Hestia BaseValue 30)
        public float burnTime = 3f;               // оценка темпа: запас Scorch выгорает за 3 с с последнего поджога
        public float frostSlowPerStack = 0.15f;   // оценка
        public float frostTime = 2f;
        public int frostToFreeze = 3;
        public float freezeTime = 1.5f;           // ChillEffect Деметры — время врага × 0.01; у нас — заморозка на 1.5 с
        public int chainTargets = 2;              // оценка: молния перескакивает на 2 врагов
        public float chainRange = 3f;
        public float chainFraction = 0.4f;
        public float chainMin = 10f * D;
        public float rotVuln = 0.25f;             // +25% получаемого урона
        public float rotTime = 4f;
        public float rotSpread = 2.5f;
        public float doomTime = 3f;               // оценка: Doom Ареса в Hades I срабатывает через 1.1 с
        public float doomFraction = 0.5f;
        public float linkShare = 0.3f;            // Hitch Геры: Amount 0.30 урона уходит связанным
        public float linkTime = 1f;               // держится, пока враг в зоне, и ещё 1 с
        public int ricochetBounces = 2;
        public float splitAngle = 12f;
        public float summonSplit = 0.6f;
        public float echoDelay = 1f;
        public float echoFraction = 0.5f;
        public float returnDelay = 1.5f;          // рывок: откат в точку старта
        public float pullRadius = 2f;
        public float pullTime = 1f;
        public float pullSpeed = 3.5f;
        public float pushSpeed = 9f;              // как Push у DandyAce
        public float pushTime = 0.25f;
        public float pushCollide = 15f * D;       // удар о стену или врага
        public float blastRadius = 1.5f;
        public float blastDamage = 40f * D;       // оценка: Ω-снаряд посоха — 80
        public float reapHaste = 1.5f;            // снаряд: двойная скорострельность после добивания
        public float growPerHit = 0.25f;
        public float growMax = 2.5f;
        public float growDashRange = 0.2f;
        public float growBarrierTime = 0.3f;
        public int maxShotsPerSkill = 24;         // предел снарядов одного скилла на экране (Рикошет + Раздвоение + Эхо)
        public float reapLockout = 0.3f;          // Жатва срабатывает не чаще раза в 0.3 с

        // ---------- Наведение, Орбита, Спуск (оценка; Noita — Homing, Orbiting Arc, Trigger; Nova Drift — орбиталы) ----------
        public float homingRange = 5f;            // кого видит наведение
        public float homingTurn = 300f;           // доворот снаряда, °/с на уровень
        public float homingDashArc = 45f;         // рывок доворачивает к врагу в ± стольких градусах (+15° за уровень)
        public float homingZoneSpeed = 1.5f;      // зона ползёт к врагу, ю/с на уровень
        public float orbitRadius = 1.6f;          // орбита вокруг Смерти
        public float orbitSpeed = 300f;           // °/с
        public float orbitLife = 1.5f;            // снаряд на орбите живёт столько (+1 с за уровень)
        public float orbitRehit = 0.6f;           // осколок бьёт того же врага не чаще
        public float orbitShardDamage = 10f * D;  // осколок рывка и барьера
        public float orbitDashTime = 2f;          // осколки после рывка
        public float orbitSpiritRam = 8f * D;     // дух на орбите таранит касанием
        public float triggerFraction = 0.4f;      // Спуск: сила карты следующего слота — 40% за уровень (до 100%)
        public float triggerLockout = 0.5f;       // Спуск одного слота — не чаще раза в 0.5 с

        // ---------- Связки (Bonds) ----------
        public float bondIcebreaker = 3f;         // Ледокол: удар о стену ×3
        public float bondDoubleDoom = 0.5f;       // Двойной приговор: второй раз — доля первого
        public float bondImplosion = 1.5f;        // Схлопывание: радиус взрыва ×1.5
        public float bondShrapnel = 0.5f;         // Шрапнель: урон осколка
        public float bondChainDetonation = 0.5f;  // Цепная детонация: доля взрыва карты
        public float bondSuperconductor = 2f;     // Сверхпроводник: урон молнии по замёрзшему
        public float bondMillstone = 1.5f;        // Жернова: радиус орбиты
        public float bondPurification = 0.04f;    // Очищение: доля здоровья за добитого горящего

        // ---------- Разгон, Дальнобой, Поглощение (оценка; Nova Drift — Kinetic Boost, Snipe, Siphon, Purification) ----------
        public float kineticRunUp = 0.6f;         // снаряд: +60% урона за каждые kineticRunDist пробежанных с прошлого выстрела
        public float kineticRunDist = 4f;         // … (рывок считается), не больше одного раза
        public float kineticRam = 15f * D;        // рывок: урон задетым за каждую пройденную ю
        public float kineticMove = 0.5f;          // зона и дух: до +50% урона, пока Смерть бежит (по доле скорости)
        public float kineticBarrierRam = 20f * D; // барьер: таран касанием на полном бегу
        public float kineticBarrierRehit = 0.5f;
        public float snipePerUnit = 0.15f;        // +15% урона за ю расстояния
        public float snipeMaxDist = 6f;           // … до 6 ю (+90%)
        public float snipeRange = 9f;             // рывок и барьер: выстрел в самого дальнего врага в этом радиусе
        public float snipeSummonRange = 1.5f;     // дух видит дальше во столько раз
        public float siphonKill = 0.02f;          // снаряд: добивание лечит 2% здоровья
        public float siphonDash = 0.01f;          // рывок: каждый задетый — 1%
        public float siphonZone = 0.004f;         // зона: каждый враг внутри — 0.4% в секунду
        public float siphonSummon = 0.03f;        // призыв: добивание духом — 3%
        public float siphonBarrier = 0.03f;       // барьер: поглощённый удар — 3%
        public float siphonRoomCap = 0.2f;        // лечения до следующей награды — не больше 20% здоровья
    }

    /// <summary>Тексты: имена, значки, что делает каждое свойство на каждой форме.</summary>
    public static class FormText
    {
        public static string Name(FormKind f) => f switch
        {
            FormKind.Shot => "Снаряд", FormKind.Dash => "Рывок", FormKind.Zone => "Зона", FormKind.Summon => "Призыв", _ => "Барьер",
        };

        public static string Name(Trait t) => t switch
        {
            Trait.Fire => "Пламя", Trait.Frost => "Иней", Trait.Lightning => "Молния", Trait.Rot => "Гниль", Trait.Doom => "Приговор",
            Trait.Ricochet => "Рикошет", Trait.Split => "Раздвоение", Trait.Pierce => "Пронзание", Trait.Return => "Возврат", Trait.Echo => "Эхо",
            Trait.Pull => "Притяжение", Trait.Push => "Отталкивание", Trait.Blast => "Взрыв", Trait.Reap => "Жатва", Trait.Grow => "Рост",
            Trait.Homing => "Наведение", Trait.Orbit => "Орбита", Trait.Trigger => "Спуск",
            Trait.Kinetic => "Разгон", Trait.Snipe => "Дальнобой", _ => "Поглощение",
        };

        public static string Glyph(FormKind f) => f switch
        {
            FormKind.Shot => "➶", FormKind.Dash => "»", FormKind.Zone => "◎", FormKind.Summon => "☠", _ => "◈",
        };

        public static string Base(FormKind f) => f switch
        {
            FormKind.Shot => "Снаряд: 171 урона, оглушение 0.3 с. Заряд (стоишь), отпустить в окне — очередь из двух снарядов.",
            FormKind.Dash => "Рывок на 2.4 сквозь врагов, неуязвимость, над лавой — прыжок. Заряд (можно ходить), отпустить в окне — вдвое дальше.",
            FormKind.Zone => "Круг в точке прицела на 3 с: каждые 0.5 с бьёт стоящих (29) и замедляет вдвое. Заряд (стоишь), отпустить в окне — вдвое больше площадь.",
            FormKind.Summon => "Дух-слуга на 8 с стреляет в ближайшего врага (57 раз в 0.8 с). Перезарядка 10 с. Заряд (стоишь), отпустить в окне — живёт вдвое дольше.",
            _ => "Щит на 2 с: поглощает 3 удара или снаряда. Перезарядка 8 с с конца. Заряд (можно ходить), отпустить в окне — вдвое больше ударов.",
        };

        /// <summary>Что свойство делает на форме (уровень — сколько раз свойство влито).</summary>
        public static string Cell(Trait t, FormKind f)
        {
            int i = (int)f;
            return t switch
            {
                Trait.Fire => new[] { "поджигает цель (Scorch 30)", "огненный след по пути", "горящая земля: поджигает стоящих", "выстрелы духа поджигают", "поджигает бьющих и всех вплотную" }[i],
                Trait.Frost => new[] { "замедляет, 3-е попадание замораживает", "ледяная дорожка: враги на ней замедлены", "каждый тик — иней, на третьем враг замерзает", "при исчезновении духа — ледяной взрыв", "ударивший замерзает" }[i],
                Trait.Lightning => new[] { "перескакивает на 2 врагов (40% урона)", "разряд по линии от старта до финиша", "каждый тик бьёт молнией врага рядом с зоной", "дуга между тобой и духом бьёт всех, кто её пересёк", "каждый поглощённый удар — молния в 2 ближайших" }[i],
                Trait.Rot => new[] { "гниль: +25% урона по цели, умерший заражает соседей", "заражает всех, сквозь кого прошёл", "заражает стоящих внутри", "выстрелы духа заражают", "ударивший заражён, снаряды отражаются гнилыми" }[i],
                Trait.Doom => new[] { "приговор: через 3 с цель получит 50% урона, набранного за это время", "приговор всем на пути", "связывает стоящих: 30% урона одному получают остальные (Hitch Геры)", "дух метит цели, при его исчезновении метки срабатывают", "ударивший получает приговор" }[i],
                Trait.Ricochet => new[] { "+2 отскока от стен и врагов", "упёрся в стену или врага — бесплатный второй рывок", "в конце прыгает к ближайшей группе (60%)", "выстрелы духа отскакивают", "поглощённые снаряды летят во врагов" }[i],
                Trait.Split => new[] { "+1 снаряд веером", "тень делает зеркальный рывок", "вторая зона зеркально от героя", "два духа, каждый по 60%", "копия остаётся на месте стеной и держит снаряды" }[i],
                Trait.Pierce => new[] { "проходит сквозь врагов", "урон каждому задетому (171), дальность × 1.3", "становится разломом-линией от тебя до точки", "дух таранит врагов вместо стрельбы", "при активации расходится режущее кольцо" }[i],
                Trait.Return => new[] { "в конце летит обратно и бьёт ещё раз", "через 1.5 с откат в точку старта с уроном по пути", "в конце стягивается к герою, нанося урон по пути", "выстрелы духа возвращаются, задевая ещё раз", "через 2 с отправляет поглощённые снаряды обратно" }[i],
                Trait.Echo => new[] { "через 1 с снаряд-эхо из той же точки (50%)", "тень повторяет рывок через 1 с", "вспыхивает повторно (50%)", "после духа появляется эхо-дух на 4 с", "сломался — включается ещё раз на 1 с" }[i],
                Trait.Pull => new[] { "вихрь в точке попадания", "тянет врагов вокруг пути к точке финиша", "воронка: тянет к центру", "дух притягивает врагов к себе", "при активации тянет к тебе врагов и снаряды" }[i],
                Trait.Push => new[] { "отброс: удар о стену или врага наносит урон", "таран: враги разлетаются", "ударная волна при появлении", "выстрелы духа отбрасывают", "при активации отбрасывает всех вокруг" }[i],
                Trait.Blast => new[] { "взрыв при попадании (229)", "взрыв в точке финиша", "детонация в конце", "дух-камикадзе: летит в толпу и взрывается", "разрушаясь, взрывается: сильнее за каждый удар" }[i],
                Trait.Reap => new[] { "добивание: 1.5 с вдвое быстрее выстрелы и заряд", "добивание рывком сразу восстанавливает рывок", "добивание продлевает зону на 1 с", "добивание сбрасывает перезарядку призыва", "добивание при барьере: +1 прочность" }[i],
                Trait.Grow => new[] { "растёт с каждым попаданием: +25% урона и размера", "каждый задетый враг: +20% дальности", "расширяется с каждым новым врагом внутри", "растёт с каждым добиванием", "каждый поглощённый удар: +0.3 с и больше" }[i],
                Trait.Homing => new[] { "доворачивает к ближайшему врагу", "доворачивает к врагу впереди (±45°)", "ползёт к ближайшему врагу", "выстрелы духа доворачивают к врагу", "поглощённые снаряды летят обратно в ближайшего врага" }[i],
                Trait.Orbit => new[] { "кружит вокруг тебя 1.5 с, задевая всех", "после рывка вокруг тебя 2 с кружат осколки", "аура: зона всегда вокруг тебя", "дух кружит вокруг тебя и таранит касанием", "вокруг щита кружат 2 осколка" }[i],
                Trait.Trigger => new[] { "попадание применяет карту следующего слота в цель (40%)", "в конце рывка — карта следующего слота (40%)", "исчезая, применяет карту следующего слота (40%)", "попадание духа — карта следующего слота (40%)", "поглощённый удар — карта следующего слота (40%)" }[i],
                Trait.Kinetic => new[] { "разбег: +60% урона за каждые 4 ю, пробежанные с прошлого выстрела (рывок считается)", "таран: задетые получают урон по пройденному пути (86 за ю)", "пока бежишь, бьёт до +50% сильнее", "пока бежишь, дух бьёт до +50% сильнее", "пока бежишь, щит таранит касанием (до 114)" }[i],
                Trait.Snipe => new[] { "+15% урона за каждую ю полёта (до +90%)", "в конце — выстрел в самого дальнего врага в 9 ю, урон растёт с расстоянием", "+15% урона за каждую ю от тебя до зоны (до +90%)", "дух бьёт издалека: дальность ×1.5, +15% за ю до цели", "поглощённый удар — выстрел в самого дальнего врага, урон растёт с расстоянием" }[i],
                _ => new[] { "добивание лечит 2% здоровья", "каждый задетый враг лечит 1% здоровья", "вытягивает жизнь: каждый враг внутри лечит 0.4% в секунду", "добивание духом лечит 3% здоровья", "каждый поглощённый удар лечит 3% здоровья" }[i],
            };
        }

        /// <summary>Описание карты: база формы и все её свойства с уровнями.</summary>
        public static List<string> Describe(SkillCard c)
        {
            var lines = new List<string> { Base(c.Form) };
            foreach (var t in c.Traits)
            {
                int lvl = c.Level(t);
                lines.Add($"{Name(t)}{(lvl > 1 ? $" ×{lvl}" : "")}: {Cell(t, c.Form)}");
            }
            foreach (var b in Bonds.Of(c)) lines.Add(BondLine(b));
            if (c.Has(Trait.Siphon)) lines.Add("Лечение до следующей награды — не больше 20% здоровья.");
            return lines;
        }

        /// <summary>Связка одной строкой: «Связка «Напалм» (Пламя + Взрыв): …».</summary>
        public static string BondLine(Bond b)
        {
            var (x, y) = Bonds.Pair(b);
            return $"Связка «{Bonds.Name(b)}» ({Name(x)} + {Name(y)}): {Bonds.Text(b)}";
        }
    }
}
