using System;

namespace Arena.Core
{
    /// <summary>Посох Мелинои (Witch's Staff) из Hades II — четыре скилла: Атака (комбо из трёх ударов, удар из рывка,
    /// Ω-атака по удержанию), Спецатака (снаряд, Ω-снаряд по удержанию), Каст (круг, Ω-каст) и Рывок (со спринтом).
    /// Источники: Game/Weapons/PlayerWeapons.sjson, Game/Projectiles/PlayerProjectiles.sjson, Scripts/WeaponData_Staff.lua,
    /// Scripts/WeaponData.lua, Scripts/WeaponLogic.lua, Scripts/HeroData.lua.
    /// Кадры — при 60 к/с; расстояния × HadesScale.Unit; урон × HadesScale.Hp (враги умирают за то же число ударов, что в Hades).
    /// Ω-приёмы в Hades стоят ману (атака 20, спецатака 10, каст 15) — маны в прототипе нет, Ω доступны всегда.</summary>
    [Serializable]
    public sealed class StaffTuning
    {
        const float F = 1f / 60f;
        const float U = HadesScale.Unit;
        const float D = HadesScale.Hp;

        // ---------- Атака: WeaponStaffSwing → Swing2 → Swing3 ----------
        /// <summary>ChargeTime — замах до удара (FireOnRelease = false: удар сам по окончании).</summary>
        public float[] swingCharge = { 0.06f, 0.11f, 0.36f };
        /// <summary>Блок атаки и поворота после удара: неотменяемый и отменяемый рывком (SwingNDisable / DisableCancellable,
        /// оба с момента удара, идут одновременно).</summary>
        public float[] swingLock = { 6 * F, 3 * F, 9 * F };
        public float[] swingLockCancel = { 8 * F, 13 * F, 18 * F };
        /// <summary>Те же эффекты держат на месте только у Swing2 и Swing3 (у Swing1 DisableMove = false — он лишь
        /// замедляет × 0.05 на 12 кадров, Swing1SelfSlowFire).</summary>
        public float[] swingRoot = { 0f, 3 * F, 9 * F };
        public float[] swingRootCancel = { 0f, 13 * F, 18 * F };
        /// <summary>RootOwnerWhileFiring — стоит, пока летит хитбокс удара.</summary>
        public bool[] swingRootWhileFiring = { true, true, false };
        /// <summary>SwingNDisableMoveHold: стоит и не поворачивается столько с момента удара (кнопка тут ни при чём), рывок снимает.</summary>
        public float[] swingMoveHold = { 11 * F, 19 * F, 30 * F };
        /// <summary>ControlWindow следующего удара (Swing2 — 0.4, Swing3 — 0.6): он держится на кнопке столько секунд с момента
        /// предыдущего удара; не нажал — комбо сначала. Нажатие во время блока копится (RequestTriggerLock).</summary>
        public float[] swingWindow = { 0.4f, 0.6f, 0f };
        public float[] swingDamage = { 20f * D, 25f * D, 60f * D };
        public float[] swingStartDelay = { 0f, 0.04f, 0.03f };
        /// <summary>Клин (0,0)–(130, ±Y): полуширина у основания.</summary>
        public float[] swingHalfWidth = { 140f * U, 140f * U, 200f * U };
        public float[] swingStun = { 0.5f, 0.5f, 1.1f }; // OnHitStun / OnHitStunHeavy
        public float[] swingLockRange = { 390f * U, 490f * U, 490f * U };
        public float swingLockArc = 130f;
        /// <summary>Swing1SelfSlowFire: после удара скорость × 0.05 ещё 12 кадров.</summary>
        public float swing1FireSlow = 12 * F;
        /// <summary>Снаряд удара: STRAIGHT, Speed 4000, Range 430, BarrelLength −150 (клин стартует за спиной и проносится вперёд).</summary>
        public float wedgeSpeed = 4000f * U;
        public float wedgeRange = 430f * U;
        public float wedgeBarrel = -150f * U;
        public float wedgeLength = 130f * U;
        /// <summary>ProjectileDefenseRadius 150 / ProjectileDefenseAngle 110: удар посоха гасит вражеские снаряды рядом с собой.</summary>
        public float defenseRadius = 150f * U;
        public float defenseHalfAngle = 55f;

