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
    }

    /// <summary>Карта скилла: форма, своё свойство и до двух влитых душ. Одинаковые свойства складываются в уровень.</summary>
    public sealed class SkillCard
    {
        public const int SoulCells = 2;

        public FormKind Form;
        public Trait? Own;
        public readonly List<Trait> Souls = new();

        public SkillCard(FormKind form, Trait? own = null) { Form = form; Own = own; }

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

        // ---------- Заряд удержанием — по луку Hades (Coronacht, BowWeapon) ----------
        // Все формы: нажал и отпустил — обычное применение; держишь — копится заряд, применяется при отпускании (FireOnRelease).
        public float chargeTime = 0.6f;           // ChargeTime — полный заряд
        public float chargeMin = 0.2f;            // MinChargeToFire — раньше 0.2 заряда отпустил — обычное применение
        public float perfectWindow = 0.15f;       // PerfectChargeWindowDuration — «идеальный» выпуск сразу после полного заряда
        public float perfectDamage = 1.167f;      // PerfectChargeDamageMultiplier
        public float chargeMoveSlow = 0.5f;       // бег во время заряда (оценка; у лука — замедление BowSelfSpeed)
        // что даёт заряд (0…1) каждой форме — наш выбор, в Hades таких форм нет:
        public int shotVolley = 2;                // снаряд: при отпускании — очередь из 1 + до 2 снарядов («идеальный» — ещё +1)
        public float shotVolleyInterval = 0.1f;
        public float shotChargeDamage = 0.5f;     // … и каждый снаряд очереди бьёт ×(1 + 0.5 × заряд)
        public float dashChargeRange = 1f;        // рывок: дальность ×(1 + заряд) — до двух рывков, прыжок через лаву
        public float dashChargeDamage = 0.5f;     // … урон задетым ×(1 + 0.5 × заряд)
        public float zoneChargeRadius = 0.75f;    // зона: радиус ×(1 + 0.75 × заряд)
        public float summonChargeLife = 1f;       // призыв: дух живёт ×(1 + заряд)
        public float summonChargeDamage = 0.5f;   // … и стреляет ×(1 + 0.5 × заряд)
        public int barrierChargeHits = 3;         // барьер: + до 3 поглощённых ударов («идеальный» — ещё +1)
        public float barrierChargeLife = 0.5f;    // … и держится ×(1 + 0.5 × заряд)

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

        // ---------- Барьер (оценка; у Мелинои блока нет) ----------
        public float barrierLife = 2f;
        public float barrierCooldown = 8f;        // с конца барьера
        public int barrierHits = 3;               // прочность: столько ударов поглощает
        public float barrierRadius = 0.9f;

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
            Trait.Pull => "Притяжение", Trait.Push => "Отталкивание", Trait.Blast => "Взрыв", Trait.Reap => "Жатва", _ => "Рост",
        };

        public static string Glyph(FormKind f) => f switch
        {
            FormKind.Shot => "➶", FormKind.Dash => "»", FormKind.Zone => "◎", FormKind.Summon => "☠", _ => "◈",
        };

        public static string Base(FormKind f) => f switch
        {
            FormKind.Shot => "Снаряд: 171 урона, оглушение 0.3 с. Держать — заряд: очередь до 3 снарядов, каждый до ×1.5 урона.",
            FormKind.Dash => "Рывок на 2.4 сквозь врагов, неуязвимость, над лавой — прыжок. Держать — заряд: до вдвое дальше.",
            FormKind.Zone => "Круг в точке прицела на 3 с: каждые 0.5 с бьёт стоящих (29) и замедляет вдвое. Держать — заряд: шире до ×1.75.",
            FormKind.Summon => "Дух-слуга на 8 с стреляет в ближайшего врага (57 раз в 0.8 с). Перезарядка 10 с. Держать — заряд: живёт до 16 с, бьёт до ×1.5.",
            _ => "Щит на 2 с: поглощает 3 удара или снаряда. Перезарядка 8 с с конца. Держать — заряд: до 6 ударов и до 3 с.",
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
                _ => new[] { "растёт с каждым попаданием: +25% урона и размера", "каждый задетый враг: +20% дальности", "расширяется с каждым новым врагом внутри", "растёт с каждым добиванием", "каждый поглощённый удар: +0.3 с и больше" }[i],
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
            return lines;
        }
    }
}
