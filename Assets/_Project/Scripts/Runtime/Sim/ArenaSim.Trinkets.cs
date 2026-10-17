using System.Collections.Generic;
using Arena.Core;

namespace Arena.Sim
{
    /// <summary>Тринкеты (docs/trinkets.md): три слота, выпадают у мини-боссов и боссов. Новый — в пустой слот или вместо
    /// стоящего (тот пропадает вместе с уровнем); такой же уже стоит — его уровень растёт на 1, до V. Выбросить нельзя.
    /// Числа скиллов с тринкетами — FormCfg (TrinketBook.Apply), урон карты за души — TrinketBook.SoulDamageMul.</summary>
    public sealed partial class ArenaSim
    {
        public readonly TrinketTuning TrinketCfg = new();
        /// <summary>Слоты тринкетов (null — пуст).</summary>
        public readonly Trinket[] TrinketSlots = new Trinket[TrinketBook.Slots];
        /// <summary>Тринкет, выбор которого открыт (подобран с пятачка); null — выбора нет.</summary>
        public Trinket TrinketOffer { get; private set; }

        void ResetTrinkets()
        {
            System.Array.Clear(TrinketSlots, 0, TrinketSlots.Length);
            TrinketOffer = null;
            RecomputeTrinkets();
        }

        /// <summary>Пересчитать итоговые числа форм по базе и тринкетам.</summary>
        public void RecomputeTrinkets() => FormCfg = TrinketBook.Apply(FormBase, TrinketSlots, TrinketCfg);

        /// <summary>В каком слоте стоит тринкет этого вида; −1 — нигде.</summary>
        public int TrinketSlotOf(TrinketKind kind)
        {
            for (int i = 0; i < TrinketSlots.Length; i++) if (TrinketSlots[i]?.Kind == kind) return i;
            return -1;
        }

        /// <summary>Особый эффект тринкета включён: он стоит на уровне III и выше.</summary>
        public bool TrinketIII(TrinketKind kind) => TrinketBook.HasSpecial(kind, TrinketSlots);

        /// <summary>Ячейки душ карты: по редкости; Сосуд душ III — каждая вторая влитая душа даёт +1 ячейку (до 4).</summary>
        public int SoulCellsOf(SkillCard card)
        {
            if (card == null) return 0;
            if (!TrinketIII(TrinketKind.SoulVessel)) return card.SoulCells;
            return System.Math.Min(SkillCard.MaxSoulCells, card.SoulCells + card.Souls.Count / 2);
        }

        /// <summary>Предложенный тринкет уже стоит — выбор поднимает его уровень, слот не нужен.</summary>
        public bool TrinketOfferIsLevelUp => TrinketOffer != null && TrinketSlotOf(TrinketOffer.Kind) >= 0;

        public bool CanLevelTrinket => TrinketOfferIsLevelUp && TrinketSlots[TrinketSlotOf(TrinketOffer.Kind)].Level < TrinketBook.MaxLevel;

        /// <summary>Поставить новый тринкет в слот: пустой или занятый (прежний пропадёт вместе с уровнем).</summary>
        public bool CanPutTrinket(int slot) =>
            RewardPending && CanAfford && TrinketOffer != null && !TrinketOfferIsLevelUp && slot >= 0 && slot < TrinketSlots.Length;

        public void PutTrinket(int slot)
        {
            if (!CanPutTrinket(slot)) return;
            TrinketSlots[slot] = new Trinket(TrinketOffer.Kind, TrinketOffer.Level);
            RecomputeTrinkets();
            FinishReward();
        }

        /// <summary>Повтор: уровень стоящего тринкета +1 (до V).</summary>
        public void LevelTrinket()
        {
            if (!RewardPending || !CanLevelTrinket || !CanAfford) return;
            var t = TrinketSlots[TrinketSlotOf(TrinketOffer.Kind)];
            t.Level = System.Math.Min(TrinketBook.MaxLevel, t.Level + 1);
            RecomputeTrinkets();
            FinishReward();
        }

        /// <summary>Тринкеты на выбор (пятачок мини-босса и босса, позже магазин): разные виды, открытые, кроме стоящих на V.
        /// Если у игрока есть тринкет не на V — хотя бы один предложенный из таких (повышение всегда в выборе).</summary>
        public List<Trinket> RollTrinketOffers(int count, int level)
        {
            var pool = new List<TrinketKind>();
            var owned = new List<TrinketKind>();
            foreach (var k in TrinketBook.All)
            {
                if (!Meta.Has(k)) continue; // только открытые у Торговца
                int slot = TrinketSlotOf(k);
                if (slot >= 0 && TrinketSlots[slot].Level >= TrinketBook.MaxLevel) continue;
                pool.Add(k);
                if (slot >= 0) owned.Add(k);
            }
            var picks = new List<TrinketKind>();
            if (owned.Count > 0) picks.Add(owned[rng.Next(owned.Count)]);
            while (picks.Count < count && picks.Count < pool.Count)
            {
                var k = pool[rng.Next(pool.Count)];
                if (!picks.Contains(k)) picks.Add(k);
            }
            for (int i = picks.Count - 1; i > 0; i--) // стоящий — не всегда слева
            {
                int j = rng.Next(i + 1);
                (picks[i], picks[j]) = (picks[j], picks[i]);
            }
            return picks.ConvertAll(k => new Trinket(k, level));
        }
    }
}
