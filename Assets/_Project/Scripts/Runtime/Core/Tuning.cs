using System;

namespace Arena.Core
{
    /// <summary>Перевод единиц Hades в юниты арены. Масштаб — по скорости героя: Загрей бежит 540 ед/с, Ace — 4.1 юнит/с.
    /// Время не масштабируется. Ось Y карт Hades сжата вдвое (изометрия 2:1), на полу она растягивается обратно.</summary>
    public static class Hades
    {
        public const float HeroSpeed = 540f;
        public const float K = 4.1f / HeroSpeed;

        /// <summary>Расстояние или скорость Hades → юниты арены.</summary>
        public static float U(float hades) => hades * K;

        /// <summary>Здоровье врагов Hades → наше: урон карт Ace примерно в 5 раз выше урона меча Загрея (100 против 20).</summary>
        public const float EnemyHpScale = 5f;

        /// <summary>Урон врагов Hades → наш: у Ace 500 HP, у Загрея 50.</summary>
        public const float EnemyDamageScale = 10f;

        /// <summary>Hades считает длительности в кадрах при 60 к/с.</summary>
        public static float Frames(int n) => n / 60f;
    }

    /// <summary>Ace: 500 HP из DandyAce. Движение, отбрасывание и неуязвимость — Загрей из Hades (PlayerUnits.sjson, HeroData.lua).</summary>
    [Serializable]
    public sealed class PlayerTuning
    {
        public float acceleration = Hades.U(2000f); // Acceleration: до полной скорости за 0.27 с
        public float dashAtCursor; // 1 — синие карты летят к курсору (опция Hades «Dash at Cursor»), по умолчанию выключено
        public float fullControlSpeed = Hades.U(400f); // FullControlVelocityThreshold: выше — ввод не управляет инерцией
        public float grip = Hades.U(1000f);            // Grip: торможение внешней скорости (отбрасывание, выпады)
        public float hp = 500f;
        // Неуязвимость Hades — только за «пачку» урона: ≥ max(25% HP, 10×10) за 0.6 с → 1.3 с (Combat.lua:1256)
        public float iFrameDuration = 1.3f;
        public float iFrameMinDamage = 10f * Hades.EnemyDamageScale;
        public float iFrameThreshold = 0.25f;
        public float iFrameWindow = 0.6f;
        public float radius = Hades.U(48f);  // восьмиугольник ±48 × ±24 → круг 48 на полу
        public float speed = 4.1f;
    }

    /// <summary>Как карта применяется: розовые — короткое нажатие (атака Hades), жёлтые — удержание с прицелом и отпускание
    /// (Cast / GunGrenadeToss), синие — мгновенно (рывок). Числа — PlayerWeapons.sjson.</summary>
    [Serializable]
    public sealed class CastTuning
    {
        // Мягкое восстановление (Cancelable = true): его прерывают синие карты, нажатия копятся в буфер
        public float quickSoftRecovery = Hades.Frames(11); // SwordWeapon: 5f жёсткое + 11f мягкое
        public float quickMoveMultiplier = 0.35f;            // SwordWeapon: ×0.35 в замахе и после удара
        public float longSoftRecovery = Hades.Frames(24);  // RangedWeapon: 6f + 24f
        public float longMinWindup = 0.1f;                   // GunGrenadeToss ChargeTime
        public float instantSoftRecovery = Hades.Frames(12); // RushWeaponDisableMove 12f
        // Прицел жёлтых карт (GunGrenadeToss + PlayerUnitData)
        public float reticleStart = Hades.U(420f);           // ManualAimingInitialOffset
        public float reticleRange = Hades.U(1200f);          // AutoLockRange — предел прицела
        public float reticleSpeed = Hades.U(2500f);          // ReticleMoveSpeed
        public float reticleMagnetDistance = Hades.U(100f);  // ReticleMagnetismDistance
        public float reticleMagnetSpeed = Hades.U(1000f);    // ReticleMagnetismSpeed
        // Автоприцел по умолчанию (Weapons.sjson): дальность 650, ±100°
        public float lockRange = Hades.U(650f);
        public float lockArc = 100f;
        public int lockCandidates = 4; // GetNearestEnemyArgs: 4 ближайших в радиусе
        // OnHitStun удара по врагу (PlayerProjectiles.sjson): меч 0.5 с, Cast 0.7 с — замах врага срывается
        public float hitStunQuick = 0.5f;
        public float hitStunLong = 0.7f;
        public float hitStunInstant = 0.5f;
    }

    /// <summary>Враг 1 — Enraged Ghost из DandyAce: двойной удар по области перед собой.</summary>
    [Serializable]
    public sealed class GhostTuning
    {
        public float attackRange = 3f;          // RGR_DC_DoubleSlash_InRange
        public float chaseSpeed = 3f;            // RGR_ACT_Chase
        public float cooldown = 2.5f;
        public float damage = Tier1.EnemyDamage[Tier1.WeakPlus]; // 50
        public float fearSpeed = 2.5f;
        public float hp = Tier1.EnemyHp[Tier1.StrongMinus];     // 975
        public float lunge = 0.8f;               // оценка: рывок Rager_MoveForce 850 перед ударом
        public float noticeRange = 10f;          // RGR_DC_PatrolSearchTarget
        public float radius = 0.35f;
        public float recovery1 = 0.1f;
        public float recovery2 = 1f;
        public float zoneDistance = 1.1f;        // центр хитбокса перед призраком
        public float zoneForward = 0.9f;         // эллипс 0.9 × 1.1
        public float zoneSide = 1.1f;
        // В DandyAce замах 0.4 и 0.3 с. Увеличено, чтобы из зоны успевал выйти игрок, отреагировавший сразу (тест GhostTests):
        // с разгоном Загрея (0.27 с до полной скорости) из центра зоны выходишь за ~0.5 с, плюс 0.2 с реакции.
        public float windup1 = 0.75f;
        public float windup2 = 0.45f;
    }

