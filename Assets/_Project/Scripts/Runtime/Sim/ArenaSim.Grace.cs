using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Милость — валюта забега (как гильзы Enter the Gungeon): малахитовые ромбики выпадают из убитых врагов, разлетаются,
    /// а ближе ArenaTuning.graceMagnet летят к Смерти и подбираются сами. Тратится на переброс пятачка и в магазине.
    /// Магазин — комната на этаже (ShopRoom): карты и тринкеты I с ценой; купленное пропадает с прилавка, остальное ждёт до конца этажа.</summary>
    public sealed partial class ArenaSim
    {
        /// <summary>Милость забега. В начале забега — 0.</summary>
        public int Grace { get; private set; }
        /// <summary>Комната-магазин этажа (−1 — нет).</summary>
        public int ShopRoom { get; private set; } = -1;
        /// <summary>На пятачке сейчас товар магазина (у предметов цена, купленный не сжигает остальные).</summary>
        public bool LootIsShop => lootShop;
        readonly List<GroundCard> shopStock = new();
        bool lootShop, shopFilled;

        /// <summary>Добавить Милость (подбор ромбика; тесты).</summary>
        public void AddGrace(int n) => Grace = Mathf.Max(0, Grace + n);

        void ResetGrace()
        {
            Grace = 0;
            shopStock.Clear();
            shopFilled = false;
            lootShop = false;
        }

        // ---------- ромбики ----------

        /// <summary>Убитый враг роняет Милость: обычный — graceMin…graceMax, мини-босс — graceElite, босс — graceBoss. Ромбик стоит
        /// 1, у элиток — по 5 (меньше предметов на полу).</summary>
        void DropGrace(Enemy e)
        {
            if (!FormsMode || HubMode || e.Dummy) return;
            var c = ArenaCfg;
            bool boss = e.Kind == EnemyKind.Megaera || (e.Elite && Floor != null && ActiveRoom >= 0 && Floor.Rooms[ActiveRoom].Elite == 2);
            bool big = boss || e.Elite;
            int total = big ? (boss ? c.graceBoss : c.graceElite) : rng.Next(c.graceMin, c.graceMax + 1);
            int piece = big ? 5 : 1;
            while (total > 0)
            {
                int v = Mathf.Min(piece, total);
                total -= v;
                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                Add(new Effect
                {
                    Kind = EffectKind.Grace, Pos = e.Pos, Dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a)),
                    Speed = c.graceScatter * (0.5f + (float)rng.NextDouble()), T = c.graceSettle, Charges = v, R = 0.2f, Life = 999f,
                });
            }
        }

        void UpdateGraceOrb(Effect f, float dt)
        {
            var c = ArenaCfg;
            f.T -= dt;
            if (f.Speed > 0f && f.Hold <= 0f)
            {
                // выпал — разлетается и тормозит, не вылетая за стены
                var next = f.Pos + f.Dir * (f.Speed * dt);
                if (InsideRoom(next, 0.15f) && !BlocksShot(next, 0.1f)) f.Pos = next;
                f.Speed = Mathf.Max(0f, f.Speed - 10f * dt);
            }
            var to = Player.Pos - f.Pos;
            float d = to.magnitude;
            if (d <= c.gracePickup)
            {
                f.Done = true;
                Grace += f.Charges;
                Events.Add(new SimEvent { Kind = EventKind.GraceCollected, Pos = Player.Pos, Value = f.Charges });
                return;
            }
            if (f.T > 0f || (d > c.graceMagnet && f.Hold <= 0f)) return;
            // притягивается: однажды поймав, летит до конца, разгоняясь
            f.Hold += dt;
            float speed = c.graceFlySpeed * Mathf.Clamp01(0.35f + f.Hold * 2f);
            f.Pos += to / d * Mathf.Min(d, speed * dt);
        }

        // ---------- магазин ----------

        /// <summary>Комната-магазин: первая комната награды схемы ETG; если её нет — мирная комната (не вход, не выход, не босс)
        /// дальше всех от входа.</summary>
        static int PickShopRoom(EtgFloorLayout f)
        {
            for (int i = 0; i < f.Rooms.Count; i++)
                if (f.Rooms[i].Kind == EtgRoomKind.Reward) return i;
            int best = -1;
            float far = -1f;
            var start = f.Rooms[f.Entrance].Center;
            for (int i = 0; i < f.Rooms.Count; i++)
            {
                var r = f.Rooms[i];
                if (r.Combat || i == f.Entrance || i == f.Exit || r.Kind is EtgRoomKind.Boss or EtgRoomKind.BossFoyer) continue;
                float d = (r.Center - start).sqrMagnitude;
                if (d > far) { far = d; best = i; }
            }
            return best;
        }

        /// <summary>Вошёл в магазин: прилавок — две строки по четыре предмета (карты и тринкеты I) с ценой.
        /// В первый раз прилавок заполняется, потом — что осталось.</summary>
        void OpenShop()
        {
            var room = Floor.Rooms[ShopRoom];
            var c = ArenaCfg;
            if (!shopFilled)
            {
                shopFilled = true;
                var items = new List<GroundCard>();
                foreach (var t in RollTrinketOffers(c.shopTrinkets, 1)) items.Add(new GroundCard { Trinket = t, Price = c.shopTrinketPrice });
                for (int i = 0; i < c.shopCards; i++)
                {
                    var card = RandomLootCard(items);
                    items.Add(new GroundCard { Card = card, Price = c.shopCardPrice[Mathf.Clamp((int)card.Rarity, 0, c.shopCardPrice.Length - 1)] });
                }
                // две строки по четыре — по горизонтали экрана, строки — вдоль вертикали экрана
                int cols = 4;
                float half = c.pedestalHalf * (cols - 1);
                var rect = room.Rect;
                var mid = PedestalSpot(new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMax), room.Center, half);
                var up = Vector2.Perpendicular(ScreenRight);
                for (int i = 0; i < items.Count; i++)
                {
                    int row = i / cols, col = i % cols;
                    float t = -1f + 2f * col / (cols - 1);
                    items[i].Pos = mid + ScreenRight * (half * t) + up * (row == 0 ? 1.1f : -1.1f);
                }
                shopStock.AddRange(items);
                PedestalPos = mid;
            }
            ClearLoot();
            Loot.AddRange(shopStock);
            lootShop = true;
            Pedestal = Loot.Count > 0;
            LootReadyAt = Time + ArenaCfg.lootAppear;
            Events.Add(new SimEvent { Kind = EventKind.LootDropped, Pos = PedestalPos });
        }

        /// <summary>Цена открытой в выборе награды (0 — пятачок, бесплатно).</summary>
        public int OpenPrice => lootOpen?.Price ?? 0;

        /// <summary>Хватает Милости на открытую награду.</summary>
        public bool CanAfford => OpenPrice <= Grace;

        /// <summary>Покупка в магазине: Милость списывается, купленный предмет пропадает с прилавка, остальные остаются.</summary>
        void FinishPurchase()
        {
            Grace -= lootOpen.Price;
            Loot.Remove(lootOpen);
            shopStock.Remove(lootOpen);
            Events.Add(new SimEvent { Kind = EventKind.GraceSpent, Pos = lootOpen.Pos, Value = lootOpen.Price });
            RewardPending = false;
            Offers.Clear();
            lootOpen = null;
            TrinketOffer = null;
            Pedestal = Loot.Count > 0;
        }
    }
}
