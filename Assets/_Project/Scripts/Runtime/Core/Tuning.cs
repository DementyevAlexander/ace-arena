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
        public float hp = 500f;
        public float radius = 0.3f;
        public float speed = 4.1f;
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
        // В DandyAce замах 0.4 и 0.3 с. Увеличено, чтобы из зоны успевал выйти игрок, отреагировавший сразу (тест GhostTests).
        public float windup1 = 0.6f;
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