        // ---------- Удар из рывка: WeaponStaffDash ----------
        public float dashStrikeWindow = 0.22f;  // DashManeuverTimeThreshold — после рывка/спринта
        public float dashStrikeQueue = 23 * F;  // DashAttackQueue: атака в рывке ждёт до этого момента от начала рывка
        public float dashStrikeCharge = 0.06f;
        public float dashStrikeLock = 5 * F;
        public float dashStrikeLockCancel = 14 * F;
        public float dashStrikeDamage = 30f * D;
        public float dashStrikeSpeed = 4800f * U;
        public float dashStrikeRange = 640f * U;
        public float dashStrikeStun = 0.5f;
        public float dashStrikeLockRange = 250f * U;
        public float dashStrikeLockArc = 150f;

        // ---------- Ω-атака: WeaponStaffSwing5 ----------
        public float omegaAttackCharge = 0.8f;    // ChargeTime, ForceMaxChargeRelease — срывается сама
        public float omegaAttackFailCooldown = 0.5f; // FailedToFireCooldownDuration — отпустили раньше
        public float omegaAttackLock = 20 * F;    // Swing5Disable
        public float omegaAttackLockCancel = 47 * F; // Swing5DisableCancellable
        public float omegaAttackDamage = 160f * D;
        public float omegaAttackSpeed = 4000f * U;
        public float omegaAttackRange = 1080f * U;
        public float omegaAttackStun = 0.7f;
        /// <summary>Трапеция ProjectileSwing5 (−130, ±130)…(−50, ±65) × GeometryScaleY 0.7 — тянется за точкой выстрела
        /// (BarrelLength 0): при выстреле фигура стоит от −130 до −50 пикселей, голова — в −50.</summary>
        public float omegaWaveBarrel = -130f * U;
        public float omegaWaveBackHalf = 130f * 0.7f * U;
        public float omegaWaveFrontHalf = 65f * 0.7f * U;
        public float omegaWaveLength = 80f * U;
        public float omegaAttackLockRange = 800f * U;
        public float omegaAttackLockArc = 130f;

        // ---------- Спецатака: WeaponStaffBall ----------
        public float specialChargeTime = 0.1f;    // ChargeTime — после него видна линия прицела (MinChargeForAimLine 1)
        public float specialMinCharge = 0.1f * 0.1f; // MinChargeToFire 0.1 × ChargeTime — короткое нажатие стреляет почти сразу
        public float specialOmegaHold = 0.71f;    // Special1DisableMoveHold с Ω-стадии: стоит и не поворачивается, рывок снимает
        public float specialOmegaWait = 0.65f;    // ChargeWeaponStages[1].Wait — с этого момента по отпусканию летит Ω-снаряд
        public float specialCooldown = 0.7f;
        public float specialOmegaCooldown = 0.31f;
        public float specialLock = 12 * F;        // Special1Disable: стоит, не поворачивается, не атакует
        public float specialLockCancel = 18 * F;  // Special1DisableCancellable — то же, отменяется рывком
        public float specialRecoil = 610f * U;    // SelfVelocity −610 — отдача назад
        public float specialDamage = 40f * D;
        public float specialSpeed = 5000f * U;
        public float specialRange = 880f * U;
        public float specialStartDelay = 0.08f;
        public float specialHoming = 20f;         // MaxAdjustRate, градусов в секунду
        public float specialRadius = 45f * U;     // треугольник (25, ±45), (−50, 0)
        public float specialStun = 0.5f;
        public float specialLockRange = 840f * U;
        public float specialLockArc = 75f;
        public float specialOmegaDamage = 80f * D;
        public float specialOmegaSpeed = 780f * U;
        public float specialOmegaStartDelay = 0.05f;
        public float specialOmegaRadius = 75f * U;
        public float specialOmegaBlast = 330f * U; // DamageRadius
        public float specialOmegaStun = 0.7f;

        // ---------- Каст: WeaponCast / WeaponCastArm ----------
        public float castCharge = 15 * F;          // ChargeTimeFrames 15, FireOnRelease = false; IgnoreOwnerAttackDisabled
        public float castChargeSlow = 0.6f;        // скорость × 0.6 …
        public float castSlowTime = 16 * F;        // … 16 кадров (WeaponCastSelfSlow)
        public float castCooldown = 0.2f;          // и ActiveProjectileCap 1: новый каст — только когда круг исчез
        public float castAttackLock = 8 * F;       // WeaponCastAttackDisable — отменяемый рывком
        public float castRadius = 430f * U;        // DamageRadius, ScaleY 0.5 — круг на полу
        public float castGrow = 430f / 2000f;      // BlastSpeed 2000 — круг растёт ≈ 0.215 с
        public float castLife = 3f;                // Fuse
        public float castRootTick = 0.15f;         // StartCastSlow: раз в 0.15 с ImpactSlow ×0.05 и ImpactGrip на 0.2 с
        public float castRootTime = 0.2f;
        public float castArmCharge = 0.915f;       // WeaponCastArm ChargeTime, ForceMaxChargeRelease
        public float castArmDamage = 150f * D;     // ArmedExpirationDamage
        public float castArmStun = 0.7f;
        public float castArmLock = 7 * F;          // стоит и не атакует, неотменяемо
        public float castArmLockCancel = 15 * F;   // не атакует, рывок снимает

