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

        /// <summary>Здоровье врагов Hades → наше: один масштаб для Hades и Hades II (<see cref="HadesScale.Hp"/>).</summary>
        public const float EnemyHpScale = HadesScale.Hp;

        /// <summary>Урон врагов Hades → наш: один масштаб для Hades и Hades II (<see cref="HadesScale.Damage"/>).</summary>
        public const float EnemyDamageScale = HadesScale.Damage;

        /// <summary>Hades считает длительности в кадрах при 60 к/с.</summary>
        public static float Frames(int n) => n / 60f;
    }

    /// <summary>Перевод чисел Hades II в масштаб Ace Arena.</summary>
    public static class HadesScale
    {
        /// <summary>Пиксели Hades → юниты: Мелиноя бежит 540 пикс/с, Смерть — 4.1 юнит/с.</summary>
        public const float Unit = 4.1f / 540f;
        /// <summary>HP врагов → под наш урон: одно нажатие основной атаки у нас — Twin Daggers 2 × 100 = 200,
        /// у Мелинои — удар посоха, в среднем по комбо (20 + 25 + 60) / 3 = 35 (ProjectileStaffSwing1–3).
        /// Враг умирает за то же число нажатий, что и в Hades.</summary>
        public const float Hp = 200f / 35f;
        /// <summary>Урон врагов по Смерти: Wastrel бьёт на 50 (как прежний враг), в Hades BrawlerMelee — 9.</summary>
        public const float Damage = 50f / 9f;
    }

    /// <summary>Враг 1 — Wastrel (Brawler) из Hades II: начальный ближний боец Эреба (первый бой — FIntroFight).
    /// Медленно подходит, бьёт комбо из двух ударов (BrawlerMelee с выпадом → BrawlerMeleeClose дугой) и сразу
    /// повторяет, если Смерть рядом. Источники: EnemyData_Brawler.lua, EnemyData.lua (BaseVulnerableEnemy),
    /// Units/Enemies.sjson, Projectiles/Enemy_BiomeF_Projectiles.sjson. Расстояния — × HadesScale.Unit.</summary>
    [Serializable]
    public sealed class WastrelTuning
    {
        public float hp = 255f * HadesScale.Hp; // MaxHealth 255 → ≈ 1457
        public float damage = 9f * HadesScale.Damage;  // BrawlerMelee 9 → 50
        public float damage2 = 7f * HadesScale.Damage; // BrawlerMeleeClose 7 → ≈ 39
        public float radius = 0.35f;
        public float fearSpeed = 2.5f;
        public float aggroRange = 4.55f;       // AIAggroRange 600
        public float reactionMin = 0.05f;      // AggroReactionTimeMin
        public float reactionMax = 0.2f;       // AggroReactionTimeMax
        public float wanderDistance = 1.52f;   // AIWanderDistance 200
        public float wanderInterval = 2f;      // AIWanderFrequency 1.5 + 0.5 с между точками блуждания
        public float speed = 1.21f;            // Speed 160
        public float attackDistance = 3.42f;   // AttackDistance 500 − AttackDistanceBuffer 50
        public float losBuffer = 85f * 0.5f * HadesScale.Unit; // RequireUnitLoS, LoSBuffer 85 — ширина луча видимости
        public float approachTimeout = 9f;     // MoveWithinRange: не дошёл за 9 с — бьёт как есть
        // удар 1 — BrawlerMelee: замах, выпад, трапеция перед собой
        public float windup1 = 1.15f;          // PreAttackDuration
        public float shake1 = 0.4f;            // PreAttackEndDuration — дрожь в конце замаха
        public float fire1 = 0.4f;             // FireDuration
        public float lungeSpeed = 13.67f;      // FireSelfVelocity 1800
        public float lungeDecel = 24.7f;       // AttackHighGrip (Grip 2500 × 1.3) — торможение, юнит/с²; оценка модели трения
        public float lungeTime = 0.3f;         // HaltOnEnd через 0.3 с — выпад ≈ 3 юнита
        public float hit1Back = 10f * HadesScale.Unit; // хитбокс прикреплён к Wastrel: от −10 …
        public float hit1Reach = 1.75f;        // … до 230 пикселей вперёд, Points (−10, ±40) … (230, ±100)
        public float hit1BaseHalf = 0.3f;
        public float hit1TipHalf = 0.76f;
        public float hit1Delay = 0.02f;        // StartDelay
        public float hit1Active = 0.2f;        // Fuse
        // удар 2 — BrawlerMeleeClose: на месте, взмах дугой 190°
        public float windup2 = 0.65f;          // PreAttackDuration
        public float shake2 = 0.5f;            // PreAttackEndDuration
        public float fire2 = 0.4f;             // FireDuration
        public float recovery = 1.1f;          // PostAttackDuration — после него сразу следующий удар (кулдауна нет)
        public float stepSpeed = 2.28f;        // FireSelfVelocity 300
        public float hit2Reach = 1.67f;        // Points (220, ±60)
        public float arcStart = -110f;         // ArcStart
        public float arcEnd = 80f;             // ArcEnd
        public float hit2Delay = 0.055f;       // StartDelay
        public float hit2Active = 0.19f;       // взмах 190° со скоростью 1000°/с
        public float hit2WindupTurn = 1200f * 0.02f; // RotationSpeed × PreAttackRotationDampening — доворот в замахе, °/с
        public float hit2FireTurn = 1200f * 0.1f;    // × FireRotationDampening — во время взмаха
        // что удар делает с героем: ImpactVelocity 200 под GRIP 0.05 на 0.3 с, HeroOnHitStun 0.2 с (стоит, атаковать может)
        public float heroKnockSpeed = 200f * HadesScale.Unit;
        public float heroKnockTime = 0.3f;
        public float heroStun = 0.2f;
    }

    /// <summary>Враг 2 — Casket (Mage) из Hades II: начальный дальний враг Эреба (первый бой — FIntroFight).
    /// HideAndPeekAI: прячется за ближайшим укрытием, затем выходит на дистанцию выстрела с прямой видимостью,
    /// замахивается, следя за Смертью, и пускает 3 самонаводящихся снаряда, отскакивая назад; потом снова прячется.
    /// Источники: EnemyData_Mage.lua, WeaponData_Mage.lua, EnemyAILogic.lua (HideAndPeekAI, DoAttackerAILoop),
    /// Units/Enemies.sjson, Projectiles/Enemy_BiomeF_Projectiles.sjson.</summary>
    [Serializable]
    public sealed class CasketTuning
    {
        public float hp = 80f * HadesScale.Hp;  // MaxHealth 80 → ≈ 457
        public float damage = 4f * HadesScale.Damage; // снаряд MageRanged: Damage 4 → ≈ 22
        public float radius = 0.33f;
        public float fearSpeed = 2.5f;
        public float aggroRange = 9.49f;       // AIAggroRange 1250
        public float aggroDuration = 0.7f;     // AggroDuration — пауза после того, как заметил
        public float reactionMin = 0.05f;
        public float reactionMax = 0.2f;
        public float wanderDistance = 1.52f;
        public float wanderInterval = 2f;
        public float speed = 2.13f;            // Speed 280
        public int maxAttackers = 6;           // MaxAttackers
        // укрытие
        public float coverMin = 1.5f;          // TakeCoverDurationMin
        public float coverMax = 2.2f;          // TakeCoverDurationMax
        public float coverHug = 1.9f;          // CoverHugDistance 250 от центра укрытия, с дальней от Смерти стороны
        public float peekDelay = 0.1f;         // выглянул — ещё 0.1 с до атаки
        // выстрел — MageRanged
        public float attackDistance = 7.59f;   // AttackDistance 1050 − AttackDistanceBuffer 50
        public float losBuffer = 25f * HadesScale.Unit; // RequireProjectileLoS, LoSBuffer 25
        public float approachTimeoutMin = 7f;  // MoveWithinRangeTimeoutMin
        public float approachTimeoutMax = 10f; // MoveWithinRangeTimeoutMax
        public float windup = 1f;              // PreAttackDuration, TrackTargetDuringCharge
        public float windupTurn = 1200f * 0.08f; // RotationSpeed × PreAttackRotationDampening — доворот прицела, °/с
        public int shots = 3;                  // FireTicks
        public float shotInterval = 0.17f;     // FireInterval 0.085 × 2 — пауза и после последнего выстрела
        public float fireDuration = 0.2f;      // FireDuration — затем восстановление
        public float kickSpeed = 3.04f;        // FireTickSelfVelocity 400, назад (AngleOffset 180)
        public float kickFalloff = 0.95f;      // FireTickSelfVelocityConsecutiveMultiplier
        public float kickDecay = 0.1f;         // оценка: как быстро гаснет отскок
        public float recoveryMin = 0.7f;       // PostAttackDurationMin
        public float recoveryMax = 0.9f;       // PostAttackDurationMax
        public float boltSpeed = 4.25f;        // Speed 560
        public float boltRange = 12.15f;       // Range 1600
        public float boltLife = 5f;            // Fuse
        public float boltRadius = 0.2f;        // Points: треугольник ±24
        public float boltTurn = 30f;           // оценка: у MageRanged нет MaxAdjustRate (по умолчанию в движке); у врагов Эреба
                                               // с заданным доворотом — 30…90°/с, для стартового врага взято нижнее значение
        public float slow = 0.7f;              // HeroOnHitStun: скорость × 0.7
        public float slowTime = 0.2f;          // Duration
    }

    /// <summary>Смерть — герой с параметрами Мелинои из Hades II (HP 30, Units/PlayerUnits.sjson, Scripts/HeroData.lua).
    /// Движение, отбрасывание и неуязвимость — по Hades (Загрей и Мелиноя одинаковы: Speed 540, Acceleration 2000).</summary>
    [Serializable]
    public sealed class PlayerTuning
    {
        public float acceleration = Hades.U(2000f); // Acceleration: до полной скорости за 0.27 с
        public float dashAtCursor; // 1 — синие карты летят к курсору (опция Hades «Dash at Cursor»), по умолчанию выключено
        /// <summary>Опция Hades «Помощь в прицеливании» (Aim Assist): «во время атаки автоматически наводиться на врагов в радиусе».
        /// Выключена — удар идёт ровно в курсор или по стику.</summary>
        public bool aimAssist = true;
        public float fullControlSpeed = Hades.U(400f); // FullControlVelocityThreshold: выше — ввод не управляет инерцией
        public float grip = Hades.U(1000f);            // Grip: торможение внешней скорости (отбрасывание, выпады)
        public float hp = 30f * HadesScale.Damage;  // MaxHealth 30 → ≈ 167 в нашем масштабе урона врагов
        // Неуязвимость Hades — только за «пачку» урона: ≥ max(25% HP, 15 урона Hades) за 0.6 с → 1.3 с (Combat.lua:1256, CombatLogic.lua)
        public float iFrameDuration = 1.3f;
        public float iFrameMinDamage = 15f * Hades.EnemyDamageScale;
        public float iFrameThreshold = 0.25f;
        public float iFrameWindow = 0.6f;
        public float radius = Hades.U(48f);  // восьмиугольник ±48 × ±24 → круг 48 на полу
        public float speed = 4.1f;
        public float minCharge = 0.1f;   // зажатие карт: как ChargeTime спецатаки посоха
        // те же правила неуязвимости под именами ветки Hades II (их читает посох и режим форм)
        public float hitInvulnerability = 1.3f;
        public float invulnWindow = 0.6f;
        public float invulnHpFraction = 0.25f;
        public float invulnMinDamage = 15f * HadesScale.Damage;
        /// <summary>Хитстоп, когда бьют героя (HitSimSlowParameters вражеского снаряда: ProjectileData_EnemyF.lua):
        /// 0 — удар 1 Wastrel, 1 — удар 2, 2 — снаряд Casket. Тряска HitScreenshake 3 пикселя на 0.12 / 0.12 / 0.06 с.</summary>
        public float[][] hitSlow =
        {
            new[] { 0.02f, 0.1f, 0f, 0.1f, 1f, 0.07f },
            new[] { 0.02f, 0.1f, 0f, 0.22f, 1f, 0.07f },
            new[] { 0f, 0.25f, 0f, 0.11f, 1f, 0.1f },
        };
        public float hitShake = 3f * HadesScale.Unit;
        public float[] hitShakeTime = { 0.12f, 0.12f, 0.06f };
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
        public float lungeDistance = Hades.U(104f);  // выпад: 1000 с торможением 4800 — ≈104 (для зоны атаки на земле)
        public float preAttack = 0.4f;               // PreAttackDuration (дрожь)
        public float preFire = 0.13f;                // PreFireDuration
        public float postAttack = 1.5f;              // PostAttackDuration
        public float radius = Hades.U(76f);
        public float speed = Hades.U(250f);
        public float stunHero = 0.2f;                // ZagreusOnHitStun
        public float wanderDistance = Hades.U(100f);
    }

    /// <summary>Мегера (Harpy) — первая фурия, босс Тартара в Hades. Файлы: Scripts/EnemyData.lua:9963 (Harpy, AIStages),
    /// Scripts/WeaponData.lua (HarpyLunge 4432, HarpyWhipWhirl 5170, HarpyBeam 5507, HarpyLightning 6186, HarpySpawns 7144),
    /// Game/Weapons/EnemyWeapons.sjson, Game/Projectiles/EnemyProjectiles.sjson, Scripts/EncounterData.lua:3509 (BossHarpy1).
    /// Без Пакта: без помощи сестёр (SupportAIWeaponOptions).</summary>
    [Serializable]
    public sealed class MegaeraTuning
    {
        public float hp = 4400f * Hades.EnemyHpScale;       // MaxHealth 4400 → ≈ 25 143
        public float speed = Hades.U(600f);                // Speed 600 — быстрее героя (540)
        public float radius = Hades.U(51f);                // столкновение ±51
        public float ccMul = 0.3f;                         // 1_BaseBoss: SlowEffectModifier 0.3 — контроль на неё втрое короче
        public float[] stageAt = { 0.75f, 0.5f, 0.25f };   // AIStages: AIEndHealthThreshold — удар не проходит ниже порога
        public float stageShift = 2f;                      // BossStageTransition: WaitDuration 2.0 — неуязвима, стоит
        public float chaseTimeout = 10f;                   // HarpyLunge: AIMoveWithinRangeTimeout 10 (рывок); у кнута — 4
        public float setupDelay = 1.5f;                    // AISetupDelay 1.5 — стоит на арене с начала, потом в бой

        // HarpyLunge: рывок по прямой
        public float lungeRange = Hades.U(600f);           // AIAttackDistance 600
        public float lungeWindup = 0.82f;                  // PreAttackDuration 0.82, направление — в начале замаха
        public float lungeDistance = Hades.U(900f);        // SelfVelocity 3000 × Grip 0.3 с ≈ 900 (оценка)
        public float lungeTime = 0.3f;                     // FireDuration 0.3
        public float lungeWidth = Hades.U(100f);           // хитбокс −50…150 поперёк (оценка)
        public float lungeDamage = 13f * Hades.EnemyDamageScale; // Damage 13 → 72
        public float lungeStun = 0.5f;                     // ZagreusOnHitStun 0.5
        public float lungeRecover = 1.2f;                  // PostAttackDuration 1.2
        public int lungeMaxRun = 2;                        // MaxConsecutiveUses 2

        // HarpyWhipWhirl: удар кнутом вокруг себя
        public float whirlRange = Hades.U(200f);           // AIAttackDistance 200
        public float whirlMaxDistance = Hades.U(600f);     // MaxPlayerDistance 600 — дальше не выбирает
        public float whirlTimeout = 4f;
        public float whirlWindup = 1f;                     // PreAttackDuration 1.0
        public float whirlRadius = Hades.U(550f);          // DamageRadius 550
        public float whirlDamage = 15f * Hades.EnemyDamageScale; // Damage 15 → 83
        public float whirlStun = 0.3f;
        public float whirlRecover = 0.3f;
        public int whirlGap = 2;                           // MinAttacksBetweenUse 2

        // HarpyBeam: 15 залпов веером по 5
        public float beamRange = Hades.U(1200f);           // AIAttackDistance 1200
        public float beamWindup = 0.5f;
        public int beamTicks = 15;                         // FireTicks 15
        public float beamTick = 0.25f;                     // FireCooldown 0.25
        public int beamCount = 5;                          // NumProjectiles 5
        public float beamAngle = 35f;                      // ProjectileAngleOffset 35 — между снарядами (трактовка)
        public float beamSpread = 5f;                      // Spread 5 — разброс ± (трактовка)
        public float beamSpeed = Hades.U(800f);
        public float beamDamage = 4f * Hades.EnemyDamageScale; // Damage 4 → 22
        public float beamRecover = 1.5f;
        public int beamGap = 3;

        // HarpyLightning: молнии с неба вокруг героя
        public float boltsRange = Hades.U(900f);           // AIAttackDistance по умолчанию 900
        public float boltsWindup = 0.5f;
        public int boltsTicks = 5;                         // FireTicks 5
        public float boltsTick = 1.2f;                     // FireCooldown 1.2
        public float boltsRing = Hades.U(425f);            // AttackSlots: центр и 4 точки на 425
        public float boltsFuse = 0.75f;                    // Fuse 0.75 — красный круг, потом удар
        public float boltsRadius = Hades.U(300f);          // DamageRadius 300
        public float boltsDamage = 9f * Hades.EnemyDamageScale; // Damage 9 → 50
        public float boltsRecover = 0.6f;
        public int boltsGap = 3;

        // HarpySpawns: «Ко мне!» — подмога
        public int spawnCount = 6;                         // SpawnCount 6, HeavyMelee или LightRanged
        public float spawnRadius = Hades.U(950f);
        public int spawnMaxActive = 5;                     // MaxActiveSpawns 5
        public float spawnRecover = 1f;
        public int spawnGap = 10;                          // MinAttacksBetweenUse 10

        // BossHarpy1: фоновая подмога весь бой
        public float addsStart = 15f;                      // StartDelay 15
        public float addsIntervalMin = 5f, addsIntervalMax = 7f; // SpawnIntervalMin/Max
        public int addsCap = 4;                            // ActiveEnemyCapBase 4 (вместе с ней)
        public float floorHpGrowth = 0.3f;                 // этажи 2–5 — пока она же, здоровье +30% за этаж (наше)
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

    /// <summary>Комнаты и волны — как Тартар Hades (EncounterData.lua): свои наборы врагов и пределы у энкаунтера комнаты
    /// (RoomDef.Encounter).</summary>
    [Serializable]
    public sealed class ArenaTuning
    {
        public float spawnInvulnerability = 0.35f;
        public float spawnTime = 2f;         // руна призыва EnemySummonRune 2.0 с, враг проявляется 1.9 с
        public float spawnTimeHades2 = 1.8f; // Hades II: ActivateDuration 1.8 с, по врагу не попасть, после — без неуязвимости
        public float waveDelay = 1f;         // следующая волна через 1 с после гибели предыдущей
        public float spawnIntervalMin = 0.175f;
        public float spawnIntervalMax = 0.225f;
        public float preSpawnDistance = Hades.U(750f); // предрасставленные враги — не ближе 750 к точке входа
        public float wakeUpDelay = 0.3f;        // Hades II: первая волна стоит заранее, WakeUpDelay и реакция 0.15–0.45 с
        public float wakeReactionMin = 0.15f;
        public float wakeReactionMax = 0.45f;
        public float casketWeight = 0.8f;       // ActiveCapWeight Casket
        /// <summary>Режим форм: в конце комнаты на пятачке ближе к центру появляются lootCount карточек, рядом
        /// по горизонтали экрана. Берётся одна, остальные сгорают. Подобрать — подойти ближе 140 (BaseLoot Interact Distance)
        /// и нажать «взаимодействие». Бюджет случайных волн растёт с каждой взятой карточкой.</summary>
        public int lootCount = 2;
        /// <summary>Веса редкости карт на пятачке: редкая (2 вливания), эпическая (3), легендарная (4). Оценка.</summary>
        public float[] rarityWeights = { 70f, 25f, 5f };
        public float lootReach = Hades.U(140f);
        public float pedestalHalf = 0.9f;      // центры карточек — в 0.9 ю от середины пятачка (между ними 1.8 ю)
        public float pedestalClear = 1.4f;     // вокруг каждой карточки свободно 1.4 ю: кольцо 0.8 и проход Смерти
        public float pedestalClearMin = 0.85f; // если так не влезает нигде (острова Асфоделя) — отступ меньше, до этого
        public float lootAppear = 0.4f;        // карточки вырастают из пола, потом их можно брать
        /// <summary>Переброс наград пятачка (F / LB рядом с ним): столько раз за одну выдачу. Цена — золото, пока бесплатно.</summary>
        public int rerollsPerDrop = 3;
        public int rerollCost = 15;            // Милости за переброс

        /// <summary>Милость — валюта забега: малахитовые ромбики выпадают из убитых врагов; ближе graceMagnet — летят к Смерти
        /// и подбираются сами. В начале забега — 0. Числа — оценка.</summary>
        public int graceMin = 1, graceMax = 3; // с обычного врага
        public int graceElite = 15;            // с мини-босса
        public int graceBoss = 40;             // с босса
        public float graceMagnet = 3f;         // «несколько метров»: ближе — притягивается
        public float gracePickup = 0.45f;
        public float graceFlySpeed = 11f;
        public float graceScatter = 3.5f;      // выпав, разлетается с такой скоростью и тормозит
        public float graceSettle = 0.35f;      // столько лежит, прежде чем начнёт притягиваться

        /// <summary>Магазин — комната на этаже (ShopRoom): карты и тринкеты I за Милость, товар не сгорает и живёт весь этаж.</summary>
        public int shopCards = 6, shopTrinkets = 2;
        public int[] shopCardPrice = { 30, 50, 80 }; // редкая, эпическая, легендарная
        public int shopTrinketPrice = 60;

        /// <summary>Элитки (мини-боссы): по два скилла-формы с перезарядкой. Телеграф красный, на время каста стоит.</summary>
        public float[] eliteSkillCd = { 6f, 9f, 12f, 10f, 7f }; // залп, круг, дух, щит, рывок
        public float eliteSkillRange = 12f;    // применяет, если Смерть ближе
        public float eliteCastTime = 0.7f;     // телеграф
        public int eliteShotCount = 3;
        public float eliteShotSpread = 18f;    // градусов между снарядами залпа
        public float eliteZoneRadius = 2f, eliteZoneLife = 3f, eliteZoneTick = 0.5f, eliteZoneDamage = 8f, eliteZoneSlow = 0.6f;
        public float eliteSpiritLife = 8f, eliteSpiritFire = 1.5f;
        public int eliteShieldHits = 5;
        public float eliteShieldTime = 4f;
        public float eliteDashRange = 6f, eliteDashTime = 0.3f, eliteDashDamage = 30f;
        public int budgetPerReward = 8;
        /// <summary>Этаж из арен: мини-босс и босс — обычный враг из набора арены, красный, с таким множителем здоровья.</summary>
        public float eliteHpMini = 10f, eliteHpBoss = 20f;
        /// <summary>Арена зачищена: сначала надпись «Арена пройдена», через столько секунд загорается дверь и пускает дальше.</summary>
        public float doorsDelay = 2.5f;
    }
}
