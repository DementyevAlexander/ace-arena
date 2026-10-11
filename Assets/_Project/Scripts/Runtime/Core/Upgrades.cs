namespace Arena.Core
{
    public enum UpgradeKind
    {
        None, Bleed, Burn, DeathBleed, DeathPoison, WildCards,
        Stun, Fear, Pull, Push, Invulnerability, GuidedLight,
        BonusVsStun, BonusVsFear, BonusVsCc,
        Haste, BlazingShoes, Hex, NoxiousCloud,
    }

    /// <summary>Что даёт карта, положенная под главную (upgradeEffects в skillsJson).</summary>
    public readonly struct UpgradeEffect
    {
        public readonly UpgradeKind Kind;
        public readonly string Name;
        public readonly string Text;
        public readonly float Value;
        public readonly float Duration;
        /// <summary>Перезарядка эффекта улучшения (бафы — 10 с, UpgradeCooldownTimeSetting).</summary>
        public readonly float Cooldown;

        public UpgradeEffect(UpgradeKind kind, string name, string text, float value = 0f, float duration = 0f, float cooldown = 0f)
        {
            Kind = kind; Name = name; Text = text; Value = value; Duration = duration; Cooldown = cooldown;
        }

        public bool IsNone => Kind == UpgradeKind.None;
        public bool IsBonus => Kind is UpgradeKind.BonusVsStun or UpgradeKind.BonusVsFear or UpgradeKind.BonusVsCc;
        public bool IsCastBuff => Kind is UpgradeKind.Haste or UpgradeKind.Invulnerability or UpgradeKind.BlazingShoes;
        public bool IsSpawn => Kind is UpgradeKind.WildCards or UpgradeKind.NoxiousCloud or UpgradeKind.GuidedLight;
    }

    public static class Upgrades
    {
        /// <summary>Урон DoT улучшений и Death-эффектов: 125 × QualityMultiplier 1.5.</summary>
        public const float UpgradeDot = 188f;
        public const float BonusHard = 225f, BonusEasy = 144f;
        /// <summary>Внутренняя перезарядка эффектов, которые порождают объекты (оценка, в данных её нет).</summary>
        public const float SpawnCooldown = 0.5f;

        /// <summary>Эффект улучшения. Цвет главной карты меняет эффект: Stun/Fear/Pull — жёлтым и синим, бонус урона — розовым.</summary>
        public static UpgradeEffect For(CardId? up, CardId? main)
        {
            if (up == null || main == null) return default;
            var m = CardCatalog.Get(main.Value);
            bool pink = m.Color == CardColor.Pink;
            float cc = m.CcDuration;
            switch (up.Value)
            {
                case CardId.TwinDaggers: return new(UpgradeKind.Bleed, "Bleed", $"Bleed: {UpgradeDot:0} урона за 4 с при попадании", UpgradeDot);
                case CardId.FiveOfAKind: return new(UpgradeKind.WildCards, "Wild Cards", "Wild Cards: из задетого врага вылетают 5 карт по 80", 80f);
                case CardId.Crescendo: return new(UpgradeKind.Burn, "Burn", $"Burn: {UpgradeDot:0} урона за 4 с при попадании", UpgradeDot);
                case CardId.Roses: return new(UpgradeKind.DeathPoison, "Death Poison", $"Death Poison: убитый враг заражает соседей Poison ({UpgradeDot:0})", UpgradeDot);
                case CardId.Dio: return new(UpgradeKind.DeathBleed, "Death Bleed", $"Death Bleed: убитый враг заражает соседей Bleed ({UpgradeDot:0})", UpgradeDot);
                case CardId.BlackHole:
                    return pink
                        ? new(UpgradeKind.BonusVsCc, "Cull the Meek", $"Cull the Meek: +{BonusEasy:0} урона по врагам под контролем", BonusEasy)
                        : new(UpgradeKind.Pull, "Pull", "Pull: задетые враги притягиваются к Ace");
                case CardId.Dazzling:
                    return pink
                        ? new(UpgradeKind.BonusVsStun, "Prey Seeker", $"Prey Seeker: +{BonusHard:0} урона по оглушённым", BonusHard)
                        : new(UpgradeKind.Stun, "Stun", $"Stun: {cc:0.##} с (по циклу главной карты)", 0f, cc);
                case CardId.Starburst: return new(UpgradeKind.Push, "Push", "Push: задетые враги отлетают от Ace");
                case CardId.Aegis: return new(UpgradeKind.Invulnerability, "Invulnerability", "Invulnerability при касте: 1.5 с неуязвимости (раз в 10 с)", 0f, 1.5f, 10f);
                case CardId.MasterfulJump: return new(UpgradeKind.GuidedLight, "Guided Light", "Guided Light: луч к задетому врагу, 100 урона за 2 с", 100f, 2f);
                case CardId.Dash: return new(UpgradeKind.Haste, "Haste", "Haste при касте: скорость +50% на 5 с (раз в 10 с)", 1.5f, 5f, 10f);
                case CardId.JumpScare:
                    return pink
                        ? new(UpgradeKind.BonusVsFear, "Taste of Fear", $"Taste of Fear: +{BonusHard:0} урона по испуганным", BonusHard)
                        : new(UpgradeKind.Fear, "Fear", $"Fear: {cc:0.##} с (по циклу главной карты)", 0f, cc);
                case CardId.BlazingSprint: return new(UpgradeKind.BlazingShoes, "Blazing Shoes", "Blazing Shoes при касте: 5 с Ace оставляет огонь (585 за 4 с)", 585f, 5f, 10f);
                case CardId.ShockingArrival: return new(UpgradeKind.Hex, "Hex", "Hex: 500 урона за 10 с, пока враг двигается", 500f, 10f);
                case CardId.EscapeArtist: return new(UpgradeKind.NoxiousCloud, "Noxious Cloud", "Noxious Cloud: облако на задетом враге, 250 за 2 с", 250f, 2f);
            }
            return default;
        }

        /// <summary>Подсказка в колоде: эффект на розовой и на жёлтой/синей главной.</summary>
        public static string Summary(CardId up)
        {
            var onPink = For(up, CardId.TwinDaggers);
            var onYellow = For(up, CardId.BlackHole);
            return onPink.Name == onYellow.Name ? onPink.Name : $"{onYellow.Name} / {onPink.Name}";
        }
    }
}
