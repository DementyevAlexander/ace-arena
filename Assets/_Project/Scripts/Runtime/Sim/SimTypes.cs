using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Ввод за кадр в координатах мира (плоскость XZ как Vector2). Вид переводит экранные направления в мировые.</summary>
    public struct SimInput
    {
        public Vector2 Move;
        public bool UseMouse;
        public Vector2 Cursor;
        public bool HasCursor;
        public int Pressed;  // биты: нажата в этом кадре
        public int Held;     // биты: держится
        public int Released; // биты: отпущена в этом кадре

        public bool IsPressed(int slot) => (Pressed & (1 << slot)) != 0;
        public bool IsHeld(int slot) => (Held & (1 << slot)) != 0;
        public bool IsReleased(int slot) => (Released & (1 << slot)) != 0;
    }

    public enum EventKind
    {
        EnemyHit, EnemyDied, EnemySpawned, EnemyAlert, PlayerHit, PlayerBlocked,
        Explosion, Teleport, CardCast, TelegraphStart, Attack, WaveStart, ShieldBreak, HitStop, Shake,
        RoomClear, TrapFire, BoltHit, PropBroken, DoorsOpen, RoomEnter, ArenaCleared,
        Zap, RewardReady, // режим форм: молния от Pos до To; выбор карточки
    }

    /// <summary>Стоп-кадр Hades (HitSimSlowParameters): замедление всей игры после удара.</summary>
    public enum HitStop { None, Quick, Long, Instant, HurtHeavy, HurtLight, HurtSmall, LastKill, WastrelHit1, WastrelHit2, CasketBolt }

    public struct SimEvent
    {
        public EventKind Kind;
        public Vector2 Pos;
        public Vector2 To;
        public float Value;
        public float Radius;
        public bool Crit;
        public int Color; // для взрывов: 0 розовый, 1 жёлтый, 2 синий, 3 огонь, 4 яд, 5 фиолетовый
        public Enemy Enemy;
        public HitStop Stop;
        /// <summary>Удар добил врага или был критическим — пауза Hades длиннее на 0.04 с.</summary>
        public bool Kill;
    }

    public enum DotKind { Bleed, Burn, Poison, Hex, Lava, Noxious, Light }

    public sealed class Dot
    {
        public DotKind Kind;
        public float Tick;
        public float Freq;
        public int Left;
        public float T;
    }

    public static class DotTable
    {
        // Время и шаг тиков (Frequency в данных DandyAce)
        public static void Get(DotKind k, out float time, out float freq)
        {
            switch (k)
            {
                case DotKind.Bleed: time = 4f; freq = 0.8f; break;
                case DotKind.Burn: time = 4f; freq = 0.4f; break;
                case DotKind.Poison: time = 4f; freq = 0.5f; break;
                case DotKind.Hex: time = 10f; freq = 0.3f; break;
                case DotKind.Lava: time = 4f; freq = 0.2f; break;
                case DotKind.Noxious: time = 2f; freq = 0.2f; break;
                default: time = 2f; freq = 0.2f; break;
            }
        }
    }

    public sealed class Slot
    {
        public CardId? Main;
        public CardId? Up;
        public int Charges;
        public float CdTimer;
        public bool OnCd;
        public float UpgCd;
        public float SpawnCd;
        public Effect Bat;

        public CardDef Card => Main.HasValue ? CardCatalog.Get(Main.Value) : null;
        public UpgradeEffect Upgrade => Upgrades.For(Up, Main);
        public bool Ready => Main.HasValue && Charges > 0;

        public void Reset()
        {
            Charges = Main.HasValue ? CardCatalog.Get(Main.Value).Charges : 0;
            CdTimer = 0f; OnCd = false; UpgCd = 0f; SpawnCd = 0f; Bat = null;
        }
    }

    /// <summary>Фазы каста по-Hades. Aim — удержание жёлтой карты с прицелом; Casting — замах (ChargeTime); Hold — накопление
    /// (Crescendo); Recovery — жёсткое восстановление (Cancelable = false); Soft — мягкое, его прерывают синие карты.</summary>
    public enum CastPhase { Aim, Casting, Hold, Recovery, Soft }

    public enum CastStyle { Quick, Long, Instant }

    public sealed class Cast
    {
        public int Slot;
        public CastPhase Phase;
        public float T;
        public float Hold;
        public Vector2 Dir;
        public Vector2 Target;
        public bool Fired;
    }

    public sealed class Player
    {
        public Vector2 Pos;
        /// <summary>Скорость от ввода (разгон Acceleration) и внешняя — отбрасывание, выпады (тормозит Grip).</summary>
        public Vector2 Vel, ExtVel;
        public float GripMul = 1f, GripT;
        /// <summary>ZagreusOnHitStun: оглушение после удара врага — нельзя двигаться и поворачиваться.</summary>
        public float Stun;
        /// <summary>Прицел жёлтых карт (точка на полу) и сглаженная цель магнетизма.</summary>
        public Vector2 Reticle;
        public readonly List<Vector2> RecentDamage = new(); // (время, урон) за последние 0.6 с
        public Vector2 Face = new(0f, 1f);
        public Vector2 Aim = new(0f, 1f);
        public float Hp;
        public float HitInvuln;
        public float BuffInvuln;
        public float DashInvuln; // рывок посоха: INVULNERABLE на время заряда и 16 кадров после
        public float Haste;
        public float Slow; // замедление от снаряда Casket (HeroOnHitStun: скорость × 0.7)

        public float Shoes;
        public float Shield;
        public float Hidden;
        public Vector2 HiddenTo;
        public bool Moving;
        public Cast Cast;
        public int Buffer = -1;
        public Enemy Lock;
        // рывок: из A в B по OutQuad
        public bool Dashing;
        public Vector2 DashFrom, DashTo;
        public float DashT, DashDur;
        public Vector2 KnockVel;
        public float KnockT;
        public float ShoeTrailT;
        public Effect ShoeTrail;
        public readonly StaffState Staff = new();

        public bool Invulnerable => HitInvuln > 0f || BuffInvuln > 0f || DashInvuln > 0f || Hidden > 0f;
        public bool Aiming => Cast != null && Cast.Phase == CastPhase.Aim;
        /// <summary>HeroOnHitStun ветки Hades II — то же оглушение героя, что и <see cref="Stun"/>.</summary>
        public float HitStun { get => Stun; set => Stun = value; }
    }

    /// <summary>Ghost и Stomper — DandyAce и Пуфф; Thug и Witch — Hades; Wastrel и Casket — Hades II.</summary>
    public enum EnemyKind { Ghost, Stomper, Thug, Witch, Wastrel, Casket }

    /// <summary>Cover, Peek — HideAndPeekAI ведьмы Hades; Hide, Approach — то же у Casket (Hades II); PreFire — пауза Thug между замахом и выпадом.</summary>
    public enum AiState { Spawn, Wander, Alert, Chase, Windup, Strike, Recover, Lunge, PreFire, Cover, Peek, Hide, Approach }

    /// <summary>Ellipse — эллипс (круг) с центром Center; Lane — полоса; Trapezoid — от Center вперёд, ширина растёт
    /// от Base до Side; Fan — сектор радиуса Forward с полууглом Angle (градусы).</summary>
    public enum TeleShape { Ellipse, Lane, Trapezoid, Fan }

    /// <summary>Обозначение будущей атаки на земле. Заполняется от 0 до 1 к моменту удара; удар бьёт ровно эту фигуру.</summary>
    public sealed class Telegraph
    {
        public TeleShape Shape;
        public Vector2 Center;
        public Vector2 Dir;
        public float Forward; // эллипс: полуось вдоль Dir; полоса: длина
        public float Side;    // эллипс: полуось поперёк; полоса: полуширина; трапеция: полуширина на конце
        public float Base;    // трапеция: полуширина у начала
        public float Angle;   // сектор: полуугол, градусы
        public float Total;
        public float T;

        public float Progress => Total <= 0f ? 1f : Mathf.Clamp01(1f - T / Total);

        public bool Contains(Vector2 p, float pad)
        {
            var d = p - Center;
            float f = Vector2.Dot(d, Dir), s = Vector2.Dot(d, new Vector2(-Dir.y, Dir.x));
            if (Shape == TeleShape.Ellipse)
            {
                float a = Forward + pad, b = Side + pad;
                return f * f / (a * a) + s * s / (b * b) <= 1f;
            }
            if (Shape == TeleShape.Trapezoid)
            {
                float half = Mathf.Lerp(Base, Side, Mathf.Clamp01(f / Mathf.Max(Forward, 0.001f)));
                return f >= -pad && f <= Forward + pad && Mathf.Abs(s) <= half + pad;
            }
            if (Shape == TeleShape.Fan)
            {
                float r = d.magnitude;
                if (r > Forward + pad) return false;
                if (r <= pad) return true;
                // запас по краю сектора — на ширину тела
                return Vector2.Angle(Dir, d) <= Angle + Mathf.Rad2Deg * Mathf.Asin(Mathf.Clamp01(pad / r));
            }
            return f >= -pad && f <= Forward + pad && Mathf.Abs(s) <= Side + pad;
        }
    }

    public sealed class Enemy
    {
        public int Id;
        public EnemyKind Kind;
        public Vector2 Pos, Vel, Face = new(0f, -1f), Dir;
        public float R;
        public float Hp, MaxHp;
        public AiState State;
        public float T, Cd, Lost, Alert;
        public int Strike;
        public Vector2 Home;       // точка появления — вокруг неё бродит, пока не заметил (AIWanderDistance)
        public bool Aggro;
        public float React = -1f;  // задержка реакции перед агрессией (AggroReactionTime), −1 — ещё не заметил
        public bool Attacker;      // Casket: сейчас атакует (MaxAttackers)
        public bool Retry;         // замах сорван оглушением — после него повторить с места
        public bool SpawnAggro;    // появился волной — сразу в бой
        public float ChaseT;       // сколько идёт к цели (тайм-аут MoveWithinRange)
        public float Rooted;       // в круге каста: не может двигаться (ImpactSlow × 0.05 + ImpactGrip)
        public bool Dead;
        public float DeathT;
        public float SpawnInvuln;
        public float Flash;
        public Telegraph Tele;
        public bool HitDone;
        // статусы
        public float Stun, Fear, Pull, PullAce, KnockT;
        public Vector2 PullTo, KnockVel;
        public readonly List<Dot> Dots = new();
        public float MarkBleed, MarkPoison;
        public float Moved;
        // Hades: агрессия, телеграф без зоны (белая вспышка и дрожь), отбрасывание с Grip
        public Vector2 CoverPos;
        public bool PreSpawned;
        public float AggroT;
        /// <summary>Длительность и остаток белой вспышки замаха (PreAttackEnd: последние min(0.5, замах) с).</summary>
        public float WarnTotal, WarnT;
        public float Shake;
        public Vector2 ExtVel;
        public float Grip;
        /// <summary>OnHitStun Hades: удар карты на миг сбивает врага (замах начинается заново). Это не контроль —
        /// бонусы улучшений «против оглушённых» на него не реагируют.</summary>
        public float HitStun;
        public bool HadesKind => Kind is EnemyKind.Thug or EnemyKind.Witch;
        // статусы свойств (режим форм)
        public float Burn, BurnT, BurnTick;   // Scorch: запас урона, выгорает за BurnT
        public int Frost;                     // стаки инея, на FrostToFreeze — заморозка
        public float FrostT, Frozen;
        public float RotT; public int RotLvl; // гниль: уязвимость, при смерти заражает соседей
        public float DoomT, DoomAcc, DoomFrac; // приговор: копит урон и через DoomT отдаёт его долю
        public Effect DoomBy;                 // метка духа — сработает, когда дух исчезнет
        public float LinkT;                   // Hitch: часть урона уходит другим связанным
        public float ZoneSlowT;
        public float PullSpeed;               // скорость притяжения (0 — 2.4, как Black Hole)
        public float KnockDmg;                // отброс свойства: урон при ударе о стену или врага

        public bool Alive => !Dead && State != AiState.Spawn;
        public bool UnderCc => Stun > 0f || Fear > 0f || Pull > 0f || PullAce > 0f;
    }

    public enum EffectKind
    {
        Dagger, FiveCard, WildCard, Missile, Rose, Bat, BlackHole, Cone, DashHit, FireTrail, Cloud, Tether, AegisShield,
        EnemyBolt, // самонаводящийся снаряд Casket
        StaffWedge, StaffBall, CastCircle, // посох: хитбокс удара, снаряд спецатаки, круг каста
        // режим форм (ArenaSim.Forms)
        FormShot, FormBolt, FormZone, FormSpirit, FormBarrier, FormWall, FormTrail, FormShade, FormEchoShot, FormDashReturn, FormVortex,
    }

    /// <summary>Чем нанесён удар: главная карта, её улучшение и собственный эффект.</summary>
    public sealed class HitSource
    {
        public Slot Slot;
        public CardDef Card;
        public UpgradeEffect Up;
        public float Base;
        public float Div = 1f;
        public DotKind OwnDot;
        public float OwnDotDamage;
        public bool OwnCcApplies = true;
        public float PushSpeed;
        public bool UpgradeApplies = true;
        public bool FromJump;
    }

    public sealed class Effect
    {
        public int Id;
        public EffectKind Kind;
        public Vector2 Pos, Dir, Start, To;
        public float Speed, Life, Age, R, T, Hold, Tick;
        public int Phase;
        public bool Done;
        public HitSource Src;
        public Slot Slot;
        public Enemy Target;
        public Enemy Exclude;
        public readonly HashSet<Enemy> Hit = new();
        public readonly List<Vector2> Points = new();
        public readonly List<float> PointAge = new();
        public float Damage;
        public DotKind Dot;
        public float Stun, Back, Length; // посох: оглушение при попадании, форма трапеции
        public HashSet<Enemy> SharedHit;  // общий список задетых на несколько хитбоксов (ExcludeSameWaveHits)
        // режим форм
        public FormSlot FSlot;
        public SkillCard Card;
        public FormKind As;               // по правилам какой формы снаряд накладывает свойства
        public int Bounces, Charges;
        public float Mul = 1f, Grow = 1f;
        public bool Echoed;
        public Effect Parent;
        public readonly List<Enemy> Marks = new();
    }

    /// <summary>Препятствие комнаты: многоугольник и описанная окружность (быстрая проверка).</summary>
    public sealed class Obstacle
    {
        public Vector2 Pos;
        public float R;
        public Vector2[] Poly;
        public float Height = 1f;
        public string Type;
        /// <summary>Круглое препятствие (колонна) — без многоугольника.</summary>
        public bool Round => Poly == null || Poly.Length < 3;
        /// <summary>StopsProjectiles: false — вода, обрыв, низкий бордюр (снаряды пролетают, ходить нельзя).</summary>
        public bool Shots = true;
    }

    /// <summary>Снаряд врага (сгусток ведьмы, дротик ловушки).</summary>
    public sealed class Bolt
    {
        public Vector2 Pos, Dir;
        public float Speed, Range, Travelled, R, Damage, Stun;
        public bool Done;
        public bool FromTrap;
        public HitStop Stop;
        /// <summary>Урон по врагам (ловушки бьют и врагов), отбрасывание героя.</summary>
        public float EnemyDamage, Knock;
        /// <summary>Пила: крупная, не исчезает при попадании.</summary>
        public bool Pierce;
        public readonly HashSet<object> Hit = new();
    }

    public enum TrapKind { Spike, DartPlate, DartEmitter, AxeTrigger, Axe, BlastCube, Gas, Poison, Phalanx, Saw, Archer, Inert }

    public enum TrapPhase { Idle, Pre, Fire, Post, Cooldown, Gone }

    /// <summary>Ловушка комнаты Hades. Срабатывает от шага на площадку (шипы, дротики, топоры, фаланга), от удара (взрывной куб)
    /// или сама по таймеру (газ, яд, пила, лучник). Pre — замах с предупреждением, Fire — удар, Post и Cooldown — отдых.</summary>
    public sealed class Trap
    {
        public TrapKind Kind;
        public string Type;
        public Vector2 Pos, Dir;
        public Vector2[] Poly;
        public Core.RoomDef.TrapParam P;
        public Trap Link;
        public readonly List<Trap> Chain = new();
        public TrapPhase Phase;
        public float T, Total;
        /// <summary>Облако газа или яда: сколько ещё висит.</summary>
        public float Cloud, CloudTick;
        public bool Regenerating;
        /// <summary>0..1 — доля замаха (для вспышки и роста предупреждения).</summary>
        public float Warn => Phase == TrapPhase.Pre && Total > 0f ? Mathf.Clamp01(1f - T / Total) : 0f;
    }

    /// <summary>Урна или горшок комнаты: стоит преградой, разбивается любым ударом карты (Breakable в картах Hades).</summary>
    public sealed class Breakable
    {
        public Vector2 Pos;
        public float R;
        public bool Broken;
        public Obstacle Body;
    }

    /// <summary>Опасная зона пола (лава, вода): урон раз в интервал, пока стоишь внутри.</summary>
    public sealed class HazardZone
    {
        public string Type;
        public Vector2[] Poly;
        public float Damage, Tick;
    }
}
