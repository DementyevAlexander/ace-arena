using System.Collections.Generic;

namespace Arena.Core
{
    public enum CardColor { Pink, Yellow, Blue, Staff }

    public enum CardId
    {
        TwinDaggers, FiveOfAKind, Crescendo, Roses, Dio,
        BlackHole, Dazzling, Starburst, Aegis, MasterfulJump,
        Dash, JumpScare, BlazingSprint, ShockingArrival, EscapeArtist,
        StaffAttack, StaffSpecial, StaffCast, StaffDash, // посох Мелинои из Hades II
    }

    public enum CcKind { None, Stun, Fear }

    /// <summary>Длительность контроля зависит от «цикла» главной карты (cyclesJson, Dazzle/Fear).</summary>
    public static class Cycles
    {
        static readonly Dictionary<string, float> Cc = new()
        {
            ["0.5-"] = 0.5f, ["0.75"] = 0.75f, ["1"] = 1f, ["2"] = 1f, ["3"] = 1.5f, ["4"] = 1.5f, ["8"] = 2.5f,
        };

        public static float CcDuration(string cycle) => Cc.TryGetValue(cycle, out var v) ? v : 1f;
    }

    /// <summary>Параметры карты. Числа — из префабов и skillsJson DandyAce, тир 1; дальность и угол автолока — по образцу Hades.</summary>
    public sealed class CardDef
    {
        public CardId Id;
        public string Name;
        public CardColor Color;
        public string Kind;
        public float Cooldown;
        public int Charges = 1;
        public float Casting;
        public float Recovery;
        /// <summary>Максимум удержания (Crescendo Fireworks), 0 — карта без удержания.</summary>
        public float HoldTime;
        public string Cycle = "1";
        /// <summary>Автолок по-Hades: дальность и допустимое отклонение от направления прицела (±градусы). 0 — карта не целится.</summary>
        public float LockRange;
        public float LockArc;
        /// <summary>На сколько хитбоксов делится добавочный урон улучшений (DamageIncreaseHitboxDivider).</summary>
        public float Div = 1f;
        public bool Interruptible = true;
        public bool CanInterrupt;
        /// <summary>Удержание кнопки повторяет каст (IsSpammable).</summary>
        public bool Spammable;
        public CcKind OwnCc;
        public string DamageText;
        public string[] Lines;

        public bool IsMovement => Color == CardColor.Blue;
        public bool Aims => LockRange > 0f;
        public float CcDuration => Cycles.CcDuration(Cycle);
    }

    public static class CardCatalog
    {
        // Дальности автолока выведены из пресетов автоприцела DandyAce (offset + distance + radius).
        const float MidPlus = 8.3f, Mid = 6.3f, Short = 4.3f, Long = 10.3f;
        const float ArcShot = 75f, ArcWide = 100f;