        // ---------- Рывок: WeaponBlink, спринт: WeaponSprint ----------
        public float blinkCharge = 3 * F;          // ChargeTimeFrames 3, скорость × 1.2
        public float blinkDuration = 0.2f;         // BlinkDuration
        public float blinkRange = 320f * U;        // WeaponRange
        public float blinkInvulnCharge = 10 * F;   // INVULNERABLE + NoUnitCollision во время заряда
        public float blinkInvulnFire = 16 * F;     // и после выстрела
        public float blinkRegen = 0.74f;           // ClipSize 1, ClipRegenInterval 0.74 — отсчёт с конца рывка
        public float sprintDelay = 0.1f;           // Rush держится ещё 0.1 с после рывка — спринт (SwapOnFire WeaponSprint)
        public float sprintSpeed = 890f * U;       // SelfVelocityCap 890
        public float autoSprintHold = 0.42f;       // AutoSprintBuffer: Rush держится 0.42 с после рывка — спринт по удержанию

        // ---------- ощущение удара ----------
        /// <summary>HitSimSlowParameters: шаги {ScreenPreWait, Fraction, LerpTime} — ждать (реальное время), затем за LerpTime
        /// перейти к скорости симуляции Fraction (новый не начинается, пока идёт прежний; на убийстве второй шаг ждёт +0.04 с).
        /// Профили: 0–2 удары комбо, 3 удар из рывка, 4 Ω-атака, 5 Ω-снаряд, 6 снаряд спецатаки, 7 взрыв Ω-каста.</summary>
        public float[][] hitSlow =
        {
            new[] { 0.025f, 0.06f, 0f, 0.01f, 1f, 0.02f },
            new[] { 0.065f, 0.07f, 0f, 0.01f, 1f, 0.02f },
            new[] { 0.07f, 0.06f, 0f, 0.02f, 1f, 0.02f },
            new[] { 0.04f, 0.1f, 0f, 0.03f, 1f, 0.07f },
            new[] { 0.05f, 0.12f, 0.06f, 0.03f, 1f, 0.03f },
            new[] { 0.03f, 0.1f, 0f, 0.02f, 1f, 0.1f },
            new[] { 0.08f, 0.1f, 0f, 0.03f, 1f, 0.07f },
            new[] { 0.07f, 0.15f, 0f, 0.25f, 1f, 0.1f },
        };
        /// <summary>SimSlowDistanceThreshold — хитстоп только вблизи героя (у ударов посоха ограничения нет).</summary>
        public float[] hitSlowRange = { 999f, 999f, 999f, 999f, 999f, 180f * U, 300f * U, 180f * U };
        /// <summary>HitSimSlowCooldown — свой у каждого удара (у удара из рывка и снарядов нет).</summary>
        public float[] hitSlowCooldown = { 0.3f, 0.3f, 0.3f, 0f, 0.01f, 0f, 0f, 0f };
        /// <summary>HitScreenshake: Distance (пиксели → юниты) и Duration.</summary>
        public float[] hitShake = { 4f * U, 4f * U, 4f * U, 0f, 4f * U, 6f * U, 0f, 0f };
        public float[] hitShakeTime = { 0.08f, 0.08f, 0.23f, 0f, 0.08f, 0.34f, 0f, 0f };
        /// <summary>FireScreenshake {Distance, Duration, ScreenPreWait}: Swing3, Ω-атака, снаряд, Ω-снаряд; ChargeScreenshake Ω-атаки.</summary>
        public float[] swing3FireShake = { 2f * U, 0.1f, 0.08f };
        public float[] omegaFireShake = { 4f * U, 0.16f, 0.19f };
        public float[] ballFireShake = { 4f * U, 0.2f, 0.08f };
        public float[] omegaBallFireShake = { 3f * U, 0.1f, 0f };
        public float[] omegaChargeShake = { 2f * U, 1f, 0f };
        /// <summary>ChargeCameraMotion Ω-атаки: камера приближается в 1.08 раза за 1.1 с и возвращается за 0.4 с.</summary>
        public float omegaZoom = 1.08f, omegaZoomIn = 1.1f, omegaZoomOut = 0.4f;
    }
}
