using System;

namespace Arena.Core
{
    /// <summary>Ace (DandyAce, PlayerMovement/PlayerHealth): 500 HP, 4.1 юнит/с, неуязвимость после удара 0.2 с.
    /// Движение — по-Hades: скорость задаётся сразу, без разгона, наклон стика на неё не влияет.</summary>
    [Serializable]
    public sealed class PlayerTuning
    {
        public float dashAtCursor; // 1 — синие карты летят к курсору (опция Hades «Dash at Cursor»), по умолчанию выключено
        public float hitInvulnerability = 0.2f;
        public float minCharge = 0.1f;   // Hades II WeaponStaffBall: ChargeTime / MinChargeToFire 0.1 с, MinWeaponChargeTime 0.1
        public float hp = 500f;
        public float radius = 0.3f;
        public float speed = 4.1f;
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
        public float damage = 50f;             // за удар; в Hades 9 и 7 — оставлен прежний урон
        public float radius = 0.35f;
        public float fearSpeed = 2.5f;
        public float aggroRange = 4.55f;       // AIAggroRange 600
        public float reactionMin = 0.05f;      // AggroReactionTimeMin
        public float reactionMax = 0.2f;       // AggroReactionTimeMax
        public float wanderDistance = 1.52f;   // AIWanderDistance 200
        public float wanderInterval = 1.5f;    // AIWanderFrequency
        public float speed = 1.21f;            // Speed 160
        public float attackDistance = 3.42f;   // AttackDistance 500 − AttackDistanceBuffer 50
        // удар 1 — BrawlerMelee: замах, выпад, трапеция перед собой
        public float windup1 = 1.15f;          // PreAttackDuration
        public float shake1 = 0.4f;            // PreAttackEndDuration — дрожь в конце замаха
        public float fire1 = 0.4f;             // FireDuration
        public float lungeSpeed = 13.67f;      // FireSelfVelocity 1800
        public float lungeDecay = 0.13f;       // оценка: GRIP 0.3 с и HaltOnEnd гасят выпад, ≈ 1.8 юнита
        public float hit1Reach = 1.75f;        // Points (−10, ±40) … (230, ±100)
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
        public float wanderInterval = 1.5f;
        public float speed = 2.13f;            // Speed 280
        public int maxAttackers = 6;           // MaxAttackers
        // укрытие
        public float coverMin = 1.5f;          // TakeCoverDurationMin
        public float coverMax = 2.2f;          // TakeCoverDurationMax
        public float coverHug = 1.9f;          // CoverHugDistance 250 — встаёт за укрытием с дальней от Смерти стороны
        // выстрел — MageRanged
        public float attackDistance = 7.97f;   // AttackDistance 1050 (ScaleY 0.48 — круг на полу)
        public float approachTimeoutMin = 7f;  // MoveWithinRangeTimeoutMin
        public float approachTimeoutMax = 10f; // MoveWithinRangeTimeoutMax
        public float windup = 1f;              // PreAttackDuration, TrackTargetDuringCharge
        public int shots = 3;                  // FireTicks
        public float shotInterval = 0.17f;     // FireInterval 0.085 × 2
        public float kickSpeed = 3.04f;        // FireTickSelfVelocity 400, назад (AngleOffset 180)
        public float kickFalloff = 0.95f;      // FireTickSelfVelocityConsecutiveMultiplier
        public float kickDecay = 0.1f;         // оценка: как быстро гаснет отскок
        public float recoveryMin = 0.7f;       // PostAttackDurationMin
        public float recoveryMax = 0.9f;       // PostAttackDurationMax
        public float boltSpeed = 4.25f;        // Speed 560
        public float boltRange = 12.15f;       // Range 1600
        public float boltLife = 5f;            // Fuse
        public float boltRadius = 0.2f;        // Points: треугольник ±24
        public float boltTurn = 90f;           // оценка: HOMING без MaxAdjustRate, градусов в секунду
        public float slow = 0.7f;              // HeroOnHitStun: скорость × 0.7
        public float slowTime = 0.2f;          // Duration
    }

    [Serializable]
    public sealed class ArenaTuning
    {
        public float halfDepth = 7f;
        public float halfWidth = 10f;
        public float spawnInvulnerability = 0.35f;
        public float spawnTime = 1f;
        public float waveDelay = 2f;
    }
}