        public static readonly CardDef[] All =
        {
            new() { Id = CardId.TwinDaggers, Name = "Twin Daggers", Color = CardColor.Pink, Kind = "Снаряд", Cooldown = 0.75f, Casting = 0.1f, Recovery = 0.1f,
                Cycle = "1", LockRange = MidPlus, LockArc = ArcShot, Div = 2, Spammable = true, DamageText = "100×2",
                Lines = new[] { "Два кинжала появляются перед Ace и через 0.4 с", "летят вперёд, пробивая врагов.", "Bleed: 63 за 4 с с каждого кинжала." } },
            new() { Id = CardId.FiveOfAKind, Name = "Five of a Kind", Color = CardColor.Pink, Kind = "Веер", Cooldown = 0.6f, Charges = 5, Casting = 0.03f, Recovery = 0.1f,
                Cycle = "0.5-", LockRange = Mid, LockArc = ArcShot, Div = 5, Spammable = true, DamageText = "35×5",
                Lines = new[] { "Веер из пяти карт (30°).", "5 зарядов, каждый перезаряжается отдельно.", "" } },
            new() { Id = CardId.Crescendo, Name = "Crescendo Fireworks", Color = CardColor.Pink, Kind = "Удержание", Cooldown = 0.75f, Casting = 0.1f, Recovery = 0.05f,
                HoldTime = 0.5f, Cycle = "0.75", LockRange = MidPlus, LockArc = ArcShot, Spammable = true, DamageText = "62~124",
                Lines = new[] { "Держите кнопку до 0.5 с: взрыв больше, урон до ×2.", "Ракета взрывается о врага.", "Burn: 248 за 4 с." } },
            new() { Id = CardId.Roses, Name = "Poisonous Roses", Color = CardColor.Pink, Kind = "Заряды", Cooldown = 0.75f, Charges = 3, Casting = 0.03f, Recovery = 0.1f,
                Cycle = "0.75", LockRange = Long, LockArc = ArcShot, Div = 3, Spammable = true, DamageText = "25×3",
                Lines = new[] { "Три розы сходятся в одну точку.", "3 заряда, каждый перезаряжается отдельно.", "Poison: 125 за 4 с с каждой розы." } },
            new() { Id = CardId.Dio, Name = "Dio, the Bat", Color = CardColor.Pink, Kind = "Реактивация", Cooldown = 0.5f, Casting = 0.1f, Recovery = 0.1f,
                Cycle = "0.5-", LockRange = MidPlus, LockArc = ArcShot, Div = 2, Spammable = true, DamageText = "100+100",
                Lines = new[] { "Мышь улетает вперёд и висит 1.5 с.", "Нажмите снова — вернётся и ударит ещё раз.", "Bleed: 50 за 4 с за удар." } },

            new() { Id = CardId.BlackHole, Name = "Black Hole", Color = CardColor.Yellow, Kind = "Область", Cooldown = 7f, Casting = 0.2f, Recovery = 0.1f,
                Cycle = "8", LockRange = Short, LockArc = ArcWide, Div = 10, DamageText = "300/3с",
                Lines = new[] { "Чёрная дыра на 3 с перед Ace.", "Затягивает врагов в центр (Pull)", "и бьёт каждые 0.3 с." } },
            new() { Id = CardId.Dazzling, Name = "Dazzling Display", Color = CardColor.Yellow, Kind = "Конус", Cooldown = 4f, Casting = 0.1f, Recovery = 0.1f,
                Cycle = "4", LockRange = Mid, LockArc = ArcWide, OwnCc = CcKind.Stun, DamageText = "125",
                Lines = new[] { "Конус слепящего света перед Ace.", "Stun: 1.5 с.", "" } },
            new() { Id = CardId.Starburst, Name = "Starburst", Color = CardColor.Yellow, Kind = "Взрыв вокруг", Cooldown = 2f, Casting = 0.05f, Recovery = 0.05f,
                Cycle = "3", CanInterrupt = true, DamageText = "225",
                Lines = new[] { "Вспышка вокруг Ace отбрасывает врагов (Push).", "Прерывает другие карты.", "" } },
            new() { Id = CardId.Aegis, Name = "Aegis Cloak", Color = CardColor.Yellow, Kind = "Щит", Cooldown = 2.5f, Casting = 0f, Recovery = 0.05f,
                Cycle = "2", Interruptible = false, CanInterrupt = true, DamageText = "100",
                Lines = new[] { "Щит на 1 с: следующий удар не проходит.", "Когда щит пропадает, он взрывается.", "Прерывает другие карты." } },
            new() { Id = CardId.MasterfulJump, Name = "Masterful Jump", Color = CardColor.Yellow, Kind = "Прыжок", Cooldown = 2.5f, Casting = 0.001f, Recovery = 0.15f,
                Cycle = "3", CanInterrupt = true, DamageText = "220",
                Lines = new[] { "Прыжок к ближайшему врагу (до 5), неуязвим в полёте.", "Удар при приземлении.", "Убил — перезарядка сбрасывается." } },

            new() { Id = CardId.Dash, Name = "Dashing Dash", Color = CardColor.Blue, Kind = "Рывок", Cooldown = 0.7f, Casting = 0.001f, Recovery = 0.13f,
                Cycle = "0.5-", Interruptible = false, CanInterrupt = true, Spammable = true, DamageText = "125",
                Lines = new[] { "Рывок на 3.5 сквозь врагов.", "Бьёт тех, кого задел.", "" } },
            new() { Id = CardId.JumpScare, Name = "Jump Scare", Color = CardColor.Blue, Kind = "Телепорт", Cooldown = 1f, Casting = 0.001f, Recovery = 0.15f,
                Cycle = "0.75", Interruptible = false, CanInterrupt = true, Spammable = true, OwnCc = CcKind.Fear, DamageText = "175",
                Lines = new[] { "Мгновенный телепорт на 4.", "Взрыв в точке прибытия: Fear 0.75 с.", "" } },
            new() { Id = CardId.BlazingSprint, Name = "Blazing Sprint", Color = CardColor.Blue, Kind = "Рывок", Cooldown = 0.7f, Casting = 0.001f, Recovery = 0.13f,
                Cycle = "0.5-", Interruptible = false, CanInterrupt = true, Spammable = true, DamageText = "25+450",
                Lines = new[] { "Рывок на 3.5, позади синий огонь на 3 с.", "Огонь: 450 за 4 с.", "" } },
            new() { Id = CardId.ShockingArrival, Name = "Shocking Arrival", Color = CardColor.Blue, Kind = "Рывок", Cooldown = 0.7f, Casting = 0.001f, Recovery = 0.13f,
                Cycle = "0.5-", Interruptible = false, CanInterrupt = true, Spammable = true, Div = 2, DamageText = "25+125",
                Lines = new[] { "Рывок на 3.5 и взрыв в конце.", "Hex: 500 за 10 с, урон идёт,", "пока враг двигается." } },
            new() { Id = CardId.EscapeArtist, Name = "Escape Artist", Color = CardColor.Blue, Kind = "Телепорт", Cooldown = 1f, Casting = 0.001f, Recovery = 0.15f,
                Cycle = "0.75", Interruptible = false, CanInterrupt = true, Spammable = true, DamageText = "325/3с",
                Lines = new[] { "Телепорт на 4, на старом месте", "ядовитое облако на 3 с.", "" } },

            // Посох Мелинои (Hades II). Логика — Sim/ArenaSim.Staff.cs, числа — Core/Staff.cs. Cooldown — для интерфейса.
            new() { Id = CardId.StaffAttack, Name = "Посох: Атака", Color = CardColor.Staff, Kind = "Комбо", Cooldown = 0f, Cycle = "1",
                DamageText = "114 / 143 / 343",
                Lines = new[] { "Три удара комбо, удар из рывка.", "Удержание после удара — Ω-атака:", "волны вперёд и назад, 914." } },
            new() { Id = CardId.StaffSpecial, Name = "Посох: Спецатака", Color = CardColor.Staff, Kind = "Снаряд", Cooldown = 0.7f, Cycle = "1",
                DamageText = "229 / Ω 457",
                Lines = new[] { "Снаряд по отпусканию, пробивает врагов.", "Держать 0.65 с — Ω-снаряд:", "медленный, взрыв радиусом 2.5." } },
            new() { Id = CardId.StaffCast, Name = "Посох: Каст", Color = CardColor.Staff, Kind = "Круг", Cooldown = 0.2f, Cycle = "1",
                DamageText = "Ω 857",
                Lines = new[] { "Круг вокруг себя на 3 с:", "враги в нём не двигаются.", "Держать дальше — Ω: круг взорвётся." } },
            new() { Id = CardId.StaffDash, Name = "Посох: Рывок", Color = CardColor.Staff, Kind = "Рывок", Cooldown = 0.74f, Cycle = "1",
                DamageText = "—",
                Lines = new[] { "Рывок на 2.4, неуязвимость.", "Держать — спринт.", "Атака сразу после — удар из рывка." } },
        };

        public static CardDef Get(CardId id) => All[(int)id];
    }
}
