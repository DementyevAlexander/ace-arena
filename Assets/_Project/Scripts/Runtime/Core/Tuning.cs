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

    /// <summary>Враг 1 — поведение Wastrel (Brawler) из Hades II: начальный ближний боец Эреба. Медленно подходит,
    /// бьёт комбо из двух ударов (BrawlerMelee с выпадом → BrawlerMeleeClose дугой) и сразу повторяет, если Смерть рядом.
    /// HP и урон — Enraged Ghost из DandyAce. Источники: EnemyData_Brawler.lua, EnemyData.lua (BaseVulnerableEnemy),
    /// Units/Enemies.sjson, Projectiles/Enemy_BiomeF_Projectiles.sjson. 1 пиксель Hades = 4.1 / 540 юнита.</summary>
    [Serializable]
    public sealed class GhostTuning
    {
        public float hp = Tier1.EnemyHp[Tier1.StrongMinus];      // 975
        public float damage = Tier1.EnemyDamage[Tier1.WeakPlus]; // 50 за удар
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

    /// <summary>Враг 2 — поведение Lubber (ZombieHeavyRanged) из Hades II: начальный враг Эфиры, бьющий по области.
    /// Подходит на дистанцию броска, долго замахивается, следя за Смертью, и кидает бочку туда, где она стоит: взрыв,
    /// затем огонь на 30 с. После броска отходит. Вблизи — толчок. Атакуют одновременно не больше трёх (SurroundAI).
    /// HP и урон — Bunny Butler из DandyAce. Источники: EnemyData_ZombieHeavyRanged.lua, WeaponData_Zombie.lua,
    /// ProjectileData_EnemyN.lua, Projectiles/Enemy_BiomeN_Projectiles.sjson, Units/Enemies.sjson.</summary>
    [Serializable]
    public sealed class StomperTuning
    {
        public float hp = Tier1.EnemyHp[Tier1.Mid];              // 725
        public float damage = Tier1.EnemyDamage[Tier1.MidMinus]; // 59 — взрыв бочки и толчок
        public float radius = 0.33f;
        public float fearSpeed = 2.5f;
        public float aggroRange = 8.35f;       // AIAggroRange 1100
        public float reactionMin = 0.05f;
        public float reactionMax = 0.2f;
        public float wanderDistance = 1.52f;
        public float wanderInterval = 1.5f;
        public float speed = 1.33f;            // Speed 175
        public int maxAttackers = 3;           // SurroundAI MaxAttackers
        public float surroundRefresh = 1.5f;   // SurroundRefreshInterval
        // бросок — ZombieHeavyRangedToss
        public float tossMinDistance = 2.66f;  // Requirements.MinPlayerDistance 350
        public float tossDistance = 7.67f;     // AttackDistance 1010
        public float tossWindup = 2.1f;        // PreAttackDuration, TrackTargetDuringCharge
        public float tossRelease = 0.165f;     // FireProjectileStartDelay
        public float tossRecovery = 1.75f;     // PostAttackDuration
        public float tossMinRange = 2.88f;     // MinRange 380
        public float tossMaxRange = 7.59f;     // Range 1000
        public float barrelSpeed = 7.59f;      // MaxSpeed 1000
        public float blastRadius = 2.28f;      // DamageRadius 300, ScaleY 0.5 — круг на полу
        public float fireRadius = 1.52f;       // ZombieHRFire DamageRadius 200
        public float fireLife = 30f;           // TotalFuse
        public float fireTick = 0.2f;          // Fuse
        public float fireRamp = 0.5f;          // DamagePerConsecutiveHit: n-й тик подряд — ⌊0.5·n⌋ урона Hades
        public float hadesTossDamage = 15f;    // урон бочки в Hades: огонь переводится в наш урон множителем damage / 15
        public float retreatDistance = 7.59f;  // RetreatBufferDistance 1000
        public float retreatTimeMin = 2f;      // RetreatTimeoutMin
        public float retreatTimeMax = 3f;      // RetreatTimeoutMax
        // толчок — ZombieHeavyRangedShove
        public float shoveMaxDistance = 3.45f; // Requirements.MaxPlayerDistance 455
        public float shoveDistance = 2.66f;    // AttackDistance 350
        public float shoveWindup = 1f;         // PreAttackDuration
        public float shoveFire = 0.4f;         // FireDuration
        public float shoveRecovery = 1.1f;     // PostAttackDuration
        public float shoveReach = 2.13f;       // Points (−10, ±40) … (280, ±90)
        public float shoveBaseHalf = 0.3f;
        public float shoveTipHalf = 0.68f;
        public float shoveActive = 0.2f;       // Fuse
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
