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
        Explosion, Teleport, CardCast, TelegraphStart, Attack, WaveStart, ShieldBreak, PlayerBurn,
    }

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

    /// <summary>Charge — кнопка зажата, идёт прицеливание (удар по отпусканию); Casting — замах; Recovery — восстановление.</summary>
    public enum CastPhase { Charge, Casting, Recovery }

    public sealed class Cast
    {
        public int Slot;
        public CastPhase Phase;
        public float T;
        public float Hold;
        public Vector2 Dir;
        public bool Fired;
    }

    public sealed class Player
    {
        public Vector2 Pos;
        public Vector2 Face = new(0f, 1f);
        public Vector2 Aim = new(0f, 1f);
        public float Hp;
        public float HitInvuln;
        public float BuffInvuln;
        public float Haste;
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

        public bool Invulnerable => HitInvuln > 0f || BuffInvuln > 0f || Hidden > 0f;
    }

    public enum EnemyKind { Ghost, Stomper }

    public enum AiState { Spawn, Wander, Alert, Chase, Windup, Strike, Recover, Lunge, Retreat, Wait }

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
        public bool Attacker;      // Lubber: занял место атакующего (SurroundAI MaxAttackers)
        public float Refresh;
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

        public bool Alive => !Dead && State != AiState.Spawn;
        public bool UnderCc => Stun > 0f || Fear > 0f || Pull > 0f || PullAce > 0f;
    }

    public enum EffectKind
    {
        Dagger, FiveCard, WildCard, Missile, Rose, Bat, BlackHole, Cone, DashHit, FireTrail, Cloud, Tether, AegisShield,
        Barrel, EnemyFire, // бочка Lubber и огонь после неё
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
    }

    public sealed class Obstacle
    {
        public Vector2 Pos;
        public float R;
    }
}
