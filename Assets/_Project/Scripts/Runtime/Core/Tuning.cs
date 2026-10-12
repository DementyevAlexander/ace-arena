using System;

namespace Arena.Core
{
    /// <summary>Смерть — герой с параметрами Мелинои из Hades II (Units/PlayerUnits.sjson, Scripts/HeroData.lua).
    /// Движение по-Hades: скорость задаётся сразу, без разгона, наклон стика на неё не влияет.</summary>
    [Serializable]
    public sealed class PlayerTuning
    {
        public float dashAtCursor; // 1 — синие карты летят к курсору (опция Hades «Dash at Cursor»), по умолчанию выключено
        public float hp = 30f * HadesScale.Damage;  // MaxHealth 30 → ≈ 167 в нашем масштабе урона врагов
        public float radius = 48f * HadesScale.Unit; // восьмиугольник ±48 × ±24 → ≈ 0.36
        public float speed = 4.1f;                   // Speed 540
        public float minCharge = 0.1f;   // зажатие карт: как ChargeTime спецатаки посоха
        /// <summary>Неуязвимость после урона (InvulnerableFrameDuration 1.3 с) включается, только когда урон за последние
        /// 0.6 с набрал max(25% HP, 15 урона Hades) — HeroData.lua, CombatLogic.lua. На смертельном ударе не включается.</summary>
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

    [Serializable]
    /// <summary>Комната и волны — как бой в Эребе Hades II (EncounterData.lua: FIntroFight, GeneratedF).</summary>
    public sealed class ArenaTuning
    {
        public float halfDepth = 7f;
        public float halfWidth = 10f;
        public float spawnInvulnerability = 0f; // после появления неуязвимости нет
        public float spawnTime = 1.8f;          // ActivateDuration: появляется 1.8 с, по нему не попасть
        public float waveDelay = 0.5f;          // следующая волна через 0.5 с, когда осталось ≤ WaveRequiredRemainingCount
        public int waveRemaining = 1;           // WaveRequiredRemainingCount
        public float spawnIntervalMin = 0.2f;   // SpawnIntervalMin/Max
        public float spawnIntervalMax = 0.4f;
        public float activeCap = 4f;            // ActiveEnemyCap; Casket весит 0.8 (ActiveCapWeight)
        public float casketWeight = 0.8f;
        public int budget = 55;                 // бюджет сложности сгенерированной волны Эреба
        public int wastrelCost = 18;            // GeneratorData.DifficultyRating
        public int casketCost = 12;
        public float wakeUpDelay = 0.3f;        // первая волна стоит в комнате заранее: WakeUpDelay и реакция 0.15–0.45 с
        public float wakeReactionMin = 0.15f;
        public float wakeReactionMax = 0.45f;
    }
}