    /// <summary>Враг 2 — поведение топотуна из Пуффа (бродит, «!», погоня, короткий выпад), числа — Bunny Butler из DandyAce.
    /// Скорости и расстояния Пуффа переведены множителем 4.1 / 3.5 (скорость Ace к скорости Пуффа).</summary>
    [Serializable]
    public sealed class StomperTuning
    {
        public float alertTime = 0.45f;
        public float chaseSpeed = 2.46f;     // 2.1 тайл/с
        public float damage = Tier1.EnemyDamage[Tier1.MidMinus]; // 59
        public float fearSpeed = 2.5f;
        public float hp = Tier1.EnemyHp[Tier1.Mid];              // 725
        public float loseDistance = 11.7f;   // 10 тайлов
        public float loseTime = 2.5f;
        public float lungeCooldown = 1.3f;
        public float lungeDistance = 1.76f;  // 1.5 тайла
        public float lungeSpeed = 5.86f;     // 5 тайл/с
        public float lungeTime = 0.26f;
        public float noticeRange = 8f;
        public float radius = 0.33f;
        public float telegraph = 0.6f;       // замах Bunny Butler перед рывком (с превью атаки)
        public float wanderSpeed = 1.11f;    // 0.95 тайл/с
    }

    /// <summary>Wretched Thug (HeavyMelee) из Hades: подходит, замах 0.4 + 0.13 с, выпад с ударом трапецией перед собой, долгий отдых.
    /// EnemyData.lua:266/332, Enemies.sjson:467, EnemyWeapons.sjson:250, EnemyProjectiles.sjson:410.</summary>
    [Serializable]
    public sealed class ThugTuning
    {
        public float aggroRange = Hades.U(600f);     // AIAggroRange
        public float attackDistance = Hades.U(200f); // AIAttackDistance
        public float damage = 10f * Hades.EnemyDamageScale;
        public float fire = 0.9f;                    // FireDuration
        public float gripDuringLunge = Hades.U(4800f); // Grip 8000 × MeleeAttackGrip 0.6
        public float hitActive = 0.18f;              // Fuse хитбокса
        public float hitBackHalf = Hades.U(40f);     // трапеция: у тела полуширина 40, на конце 80, длина 310
        public float hitFrontHalf = Hades.U(80f);
        public float hitBack = Hades.U(-10f);
        public float hitFront = Hades.U(310f);
        public float hp = 160f * Hades.EnemyHpScale;
        public float knockback = Hades.U(600f);      // ImpactVelocity по герою
        public float knockbackGripTime = 0.3f;       // MeleeAttackVictimGrip: сцепление героя ×0.05 на 0.3 с, затем стоп
        public float lungeSpeed = Hades.U(1000f);    // SelfVelocity
        public float preAttack = 0.4f;               // PreAttackDuration (дрожь и белая вспышка)
        public float preFire = 0.13f;                // PreFireDuration
        public float postAttack = 1.5f;              // PostAttackDuration
        public float radius = Hades.U(76f);
        public float speed = Hades.U(250f);
        public float stunHero = 0.2f;                // ZagreusOnHitStun
        public float wanderDistance = Hades.U(100f);
    }

    /// <summary>Wretched Witch (LightRanged) из Hades: прячется за укрытием 2.3 с, выходит на линию огня, замах 0.5 с, стреляет сгустком.
    /// EnemyData.lua:1548/1646, EnemyAI.lua:1846 (HideAndPeekAI), EnemyProjectiles.sjson:2672.</summary>
    [Serializable]
    public sealed class WitchTuning
    {
        public float aggroRange = Hades.U(750f);
        public float attackDistance = Hades.U(1200f);
        public float barrel = Hades.U(55f);
        public float boltRadius = Hades.U(24f);
        public float boltRange = Hades.U(1200f);
        public float boltSpeed = Hades.U(570f);
        public float coverHug = Hades.U(150f);       // CoverHugDistance
        public float damage = 5f * Hades.EnemyDamageScale;
        public float hp = 80f * Hades.EnemyHpScale;
        public float losBuffer = Hades.U(80f);      // AILineOfSightBuffer
        public float moveTimeoutMax = 4f;           // AIMoveWithinRangeTimeout 2–4 с
        public float moveTimeoutMin = 2f;
        public float postAttack = 0.5f;
        public float preAttack = 0.5f;
        public float radius = Hades.U(64f);
        public float speed = Hades.U(380f);
        public float stunHero = 0.2f;
        public float takeCover = 2.3f;              // TakeCoverDuration
    }

    [Serializable]
    public sealed class ArenaTuning
    {
        public float spawnInvulnerability = 0.35f;
        public float spawnTime = 2f;         // руна призыва EnemySummonRune 2.0 с, враг проявляется 1.9 с
        public float waveDelay = 1f;         // следующая волна через 1 с после гибели предыдущей
        public float spawnIntervalMin = 0.175f;
        public float spawnIntervalMax = 0.225f;
        public float preSpawnDistance = Hades.U(750f); // предрасставленные враги — не ближе 750 к точке входа
    }
}
