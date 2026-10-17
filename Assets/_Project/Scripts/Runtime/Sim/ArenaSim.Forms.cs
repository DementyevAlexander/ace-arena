using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Слот режима форм: карта скилла, перезарядка, состояние очереди снаряда, живые зоны, духи и барьер.</summary>
    public sealed class FormSlot
    {
        public SkillCard Card;
        public float Cd, CdTotal;
        public bool Holding;          // кнопка зажата — копится заряд, применение — при отпускании
        public float HoldT;           // сколько держится
        public int Burst;             // снаряд: сколько выстрелов очереди ещё впереди
        public float BurstT, BurstMul;
        public float Haste;           // Жатва снаряда: двойная скорострельность
        public float ReapLock;
        public bool FreeDash;         // Рикошет рывка: второй рывок уже бесплатный
        public float TriggerLock;     // Спуск: до следующего срабатывания
        public float Run;             // Разгон снаряда: сколько пробежано с прошлого выстрела
        public int Shots;             // Колчан III: каждый 4-й выстрел — сразу очередь
        public readonly List<Effect> Active = new();

        public bool Ready => Card != null && Cd <= 0f;

        public void Reset()
        {
            Cd = CdTotal = 0f; Holding = false; HoldT = 0f; Burst = 0; BurstT = BurstMul = 0f; Haste = ReapLock = TriggerLock = Run = 0f; FreeDash = false; Shots = 0;
            Active.Clear();
        }
    }

    /// <summary>Предмет на пятачке наград: карта (обычная комната) или тринкет (мини-босс, босс) — заполнено одно из двух.</summary>
    public sealed class GroundCard
    {
        public SkillCard Card;
        public Trinket Trinket;
        public Vector2 Pos;
        /// <summary>Цена в Милости (магазин); 0 — награда пятачка.</summary>
        public int Price;
    }

    /// <summary>Режим форм: 4 слота (ЛКМ, ПКМ, Q — снаряд, зона, призыв или барьер; Space — рывок), в каждом карта
    /// «форма + свойства». В конце каждой комнаты ближе к центру появляются две карточки: подошёл к одной, нажал
    /// «взаимодействие» — открывается выбор: поставить её в слот или влить её душу в карту слота.
    /// Числа — FormTuning (по Hades II). Свойства на каждой форме — FormText.Cell.</summary>
    public sealed partial class ArenaSim
    {
        /// <summary>Базовые числа форм и свойств (по Hades II). Поменял — вызови RecomputeTrinkets.</summary>
        public readonly FormTuning FormBase = new();
        /// <summary>Итоговые числа с тринкетами — их читают скиллы (TrinketBook.Apply поверх FormBase).</summary>
        public FormTuning FormCfg { get; private set; } = new();
        /// <summary>Сколько ещё можно перебросить награды пятачка (ArenaTuning.rerollsPerDrop за выдачу).</summary>
        public int RerollsLeft { get; private set; }
        int pedestalTrinketLevel;
        float healLeft; // Поглощение: сколько ещё можно вылечить до следующей награды
        public bool FormsMode { get; private set; }
        public readonly FormSlot[] Forms = { new(), new(), new(), new() };
        public readonly List<SkillCard> Offers = new();
        public bool RewardPending;
        public int Rewards;
        public const int DashSlot = 3;
        /// <summary>Карточки на пятачке (пусто — нет). Взята одна — остальные сгорают.</summary>
        public readonly List<GroundCard> Loot = new();
        /// <summary>В этой комнате уже выпал пятачок наград (место под карточки; после выбора пуст до выхода).</summary>
        public bool Pedestal;
        public Vector2 PedestalPos;
        /// <summary>Когда карточки вырастут целиком и их можно брать (по Time).</summary>
        public float LootReadyAt;
        GroundCard lootOpen;

        // рывок формы: из A в B равномерно (BlinkEaseIn/Out 0)
        bool fDashing, fDashReturn, fDashWall, fDashTouched, fDashKilled;
        Vector2 fDashFrom, fDashTo, fDashDir;
        float fDashT, fDashDur, fDashRange;
        FormSlot fDashSlot;
        readonly HashSet<Enemy> fDashHit = new();
        float fShotSlow;
        float fChargeMove = 1f; // скорость бега во время заряда: атакующие формы — 0 (стоит), защитные — 1
        bool fDashLeap;   // рывок над лавой или заряженный — герой в прыжке
        float fDashMul = 1f;
        bool doomBursting, sharing;

        public bool FormDashing => fDashing;
        /// <summary>Рывок над лавой или заряженный — для вида: дуга прыжка (0…1 по ходу рывка), иначе −1.</summary>
        public float FormLeap => fDashing && fDashLeap ? Mathf.Clamp01(fDashT / Mathf.Max(fDashDur, 0.0001f)) : -1f;

        /// <summary>Заряд слота из раздела его формы: порог, окно усиления, скорость бега во время заряда.
        /// Жатва снаряда (скорострельность) вдвое укорачивает порог и окно.</summary>
        (float time, float window, float move) ChargeOfForm(FormSlot s)
        {
            var c = FormCfg;
            var (t, w, m) = s.Card == null ? (c.shotChargeTime, c.shotChargeWindow, c.shotChargeMove) : s.Card.Form switch
            {
                FormKind.Shot => (c.shotChargeTime, c.shotChargeWindow, c.shotChargeMove),
                FormKind.Dash => (c.dashChargeTime, c.dashChargeWindow, c.dashChargeMove),
                FormKind.Zone => (c.zoneChargeTime, c.zoneChargeWindow, c.zoneChargeMove),
                FormKind.Summon => (c.summonChargeTime, c.summonChargeWindow, c.summonChargeMove),
                _ => (c.barrierChargeTime, c.barrierChargeWindow, c.barrierChargeMove),
            };
            float k = s.Haste > 0f ? 0.5f : 1f;
            return (Mathf.Max(0.01f, t) * k, Mathf.Max(0.01f, w) * k, Mathf.Clamp01(m));
        }

        /// <summary>Сколько набрано до порога, 0…1 (для шкалы и кольца).</summary>
        public float ChargeOf(FormSlot s) => s.Holding ? Mathf.Clamp01(s.HoldT / ChargeOfForm(s).time) : 0f;

        /// <summary>Сейчас окно усиления: порог пройден, окно ещё не кончилось — отпустишь, будет ×2.</summary>
        public bool InChargeWindow(FormSlot s)
        {
            if (!s.Holding) return false;
            var (t, w, _) = ChargeOfForm(s);
            return s.HoldT >= t && s.HoldT <= t + w;
        }

        /// <summary>Окно пропущено — отпустишь, будет обычное применение.</summary>
        public bool ChargeMissed(FormSlot s) => s.Holding && s.HoldT > ChargeOfForm(s).time + ChargeOfForm(s).window;

        bool formShooting;

        /// <summary>Новая комната за дверью: снаряды, зоны и духи прошлой комнаты исчезают, рывок обрывается.</summary>
        void ResetFormRoom()
        {
            foreach (var s in Forms) { s.Active.Clear(); s.Holding = false; s.Burst = 0; }
            fDashing = false; fShotSlow = 0f; formShooting = false;
            healLeft = FormCfg.siphonRoomCap * PlayerCfg.hp;
        }

        /// <summary>Включает режим форм и начинает забег заново: снаряд в ЛКМ, рывок в Space.</summary>
        public void EnableForms()
        {
            FormsMode = true;
            Restart();
        }

        void ResetForms()
        {
            foreach (var s in Forms) { s.Card = null; s.Reset(); }
            Forms[0].Card = new SkillCard(FormKind.Shot);
            Forms[DashSlot].Card = new SkillCard(FormKind.Dash);
            ApplyMentorSet(); // набор Наставника — в ПКМ и Q
            fDashing = false; fShotSlow = 0f; Rewards = 0;
            healLeft = FormCfg.siphonRoomCap * PlayerCfg.hp;
            RewardPending = false; Offers.Clear();
            ClearLoot();
            ResetTrinkets();
            ResetGrace();
        }

        // ---------- карточки ----------

        /// <summary>Конец комнаты: на пятачке ближе к центру комнаты из пола вырастают награды, рядом по горизонтали экрана.
        /// Обычная комната — карточки «форма + свойство» (ArenaTuning.lootCount), комната мини-босса — тринкеты
        /// (TrinketTuning.offerCount). Берётся одна, остальные сгорают.</summary>
        void DropLoot() => RaisePedestal(Room.min, Room.max, null, IsMiniBossRoom ? TrinketCfg.miniBossLevel : 0);

        /// <summary>Комната мини-босса: награда — тринкеты (A_MiniBoss01, C_MiniBoss01, D_Mini05, D_Mini13).</summary>
        public bool IsMiniBossRoom => !FloorMode && Room?.name != null && Room.name.Contains("Mini");

        /// <summary>Пятачок у заданной точки (этаж: комната награды, после босса) — там или на ближайшем месте, где он помещается.</summary>
        void DropLootAt(Vector2 at)
        {
            if (Loot.Count > 0) return; // прежние ещё лежат
            var w = new Vector2(10f, 10f);
            RaisePedestal(at - w, at + w, at, 0);
        }

        /// <summary>trinketLevel > 0 — тринкеты этого уровня, иначе карты. Между соседними предметами 2·pedestalHalf.</summary>
        void RaisePedestal(Vector2 lo, Vector2 hi, Vector2? center, int trinketLevel)
        {
            ClearLoot();
            var items = new List<GroundCard>();
            if (trinketLevel > 0)
                foreach (var t in RollTrinketOffers(TrinketCfg.offerCount, trinketLevel)) items.Add(new GroundCard { Trinket = t });
            if (items.Count == 0)
                for (int i = 0; i < Mathf.Max(1, ArenaCfg.lootCount); i++) items.Add(new GroundCard { Card = RandomLootCard(items) });
            int n = items.Count;
            float half = ArenaCfg.pedestalHalf * (n - 1);
            var at = PedestalSpot(lo, hi, center, half);
            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0f : -1f + 2f * i / (n - 1);
                items[i].Pos = at + ScreenRight * (half * t);
            }
            Loot.AddRange(items);
            Pedestal = true;
            PedestalPos = at;
            LootReadyAt = Time + ArenaCfg.lootAppear;
            RerollsLeft = ArenaCfg.rerollsPerDrop;
            pedestalTrinketLevel = items[0].Trinket != null ? trinketLevel : 0;
            healLeft = FormCfg.siphonRoomCap * PlayerCfg.hp; // лимит Поглощения — до следующей награды
            Events.Add(new SimEvent { Kind = EventKind.LootDropped, Pos = at });
        }

        /// <summary>Можно перебросить: Смерть у пятачка, карточки выросли, выбор не открыт, перебросы остались.
        /// Цена — ArenaTuning.rerollCost золота (пока золота нет — бесплатно).</summary>
        public bool CanReroll => RerollsLeft > 0 && !lootShop && LootNear != null && Grace >= ArenaCfg.rerollCost;

        /// <summary>Перебросить все награды пятачка: старые сгорают, на их местах вырастают новые того же вида (карты или тринкеты
        /// того же уровня) по тем же правилам выпадения. Не больше ArenaTuning.rerollsPerDrop раз за выдачу.</summary>
        public bool RerollLoot()
        {
            if (!CanReroll) return false;
            RerollsLeft--;
            Grace -= ArenaCfg.rerollCost;
            if (ArenaCfg.rerollCost > 0) Events.Add(new SimEvent { Kind = EventKind.GraceSpent, Pos = PedestalPos, Value = ArenaCfg.rerollCost });
            for (int i = 0; i < Loot.Count; i++) Events.Add(new SimEvent { Kind = EventKind.LootBurned, Pos = Loot[i].Pos, Value = i });
            var items = new List<GroundCard>();
            if (pedestalTrinketLevel > 0)
                foreach (var t in RollTrinketOffers(Loot.Count, pedestalTrinketLevel)) items.Add(new GroundCard { Trinket = t });
            if (items.Count == 0)
                for (int i = 0; i < Loot.Count; i++) items.Add(new GroundCard { Card = RandomLootCard(items) });
            for (int i = 0; i < items.Count; i++) items[i].Pos = Loot[Mathf.Min(i, Loot.Count - 1)].Pos;
            Loot.Clear();
            Loot.AddRange(items);
            LootReadyAt = Time + ArenaCfg.lootAppear;
            Events.Add(new SimEvent { Kind = EventKind.LootRerolled, Pos = PedestalPos });
            Events.Add(new SimEvent { Kind = EventKind.LootDropped, Pos = PedestalPos });
            return true;
        }

        /// <summary>Одна случайная карта по тем же правилам, что на пятачке (веса тринкетов, редкость) — для магазина и проверок.</summary>
        public SkillCard RollLootCard() => RandomLootCard(new List<GroundCard>());

        static readonly float[] FormDropWeights = { 1f, 0.6f, 1f, 1f, 1f }; // снаряд, рывок, зона, призыв, барьер

        /// <summary>Случайная карточка, не повторяющая свойство уже выложенных. Тринкеты направляют выпадение: свойство
        /// тринкета — ×traitDropWeight, форма — ×formDropWeight. Редкость — по ArenaTuning.rarityWeights.</summary>
        SkillCard RandomLootCard(List<GroundCard> already)
        {
            var formW = new float[FormDropWeights.Length];
            for (int i = 0; i < formW.Length; i++)
                formW[i] = Meta.Has((FormKind)i) ? FormDropWeights[i] * TrinketBook.FormWeight((FormKind)i, TrinketSlots, TrinketCfg) : 0f; // только открытые
            var traitW = new float[Traits.Count];
            for (int i = 0; i < traitW.Length; i++)
                traitW[i] = !Meta.Has((Trait)i) || already.Exists(g => g.Card?.Own == (Trait)i) ? 0f : TrinketBook.TraitWeight((Trait)i, TrinketSlots, TrinketCfg);
            var card = new SkillCard((FormKind)WeightedPick(formW), (Trait)WeightedPick(traitW));
            card.Rarity = Rarities.Roll(rng, ArenaCfg.rarityWeights);
            return card;
        }

        int WeightedPick(float[] w)
        {
            float sum = 0f;
            foreach (var x in w) sum += x;
            if (sum <= 0f) return 0; // ничего не открыто — первое (снаряд, пламя)
            double r = rng.NextDouble() * sum;
            for (int i = 0; i < w.Length; i++)
            {
                r -= w[i];
                if (r < 0) return i;
            }
            return w.Length - 1;
        }

        void ClearLoot()
        {
            Loot.Clear();
            Pedestal = false;
            lootOpen = null;
            TrinketOffer = null;
            RerollsLeft = 0;
            lootShop = false;
        }

        /// <summary>Ближайшая к Смерти карточка пятачка не дальше lootReach. Null — нет такой, карточки ещё вырастают
        /// или выбор уже открыт.</summary>
        public GroundCard LootNear
        {
            get
            {
                if (RewardPending || GameOver || Time < LootReadyAt) return null;
                GroundCard best = null;
                float bd = ArenaCfg.lootReach * ArenaCfg.lootReach;
                foreach (var g in Loot)
                {
                    float d = (Player.Pos - g.Pos).sqrMagnitude;
                    if (d <= bd) { bd = d; best = g; }
                }
                return best;
            }
        }

        /// <summary>Смерть стоит у карточки и выбор не открыт — можно подобрать.</summary>
        public bool LootInReach => LootNear != null;

        /// <summary>Подобрать ближайшую награду («взаимодействие» рядом с ней): открывается выбор карты (Offers) или тринкета
        /// (TrinketOffer), бой стоит.</summary>
        public bool PickUpLoot()
        {
            var g = LootNear;
            if (g == null) return false;
            lootOpen = g;
            RewardPending = true;
            Offers.Clear();
            if (g.Trinket != null) TrinketOffer = g.Trinket; // выбор тринкета: Offers пуст
            else Offers.Add(g.Card);
            Events.Add(new SimEvent { Kind = EventKind.RewardReady });
            return true;
        }

        /// <summary>Номер открытой в выборе награды на пятачке (−1 — выбор закрыт).</summary>
        public int LootOpenIndex => RewardPending && lootOpen != null ? Loot.IndexOf(lootOpen) : -1;

        /// <summary>Перелистнуть выбор на соседнюю награду пятачка (dir −1 / +1, по кругу): сгорят потом остальные — не взятая.</summary>
        public bool CycleLoot(int dir)
        {
            if (!RewardPending || lootOpen == null || Loot.Count < 2 || dir == 0) return false;
            int i = (Loot.IndexOf(lootOpen) + (dir > 0 ? 1 : -1) + Loot.Count) % Loot.Count;
            lootOpen = Loot[i];
            Offers.Clear();
            TrinketOffer = null;
            if (lootOpen.Trinket != null) TrinketOffer = lootOpen.Trinket;
            else Offers.Add(lootOpen.Card);
            return true;
        }

        /// <summary>Закрыть выбор, не применяя: награды остаются на пятачке, до выхода из комнаты можно взять любую.</summary>
        public void LeaveLoot()
        {
            RewardPending = false;
            Offers.Clear();
            lootOpen = null;
            TrinketOffer = null;
        }

        /// <summary>Поставить карточку в слот: рывок — только в Space, остальные — в ЛКМ, ПКМ, Q. Прежняя карта пропадает.</summary>
        public bool CanTake(int offer, int slot) =>
            RewardPending && CanAfford && offer >= 0 && offer < Offers.Count && slot >= 0 && slot < 4 && (Offers[offer].Form == FormKind.Dash) == (slot == DashSlot);

        /// <summary>Влить душу карточки (её свойство) в карту слота: форма остаётся от карты слота.</summary>
        public bool CanFuse(int offer, int slot) =>
            RewardPending && CanAfford && offer >= 0 && offer < Offers.Count && slot >= 0 && slot < 4 && Forms[slot].Card != null && Forms[slot].Card.Souls.Count < SoulCellsOf(Forms[slot].Card);

        public void TakeOffer(int offer, int slot)
        {
            if (!CanTake(offer, slot)) return;
            foreach (var f in Forms[slot].Active) f.Done = true;
            Forms[slot].Card = Offers[offer];
            Forms[slot].Reset();
            MarkBonds();
            FinishReward();
        }

        public void FuseOffer(int offer, int slot)
        {
            if (!CanFuse(offer, slot)) return;
            Forms[slot].Card.Souls.Add(Offers[offer].Own.Value);
            MarkBonds();
            FinishReward();
        }

        void FinishReward()
        {
            if (lootShop && lootOpen != null) { FinishPurchase(); return; } // магазин: остальные не сгорают
            RewardPending = false;
            Offers.Clear();
            for (int i = 0; i < Loot.Count; i++)
                if (Loot[i] != lootOpen) Events.Add(new SimEvent { Kind = EventKind.LootBurned, Pos = Loot[i].Pos, Value = i });
            Loot.Clear(); // пятачок остаётся пустым
            lootOpen = null;
            TrinketOffer = null;
            Rewards++;
            waveTimer = ArenaCfg.waveDelay;
        }

        // ---------- герой ----------

        void UpdatePlayerForms(float dt, SimInput input)
        {
            var p = Player;
            var c = FormCfg;
            p.HitInvuln -= dt; p.BuffInvuln -= dt; p.DashInvuln -= dt; p.HitStun -= dt; p.Slow -= dt;
            fShotSlow -= dt;
            var before = p.Pos;
            bool shooting = false;
            fChargeMove = 1f;
            for (int i = 0; i < 4; i++)
            {
                var s = Forms[i];
                s.Haste -= dt; s.ReapLock -= dt; s.TriggerLock -= dt;
                s.Active.RemoveAll(f => f.Done);
                if (s.Card == null || (s.Card.Form == FormKind.Barrier && TrinketBook.BarrierSealed(TrinketSlots))) { s.Holding = false; continue; }
                // зона и барьер: новая — только когда прежняя исчезла (ActiveProjectileCap 1)
                if (s.Card.Form is FormKind.Zone or FormKind.Barrier)
                {
                    float left = 0f, first = float.MaxValue;
                    int zones = 0;
                    foreach (var f in s.Active)
                    {
                        if (f.Kind is not (EffectKind.FormZone or EffectKind.FormBarrier)) continue;
                        float l = f.Life + (f.Kind == EffectKind.FormZone && f.Card.Has(Trait.Return) ? 0.5f : 0f);
                        left = Mathf.Max(left, l);
                        if (f.Kind == EffectKind.FormZone) { zones++; first = Mathf.Min(first, l); }
                    }
                    if (s.Card.Form == FormKind.Zone && TrinketIII(TrinketKind.Compass)) left = zones >= 2 ? first : 0f; // Компас III: две зоны
                    if (left > 0f) { float extra = s.Card.Form == FormKind.Zone ? c.zoneCooldown : c.barrierCooldown; s.Cd = Mathf.Max(s.Cd, left + extra); s.CdTotal = Mathf.Max(s.CdTotal, s.Cd); }
                }
                s.Cd -= dt;
                if (s.Burst > 0) { UpdateBurst(s, i, dt, input); shooting = true; }
                // заряд как у лука Hades: нажал — копится, отпустил — применение с набранным зарядом
                if (input.IsPressed(i) && !s.Holding && s.Ready && s.Burst == 0 && !fDashing) { s.Holding = true; s.HoldT = 0f; }
                if (!s.Holding) continue;
                s.HoldT += dt;
                fChargeMove = Mathf.Min(fChargeMove, ChargeOfForm(s).move);
                if (s.Card.Form == FormKind.Shot)
                {
                    shooting = true;
                    p.Face = AimAssist(c.shotLockRange, c.shotLockArc, c.shotLockWithMouse, input);
                }
                if (input.IsHeld(i) || fDashing) continue;
                bool boosted = InChargeWindow(s);
                s.Holding = false;
                ReleaseForm(s, i, input, boosted ? (TrinketIII(TrinketKind.Hourglass) ? TrinketCfg.hourglassBoost : 1f) : 0f); // Песочные часы III: ×2.5
            }

            p.Moving = false;
            formShooting = shooting;
            if (fDashing) { UpdateFormDash(dt); p.Vel = Vector2.zero; }
            else MovePlayer(dt, input); // разгон Загрея, отбрасывание под Grip; заряд и выстрел замедляют
            if (p.Lock != null && (!p.Lock.Alive || (input.Held == 0 && !shooting))) p.Lock = null;
            p.Aim = shooting ? p.Face : ComputeAimPreview(input);
            CollideCircle(ref p.Pos, PlayerCfg.radius, before);
            float moved = (p.Pos - before).magnitude; // Разгон снаряда: пробег с прошлого выстрела, рывок тоже считается
            foreach (var s in Forms) s.Run = Mathf.Min(s.Run + moved, c.kineticRunDist * 2f);
        }

        /// <summary>Отпустил кнопку: применение формы с зарядом k (0 — обычное), «идеальный» выпуск — ×1.167 урона.
        /// Полный заряд — эффективность ×2: снаряд — очередь из двух, рывок — вдвое дальше, зона — вдвое больше площадь,
        /// призыв — вдвое дольше, барьер — вдвое больше ударов.</summary>
        void ReleaseForm(FormSlot s, int i, SimInput input, float k)
        {
            var c = FormCfg;
            const float pm = 1f;
            if (k > 0f) Events.Add(new SimEvent { Kind = EventKind.ChargeRelease, Pos = Player.Pos, Value = k, Crit = true });
            switch (s.Card.Form)
            {
                case FormKind.Shot:
                {
                    var dir = AimAssist(c.shotLockRange, c.shotLockArc, c.shotLockWithMouse, input);
                    Player.Face = dir;
                    float mul = pm;
                    if (TrinketIII(TrinketKind.Quiver) && ++s.Shots % 4 == 0) k = Mathf.Max(k, 1f); // Колчан III: 4-й выстрел — сразу очередь
                    FireShot(s, i, dir, mul);
                    s.Burst = Mathf.RoundToInt(c.shotVolley * k);
                    s.BurstT = c.shotVolleyInterval;
                    s.BurstMul = mul;
                    fShotSlow = c.shotFireSlowTime;
                    s.Cd = s.CdTotal = c.shotInterval * (s.Haste > 0f ? 0.5f : 1f) + s.Burst * c.shotVolleyInterval;
                    break;
                }
                case FormKind.Dash:
                    StartFormDash(s, input, k, pm);
                    break;
                default:
                    ActivateForm(s, input, k);
                    break;
            }
        }

        /// <summary>Очередь заряженного снаряда: выстрелы через shotVolleyInterval, прицел — по ходу (Aim Assist).</summary>
        void UpdateBurst(FormSlot s, int i, float dt, SimInput input)
        {
            var c = FormCfg;
            s.BurstT -= dt;
            if (s.BurstT > 0f || fDashing) return;
            var dir = AimAssist(c.shotLockRange, c.shotLockArc, c.shotLockWithMouse, input);
            Player.Face = dir;
            FireShot(s, i, dir, s.BurstMul);
            fShotSlow = c.shotFireSlowTime;
            s.Burst--;
            s.BurstT += c.shotVolleyInterval;
        }

        void FireShot(FormSlot s, int i, Vector2 dir, float mul = 1f)
        {
            var c = FormCfg;
            var card = s.Card;
            var origin = Player.Pos + dir * 0.4f;
            int kinetic = card.Level(Trait.Kinetic);
            if (kinetic > 0) mul *= 1f + c.kineticRunUp * kinetic * Mathf.Min(1f, s.Run / c.kineticRunDist); // разбег
            s.Run = 0f;
            SpreadShots(s, card, origin, dir, mul, false);
            int echo = card.Level(Trait.Echo);
            if (echo > 0)
                Add(new Effect { Kind = EffectKind.FormEchoShot, Pos = origin, Dir = dir, T = c.echoDelay, Card = card, FSlot = s, Mul = Mathf.Min(1f, c.echoFraction * echo) * mul });
            Events.Add(new SimEvent { Kind = EventKind.CardCast, Pos = Player.Pos, To = dir, Value = i, Color = 2 });
        }

        /// <summary>Раздвоение: веер из 1 + уровень снарядов.</summary>
        void SpreadShots(FormSlot s, SkillCard card, Vector2 origin, Vector2 dir, float mul, bool echoed)
        {
            int n = 1 + card.Level(Trait.Split);
            bool orbit = card.Has(Trait.Orbit);
            for (int k = 0; k < n; k++)
            {
                float a = orbit ? k * Mathf.PI * 2f / n : (k - (n - 1) * 0.5f) * FormCfg.splitAngle * Mathf.Deg2Rad;
                SpawnShot(s, card, origin, Rotate(dir, a), mul, echoed);
            }
        }

        int ActiveShots(FormSlot s)
        {
            int n = 0;
            foreach (var f in Effects) if (!f.Done && f.FSlot == s && f.Kind is EffectKind.FormShot or EffectKind.FormBolt) n++;
            return n;
        }

        Effect SpawnShot(FormSlot s, SkillCard card, Vector2 pos, Vector2 dir, float mul, bool echoed)
        {
            var c = FormCfg;
            if (ActiveShots(s) >= c.maxShotsPerSkill) return null;
            var f = Add(new Effect
            {
                Kind = EffectKind.FormShot, Pos = pos, Dir = dir, Speed = c.shotSpeed, Life = c.shotFuse, R = c.shotRadius,
                Card = card, FSlot = s, As = FormKind.Shot, Mul = mul, Echoed = echoed, Bounces = c.ricochetBounces * card.Level(Trait.Ricochet),
            });
            int orbit = card.Level(Trait.Orbit);
            if (orbit > 0) MakeOrbit(f, dir, c.orbitLife + (orbit - 1));
            return f;
        }

        // ---------- Орбита ----------

        /// <summary>Снаряд на орбите вокруг Смерти (Phase 3): кружит orbitSpeed, бьёт касанием, того же врага — не чаще orbitRehit.
        /// Жернова (Орбита + Пронзание): круг шире, бьёт вдвое чаще.</summary>
        void MakeOrbit(Effect f, Vector2 dir, float life)
        {
            var c = FormCfg;
            f.Phase = 3;
            f.Angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            f.Back = c.orbitRadius * (Bonds.Has(f.Card, Bond.Millstone) ? c.bondMillstone : 1f);
            f.Life = life;
            f.Pos = Player.Pos + dir * f.Back;
        }

        /// <summary>Осколки на орбите (рывок, барьер): n штук поровну по кругу, урон orbitShardDamage, свойства — по форме as.</summary>
        void SpawnShards(FormSlot s, SkillCard card, FormKind form, int n, float life)
        {
            var c = FormCfg;
            for (int k = 0; k < n; k++)
            {
                var dir = Rotate(Player.Face, k * Mathf.PI * 2f / n);
                var f = Add(new Effect
                {
                    Kind = EffectKind.FormShot, Pos = Player.Pos, Dir = dir, R = c.shotRadius, Card = card, FSlot = s, As = form,
                    Damage = c.orbitShardDamage, Payload = true,
                });
                MakeOrbit(f, dir, life);
            }
        }

        void UpdateOrbit(Effect f, float dt)
        {
            var c = FormCfg;
            f.Life -= dt;
            if (f.Life <= 0f) { f.Done = true; return; }
            f.Angle += c.orbitSpeed * dt;
            float a = f.Angle * Mathf.Deg2Rad;
            var r = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            f.Pos = Player.Pos + r * f.Back;
            f.Dir = new Vector2(-r.y, r.x); // по касательной
            f.Hold += dt;
            if (f.Hold >= c.orbitRehit / (Bonds.Has(f.Card, Bond.Millstone) ? 2f : 1f)) { f.Hold = 0f; f.Hit.Clear(); }
            HitRoomObjects(f.Pos, f.R);
            foreach (var e in Enemies)
            {
                if (!e.Alive || f.Hit.Contains(e) || (e.Pos - f.Pos).magnitude > f.R * f.Grow + e.R) continue;
                f.Hit.Add(e);
                ShotHit(f, e);
            }
            // как мечи и Orbital в Nova Drift: орбита сбивает вражеские снаряды
            foreach (var b in Effects)
                if (b.Kind == EffectKind.EnemyBolt && !b.Done && (b.Pos - f.Pos).magnitude <= f.R * f.Grow + b.R)
                {
                    b.Done = true;
                    Events.Add(new SimEvent { Kind = EventKind.PlayerBlocked, Pos = b.Pos });
                }
        }

        // ---------- Наведение ----------

        /// <summary>Наведение: доворачивает снаряд к ближайшему ещё не задетому врагу в homingRange — не круче homingTurn × уровень.</summary>
        void Steer(Effect f, int lvl, float dt)
        {
            if (lvl <= 0) return;
            var t = NearestEnemy(f.Pos, FormCfg.homingRange, null, f.Hit);
            if (t == null) return;
            var want = (t.Pos - f.Pos).normalized;
            float max = FormCfg.homingTurn * lvl * dt;
            f.Dir = Rotate(f.Dir, Mathf.Clamp(Vector2.SignedAngle(f.Dir, want), -max, max) * Mathf.Deg2Rad);
        }

        /// <summary>Ближайший враг в range и в ± arc градусах от dir.</summary>
        Enemy NearestInArc(Vector2 from, Vector2 dir, float range, float arc)
        {
            Enemy best = null;
            float bd = range;
            foreach (var e in Enemies)
            {
                if (!e.Alive) continue;
                var d = e.Pos - from;
                float m = d.magnitude;
                if (m < bd && m > 0.01f && Vector2.Angle(dir, d) <= arc) { bd = m; best = e; }
            }
            return best;
        }

        // ---------- Спуск ----------

        /// <summary>Спуск (Trigger из Noita): карта слота s применяет карту следующего непустого слота (ЛКМ → ПКМ → Q → Space → ЛКМ)
        /// в точке at с силой triggerFraction × уровень (до 100%), не чаще triggerLockout. Применённое Спуском дальше не спускает.
        /// Снаряд — выстрел в ближайшего врага, рывок — тень-рывок к нему, зона — зона (вдвое короче), призыв — эхо-дух на 4 с,
        /// барьер — стена-копия на 2 с, держит снаряды.</summary>
        void FireTrigger(FormSlot s, SkillCard card, Vector2 at, Vector2 dir, Enemy except = null)
        {
            int lvl = card.Level(Trait.Trigger);
            if (lvl <= 0 || s == null || s.TriggerLock > 0f) return;
            int i = System.Array.IndexOf(Forms, s);
            if (i < 0) return;
            FormSlot next = null;
            for (int k = 1; k < Forms.Length; k++)
            {
                var o = Forms[(i + k) % Forms.Length];
                if (o.Card != null) { next = o; break; }
            }
            if (next == null) return;
            var c = FormCfg;
            s.TriggerLock = c.triggerLockout;
            float mul = Mathf.Min(1f, c.triggerFraction * lvl);
            var pc = next.Card;
            var target = NearestEnemy(at, 8f, except);
            var aim = target != null ? (target.Pos - at).normalized : dir.sqrMagnitude > 0.0001f ? dir.normalized : Player.Face;
            switch (pc.Form)
            {
                case FormKind.Shot:
                {
                    var f = SpawnShot(next, pc, at, aim, mul, true);
                    if (f != null) { f.Payload = true; if (except != null) f.Hit.Add(except); }
                    break;
                }
                case FormKind.Dash:
                    Add(new Effect { Kind = EffectKind.FormShade, Pos = at, To = TravelTarget(at, aim, c.dashRange), Life = c.dashTime, Card = pc, FSlot = next, As = FormKind.Dash, Mul = mul, Echoed = true, Payload = true });
                    break;
                case FormKind.Zone:
                {
                    var z = SpawnZone(next, pc, at, mul, true, 0);
                    z.Payload = true;
                    next.Active.Remove(z); // перезарядку своей зоны не трогает
                    break;
                }
                case FormKind.Summon:
                {
                    var f = SpawnSpirit(next, pc, 1f, mul, true);
                    f.Pos = at; f.Payload = true;
                    next.Active.Remove(f);
                    break;
                }
                default:
                    next.Active.Add(Add(new Effect { Kind = EffectKind.FormWall, Pos = at, Life = 2f, R = c.barrierRadius * 1.3f, Card = pc, FSlot = next, Payload = true }));
                    break;
            }
            Events.Add(new SimEvent { Kind = EventKind.Zap, Pos = at, To = at + aim * 0.8f, Color = 1 });
        }

        // ---------- активация зоны, призыва, барьера ----------

        /// <summary>Зона, призыв, барьер: k = 1 — усиление (отпущено в окне заряда), 0 — обычное применение.</summary>
        void ActivateForm(FormSlot s, SimInput input, float k = 0f, float pm = 1f)
        {
            var c = FormCfg;
            var card = s.Card;
            var p = Player;
            switch (card.Form)
            {
                case FormKind.Zone:
                {
                    var at = ZoneTarget(input);
                    float wide = Mathf.Sqrt(1f + c.zoneChargeArea * k); // площадь ×(1 + k)
                    SpawnZone(s, card, at, pm, false, card.Level(Trait.Ricochet), wide);
                    if (card.Has(Trait.Split))
                    {
                        var mirror = p.Pos - (at - p.Pos);
                        CollideCircle(ref mirror, 0.3f);
                        SpawnZone(s, card, mirror, pm, false, card.Level(Trait.Ricochet), wide);
                    }
                    s.Cd = s.CdTotal = TrinketIII(TrinketKind.Compass) ? c.zoneCooldown + 0.3f : c.zoneLife + c.zoneCooldown;
                    break;
                }
                case FormKind.Summon:
                {
                    int alive = 0;
                    foreach (var f in s.Active) if (f.Kind == EffectKind.FormSpirit && f.Phase != 3) alive++;
                    if (alive >= 2 && card.Has(Trait.Reap)) foreach (var f in s.Active) { if (f.Kind == EffectKind.FormSpirit) { f.Life = 0f; break; } }
                    float dmg = pm, life = 1f + c.summonChargeLife * k;
                    if (card.Has(Trait.Split))
                    {
                        SpawnSpirit(s, card, -1f, c.summonSplit * dmg, false).Life *= life;
                        SpawnSpirit(s, card, 1f, c.summonSplit * dmg, false).Life *= life;
                    }
                    else SpawnSpirit(s, card, alive > 0 ? 1f : -1f, dmg, false).Life *= life;
                    s.Cd = s.CdTotal = c.summonCooldown;
                    break;
                }
                case FormKind.Barrier:
                {
                    float life = c.barrierLife;
                    var b = SpawnBarrier(s, card, life, false);
                    b.Charges += Mathf.RoundToInt(c.barrierChargeHits * k);
                    s.Cd = s.CdTotal = life + c.barrierCooldown;
                    break;
                }
                    break;
            }
            Events.Add(new SimEvent { Kind = EventKind.CardCast, Pos = p.Pos, To = p.Face, Value = System.Array.IndexOf(Forms, s), Color = 1 });
        }

        /// <summary>Точка зоны: мышь — курсор (не дальше zoneCastRange), как каст Hades с AutoLockWithMouse = false;
        /// геймпад — ближайший враг в радиусе и ±75° от стика (Aim Assist), иначе 3.5 перед героем.</summary>
        Vector2 ZoneTarget(SimInput input)
        {
            var p = Player;
            var c = FormCfg;
            Vector2 at;
            if (input.UseMouse && input.HasCursor)
            {
                var d = input.Cursor - p.Pos;
                at = p.Pos + Vector2.ClampMagnitude(d, c.zoneCastRange);
            }
            else
            {
                var dir = AimAssist(c.zoneLockRange, c.zoneLockArc, false, input);
                at = p.Lock != null ? p.Lock.Pos : p.Pos + dir * 3.5f;
                p.Face = dir;
            }
            Vector2 mn = RoomMin, mx = RoomMax;
            at.x = Mathf.Clamp(at.x, mn.x + 0.5f, mx.x - 0.5f);
            at.y = Mathf.Clamp(at.y, mn.y + 0.5f, mx.y - 0.5f);
            return at;
        }

        Effect SpawnZone(FormSlot s, SkillCard card, Vector2 at, float mul, bool echoed, int bounces, float wide = 1f)
        {
            var c = FormCfg;
            var p = Player;
            if (!card.Has(Trait.Orbit)) mul *= SnipeMul(card.Level(Trait.Snipe), (at - p.Pos).magnitude); // Дальнобой: чем дальше, тем сильнее
            var z = new Effect
            {
                Kind = EffectKind.FormZone, Pos = at, R = c.zoneRadius * wide, Life = c.zoneLife * (echoed ? 0.5f : 1f), Tick = 0f,
                Card = card, FSlot = s, As = FormKind.Zone, Mul = mul, Echoed = echoed, Bounces = bounces,
            };
            int orbit = card.Level(Trait.Orbit);
            if (orbit > 0 && !card.Has(Trait.Pierce)) { z.Pos = p.Pos; z.R *= 1f + 0.2f * (orbit - 1); } // аура: всегда вокруг Смерти
            if (card.Has(Trait.Pierce))
            {
                // разлом: полоса от героя до точки
                z.Phase = 1; z.Start = p.Pos; z.To = at;
                var d = at - p.Pos;
                z.Dir = d.sqrMagnitude > 0.01f ? d.normalized : p.Face;
            }
            Add(z);
            z.Start = z.Phase == 1 ? p.Pos : at;
            s.Active.Add(z);
            int push = card.Level(Trait.Push);
            if (push > 0)
            {
                foreach (var e in Enemies)
                    if (e.Alive && ZoneContains(z, e)) Shove(e, e.Pos - z.Pos, c.pushSpeed * (1f + 0.3f * (push - 1)), c.pushCollide * mul * Icebreak(card, e));
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = z.Pos, Radius = z.R, Color = 1 });
            }
            return z;
        }

        Effect SpawnSpirit(FormSlot s, SkillCard card, float side, float mul, bool echo)
        {
            var c = FormCfg;
            var p = Player;
            var f = Add(new Effect
            {
                Kind = EffectKind.FormSpirit, Pos = p.Pos + Rotate(-p.Face, side * 0.9f) * c.summonOffset, Life = echo ? 4f : c.summonLife,
                Card = card, FSlot = s, As = FormKind.Summon, Mul = mul, Echoed = echo, Back = side, T = 0.3f, Angle = side * 90f,
            });
            s.Active.Add(f);
            return f;
        }

        Effect SpawnBarrier(FormSlot s, SkillCard card, float life, bool echoed)
        {
            var c = FormCfg;
            var p = Player;
            var b = Add(new Effect
            {
                Kind = EffectKind.FormBarrier, Pos = p.Pos, Life = life, R = c.barrierRadius, Charges = c.barrierHits,
                Card = card, FSlot = s, As = FormKind.Barrier, Echoed = echoed,
            });
            s.Active.Add(b);
            if (echoed) return b;
            int pierce = card.Level(Trait.Pierce);
            if (pierce > 0)
            {
                foreach (var e in Enemies) if (e.Alive && (e.Pos - p.Pos).magnitude <= 2.2f + e.R) FormStrike(e, c.dashPierceDamage * pierce, 0.3f, card, s, FormKind.Barrier, p.Pos, e.Pos - p.Pos, 1f, b);
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = p.Pos, Radius = 2.2f, Color = 2 });
            }
            int push = card.Level(Trait.Push);
            if (push > 0)
            {
                foreach (var e in Enemies) if (e.Alive && (e.Pos - p.Pos).magnitude <= 2.5f + e.R) Shove(e, e.Pos - p.Pos, c.pushSpeed * (1f + 0.3f * (push - 1)), c.pushCollide * Icebreak(card, e));
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = p.Pos, Radius = 2.5f, Color = 1 });
            }
            int pull = card.Level(Trait.Pull);
            if (pull > 0)
            {
                foreach (var e in Enemies)
                    if (e.Alive && (e.Pos - p.Pos).magnitude <= 4f + e.R) { e.Pull = 0.35f; e.PullTo = p.Pos; e.PullSpeed = 6f * pull; }
                foreach (var f in Effects)
                    if (f.Kind == EffectKind.EnemyBolt && !f.Done && (f.Pos - p.Pos).magnitude < 5f) f.Dir = (p.Pos - f.Pos).normalized;
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = p.Pos, Radius = 4f, Color = 5 });
            }
            if (card.Has(Trait.Split))
                s.Active.Add(Add(new Effect { Kind = EffectKind.FormWall, Pos = p.Pos, Life = life, R = c.barrierRadius * 1.3f, Card = card, FSlot = s }));
            int orbit = card.Level(Trait.Orbit);
            if (orbit > 0) SpawnShards(s, card, FormKind.Barrier, 2 * orbit, life);
            return b;
        }

        // ---------- рывок ----------

        /// <summary>Рывок с зарядом k: дальность ×(1 + k), урон задетым ×mul.</summary>
        void StartFormDash(FormSlot s, SimInput input, float k = 0f, float mul = 1f)
        {
            var p = Player;
            var dir = input.Move.sqrMagnitude > 0.0001f ? input.Move.normalized : p.Face;
            if (input.UseMouse && input.HasCursor && PlayerCfg.dashAtCursor > 0f && (input.Cursor - p.Pos).sqrMagnitude > 0.01f) dir = (input.Cursor - p.Pos).normalized;
            int homing = s.Card.Level(Trait.Homing);
            if (homing > 0 && NearestInArc(p.Pos, dir, FormCfg.dashRange * 1.5f, FormCfg.homingDashArc + 15f * (homing - 1)) is Enemy t)
                dir = (t.Pos - p.Pos).normalized; // Наведение: рывок доворачивает к врагу впереди
            BeginDash(s, dir, false, null, 1f + FormCfg.dashChargeRange * k);
            fDashMul = mul;
            fDashLeap |= k > 0f;
            var card = s.Card;
            var c = FormCfg;
            if (card.Has(Trait.Split))
            {
                var to = TravelTarget(p.Pos, -dir, fDashRange);
                Add(new Effect { Kind = EffectKind.FormShade, Pos = p.Pos, Start = p.Pos, To = to, Life = c.dashTime, Card = card, FSlot = s, As = FormKind.Dash, Mul = 1f });
            }
            int echo = card.Level(Trait.Echo);
            if (echo > 0)
                Add(new Effect { Kind = EffectKind.FormShade, Pos = fDashFrom, Start = fDashFrom, To = fDashTo, T = c.echoDelay, Life = c.dashTime, Card = card, FSlot = s, As = FormKind.Dash, Mul = Mathf.Min(1f, c.echoFraction * echo), Echoed = true });
            if (card.Has(Trait.Return))
                Add(new Effect { Kind = EffectKind.FormDashReturn, Pos = fDashFrom, T = c.returnDelay, Card = card, FSlot = s });
        }

        void BeginDash(FormSlot s, Vector2 dir, bool back, Vector2? target = null, float reach = 1f)
        {
            var c = FormCfg;
            var p = Player;
            var card = s.Card;
            fDashRange = c.dashRange * (card.Has(Trait.Pierce) ? c.dashPierceRange : 1f) * reach;
            fDashFrom = p.Pos;
            fDashTo = target ?? TravelTarget(p.Pos, dir, fDashRange);
            fDashDir = dir;
            fDashWall = !target.HasValue && (fDashTo - fDashFrom).magnitude < fDashRange - 0.15f;
            // кончается в лаве — тянется до берега, не дальше ×dashLavaReach; над лавой — прыжок
            if (!target.HasValue && OnLava(fDashTo) && !fDashWall)
            {
                for (float d = fDashRange + 0.1f; d <= fDashRange * c.dashLavaReach; d += 0.1f)
                {
                    var to = TravelTarget(fDashFrom, dir, d);
                    if ((to - fDashFrom).magnitude < d - 0.15f) break; // упёрся в стену
                    if (!OnLava(to)) { fDashTo = to; break; }
                }
            }
            fDashLeap = OverLava(fDashFrom, fDashTo);
            fDashMul = 1f;
            fDashT = 0f;
            // рывок дальше обычного — та же скорость, дольше
            fDashDur = c.dashTime * (back ? 0.75f : 1f) * Mathf.Max(1f, (fDashTo - fDashFrom).magnitude / c.dashRange);
            fDashSlot = s; fDashReturn = back; fDashTouched = false; fDashKilled = false;
            fDashing = true;
            fDashHit.Clear();
            p.Face = dir;
            p.DashInvuln = Mathf.Max(p.DashInvuln, fDashDur + c.dashInvuln);
            if (!back) s.Cd = s.CdTotal = 99f; // перезарядка — с конца рывка; откат назад её не трогает
            Events.Add(new SimEvent { Kind = EventKind.CardCast, Pos = p.Pos, To = dir, Value = DashSlot, Color = 2 });
        }

        void UpdateFormDash(float dt)
        {
            var c = FormCfg;
            var p = Player;
            var s = fDashSlot;
            var card = s.Card;
            fDashT += dt;
            float k = Mathf.Clamp01(fDashT / fDashDur);
            p.Pos = Vector2.Lerp(fDashFrom, fDashTo, k);
            HitRoomObjects(p.Pos, c.dashTouch); // рывок сквозь урну разбивает её, задетый куб загорается
            int grow = card.Level(Trait.Grow);
            foreach (var e in Enemies)
            {
                if (!e.Alive || fDashHit.Contains(e) || (e.Pos - p.Pos).magnitude > c.dashTouch + e.R) continue;
                fDashHit.Add(e);
                fDashTouched = true;
                if (DashTouch(e, card, s, p.Pos, fDashDir, fDashMul, null, (p.Pos - fDashFrom).magnitude)) fDashKilled = true;
                if (grow > 0 && !fDashReturn)
                {
                    // Рост: каждый задетый враг удлиняет рывок
                    float extra = fDashRange * c.growDashRange * grow;
                    var to = TravelTarget(fDashFrom, fDashDir, (fDashTo - fDashFrom).magnitude + extra);
                    float left = (fDashTo - fDashFrom).magnitude;
                    fDashDur *= Mathf.Max(1f, (to - fDashFrom).magnitude / Mathf.Max(left, 0.01f));
                    fDashTo = to;
                }
            }
            if (k < 1f) return;
            fDashing = false;
            DashEndHooks(card, s, fDashFrom, fDashTo, fDashMul);
            if (fDashReturn) return;
            int orbit = card.Level(Trait.Orbit);
            if (orbit > 0) SpawnShards(s, card, FormKind.Dash, 1 + orbit, c.orbitDashTime);
            SnipeBolt(card, s, FormKind.Dash, fDashTo, c.shotDamage * 0.5f);
            if (TrinketIII(TrinketKind.Feather))
            {
                // Перо III: конец рывка оставляет зону-след со свойствами рывка
                var z = SpawnZone(s, card, fDashTo, 1f, true, 0);
                z.Life = TrinketCfg.featherZoneLife; z.Payload = true;
                s.Active.Remove(z);
            }
            FireTrigger(s, card, fDashTo, fDashDir);
            s.Cd = s.CdTotal = c.dashCooldown;
            // Рикошет: упёрся в стену или задел врага — второй рывок бесплатно (один раз подряд)
            if (card.Has(Trait.Ricochet) && !s.FreeDash && (fDashWall || fDashTouched)) { s.Cd = 0f; s.FreeDash = true; }
            else s.FreeDash = false;
            if (fDashKilled && card.Has(Trait.Reap)) s.Cd = 0f;
        }

        /// <summary>Касание рывка (героя или тени): урон — с Пронзанием и Разгоном (по пройденному пути traveled), остальное —
        /// свойства; Поглощение лечит за каждого задетого. true — враг убит.</summary>
        bool DashTouch(Enemy e, SkillCard card, FormSlot s, Vector2 from, Vector2 dir, float mul, Effect src, float traveled)
        {
            var c = FormCfg;
            int pierce = card.Level(Trait.Pierce), kinetic = card.Level(Trait.Kinetic);
            float dmg = (pierce > 0 ? c.dashPierceDamage * pierce : 0f) + (kinetic > 0 ? c.kineticRam * kinetic * traveled : 0f);
            FormStrike(e, dmg * mul, pierce > 0 || kinetic > 0 ? c.dashPierceStun : 0f, card, s, FormKind.Dash, from, e.Pos - from, mul, src);
            HealPlayer(c.siphonDash * card.Level(Trait.Siphon));
            return e.Dead;
        }

        // ---------- Разгон, Дальнобой, Поглощение ----------

        /// <summary>Доля скорости бега Смерти, 0…1 (в рывке — 1).</summary>
        float RunFactor => fDashing ? 1f : Mathf.Clamp01(Player.Vel.magnitude / Mathf.Max(0.01f, PlayerCfg.speed));

        /// <summary>Разгон зоны и духа: пока Смерть бежит, до +kineticMove за уровень.</summary>
        float KineticMul(SkillCard card) => 1f + FormCfg.kineticMove * card.Level(Trait.Kinetic) * RunFactor;

        /// <summary>Дальнобой: +snipePerUnit за ю расстояния за уровень, не дальше snipeMaxDist.</summary>
        float SnipeMul(int lvl, float dist) => lvl > 0 ? 1f + FormCfg.snipePerUnit * lvl * Mathf.Min(dist, FormCfg.snipeMaxDist) : 1f;

        Enemy FarthestEnemy(Vector2 from, float range)
        {
            Enemy best = null;
            float bd = -1f;
            foreach (var e in Enemies)
            {
                if (!e.Alive) continue;
                float d = (e.Pos - from).magnitude;
                if (d <= range && d > bd) { bd = d; best = e; }
            }
            return best;
        }

        /// <summary>Дальнобой рывка и барьера: выстрел в самого дальнего врага в snipeRange, догоняет цель, урон растёт с расстоянием.</summary>
        void SnipeBolt(SkillCard card, FormSlot s, FormKind form, Vector2 from, float dmg)
        {
            int lvl = card.Level(Trait.Snipe);
            if (lvl <= 0) return;
            var c = FormCfg;
            var t = FarthestEnemy(from, c.snipeRange);
            if (t == null) return;
            var d = t.Pos - from;
            float dist = d.magnitude;
            var dir = dist > 0.01f ? d / dist : Player.Face;
            Add(new Effect
            {
                Kind = EffectKind.FormBolt, Pos = from + dir * 0.5f, Dir = dir, Speed = c.shotSpeed, Life = dist / c.shotSpeed + 0.6f, R = 0.22f,
                Card = card, FSlot = s, As = form, Damage = dmg * SnipeMul(lvl, dist), Target = t, Phase = 2,
            });
        }

        /// <summary>Поглощение: лечит долю здоровья Смерти, но до следующей награды — не больше siphonRoomCap.</summary>
        void HealPlayer(float frac)
        {
            var p = Player;
            if (frac <= 0f || p.Hp <= 0f || GameOver) return;
            float amount = Mathf.Min(frac * PlayerCfg.hp, Mathf.Min(healLeft, PlayerCfg.hp - p.Hp));
            if (amount <= 0f) return;
            p.Hp += amount;
            healLeft -= amount;
            Events.Add(new SimEvent { Kind = EventKind.PlayerHealed, Pos = p.Pos, Value = amount });
        }

        /// <summary>Конец рывка (героя или тени): огненный и ледяной след, разряд по линии, притяжение к финишу, взрыв.</summary>
        void DashEndHooks(SkillCard card, FormSlot s, Vector2 from, Vector2 to, float mul)
        {
            var c = FormCfg;
            int fire = card.Level(Trait.Fire), frost = card.Level(Trait.Frost);
            if (fire > 0) AddTrail(card, s, from, to, 0, fire * mul);
            if (frost > 0) AddTrail(card, s, from, to, 1, frost * mul);
            int lightning = card.Level(Trait.Lightning);
            if (lightning > 0)
            {
                foreach (var e in Enemies)
                    if (e.Alive && DistToSegment(e.Pos, from, to) <= 0.5f + e.R) LightningStrike(e, c.chainMin * 2f * lightning * mul, 0.2f, card, s, FormKind.Dash, from, to - from, mul, null);
                Events.Add(new SimEvent { Kind = EventKind.Zap, Pos = from, To = to });
            }
            int pull = card.Level(Trait.Pull);
            if (pull > 0)
            {
                foreach (var e in Enemies)
                    if (e.Alive && DistToSegment(e.Pos, from, to) <= 2.5f + e.R) { e.Pull = 0.5f; e.PullTo = to; e.PullSpeed = 5f * pull; }
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = to, Radius = 2.5f, Color = 5 });
            }
            int blast = card.Level(Trait.Blast);
            if (blast > 0) FormBlast(card, s, FormKind.Dash, to, c.blastRadius, c.blastDamage * blast * mul, null);
        }

        void AddTrail(SkillCard card, FormSlot s, Vector2 from, Vector2 to, int kind, float mul)
        {
            var f = Add(new Effect { Kind = EffectKind.FormTrail, Life = 3f, R = 0.6f, Phase = kind, Mul = mul, Card = card, FSlot = s });
            f.Points.Add(from); f.Points.Add(to);
        }

        // ---------- общий удар формы ----------

        /// <summary>Удар формы по врагу: урон, оглушение, свойства-статусы по правилам формы (as). secondary — вторичный удар
        /// (молния, взрыв): статусы не накладывает и молнию дальше не передаёт.</summary>
        void FormStrike(Enemy e, float dmg, float stun, SkillCard card, FormSlot s, FormKind form, Vector2 from, Vector2 dir, float mul, Effect src, bool secondary = false)
        {
            if (!e.Alive) return;
            var c = FormCfg;
            if (!secondary) dmg *= TrinketBook.SoulDamageMul(card, TrinketSlots, TrinketCfg) * TrinketBook.DamageMul(form, TrinketSlots, TrinketCfg); // Сосуд душ и проклятия; молния берёт уже усиленный
            if (dmg > 0f) Damage(e, dmg, false); else e.Flash = 0.08f;
            if (e.Dead) { OnFormKill(card, s, form, src, e); return; }
            if (stun > 0f) ApplyCc(e, CcKind.Stun, stun);
            if (secondary) return;
            int lvl;
            if ((lvl = card.Level(Trait.Fire)) > 0 && form is FormKind.Shot or FormKind.Zone or FormKind.Summon) AddBurn(e, c.burn * lvl * mul);
            if ((lvl = card.Level(Trait.Frost)) > 0 && form is FormKind.Shot or FormKind.Zone) AddFrost(e, lvl);
            if ((lvl = card.Level(Trait.Rot)) > 0 && form != FormKind.Barrier) AddRot(e, lvl);
            if ((lvl = card.Level(Trait.Doom)) > 0 && form is FormKind.Shot or FormKind.Dash or FormKind.Summon) AddDoom(e, lvl, form == FormKind.Summon ? src?.Parent ?? src : null, card);
            if ((lvl = card.Level(Trait.Push)) > 0 && form is FormKind.Shot or FormKind.Dash or FormKind.Summon)
                Shove(e, form == FormKind.Dash ? e.Pos - Player.Pos : dir, c.pushSpeed * (1f + 0.3f * (lvl - 1)), c.pushCollide * mul * Icebreak(card, e));
            if (Bonds.Has(card, Bond.ThermalShock) && e.Frozen > 0f && e.Burn > 0f)
            {
                // Тепловой удар: лёд раскалывается — весь запас горения сразу
                float burn = e.Burn;
                e.Burn = 0f;
                Damage(e, burn, false, true, DotKind.Burn);
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = e.Pos, Radius = 1f, Color = 3 });
                if (e.Dead) { OnFormKill(card, s, form, src, e); return; }
            }
            if ((lvl = card.Level(Trait.Lightning)) > 0 && form == FormKind.Shot)
                Chain(e, Mathf.Max(c.chainMin, dmg * (TrinketIII(TrinketKind.LightningRod) ? 1f : c.chainFraction)), c.chainTargets * lvl, card, s, form, src); // Громоотвод III: без потери
        }

        /// <summary>Ледокол (Иней + Отталкивание): заиндевевший или замёрзший бьётся об стену или врага сильнее.</summary>
        float Icebreak(SkillCard card, Enemy e) => Bonds.Has(card, Bond.Icebreaker) && (e.Frost > 0 || e.Frozen > 0f) ? FormCfg.bondIcebreaker : 1f;

        /// <summary>Вторичный удар молнии карты. Сверхпроводник (Иней + Молния) — ×2 по заиндевевшему или замёрзшему,
        /// Зараза (Молния + Гниль) — молния заражает.</summary>
        void LightningStrike(Enemy e, float dmg, float stun, SkillCard card, FormSlot s, FormKind form, Vector2 from, Vector2 dir, float mul, Effect src)
        {
            if (Bonds.Has(card, Bond.Superconductor) && (e.Frost > 0 || e.Frozen > 0f)) dmg *= FormCfg.bondSuperconductor;
            FormStrike(e, dmg, stun, card, s, form, from, dir, mul, src, true);
            if (e.Alive && Bonds.Has(card, Bond.Contagion)) AddRot(e, card.Level(Trait.Rot));
        }

        /// <summary>Добивание формой: Жатва и Рост духа.</summary>
        void OnFormKill(SkillCard card, FormSlot s, FormKind form, Effect src, Enemy victim = null)
        {
            var c = FormCfg;
            // Цепная детонация (Жатва + Взрыв): добитый взрывается чуть позже — так цепочка идёт волной
            if (victim != null && Bonds.Has(card, Bond.ChainDetonation))
                Add(new Effect { Kind = EffectKind.FormDetonation, Pos = victim.Pos, T = 0.12f, Card = card, FSlot = s, As = form });
            int siphon = card.Level(Trait.Siphon);
            if (siphon > 0)
            {
                if (form == FormKind.Shot) HealPlayer(c.siphonKill * siphon);
                else if (form == FormKind.Summon) HealPlayer(c.siphonSummon * siphon);
                if (victim != null && victim.Burn > 0f && Bonds.Has(card, Bond.Purification)) HealPlayer(c.bondPurification * siphon); // Очищение
            }
            if (form == FormKind.Summon && src != null)
            {
                var spirit = src.Kind == EffectKind.FormSpirit ? src : src.Parent;
                int grow = card.Level(Trait.Grow);
                if (spirit != null && grow > 0) spirit.Grow = Mathf.Min(c.growMax, spirit.Grow + c.growPerHit * grow);
            }
            int reap = card.Level(Trait.Reap);
            if (reap == 0 || s == null || s.ReapLock > 0f) return;
            s.ReapLock = c.reapLockout;
            switch (form)
            {
                case FormKind.Shot: s.Haste = c.reapHaste * reap; break;
                case FormKind.Zone: if (src != null && src.Kind == EffectKind.FormZone) src.Life += 1f * reap; break;
                case FormKind.Summon: s.Cd = 0f; break;
                // рывок — в конце рывка (UpdateFormDash), барьер — в Kill (любое добивание, пока барьер стоит)
            }
        }

        void FormBlast(SkillCard card, FormSlot s, FormKind form, Vector2 at, float radius, float dmg, Effect src)
        {
            dmg *= TrinketBook.SoulDamageMul(card, TrinketSlots, TrinketCfg) * TrinketBook.DamageMul(form, TrinketSlots, TrinketCfg); // Сосуд душ и проклятия
            var c = FormCfg;
            bool napalm = Bonds.Has(card, Bond.Napalm) || TrinketIII(TrinketKind.PowderHorn), implode = Bonds.Has(card, Bond.Implosion); // Пороховница III — как Напалм
            if (implode) radius *= c.bondImplosion;
            HitRoomObjects(at, radius);
            foreach (var e in Enemies)
            {
                if (!e.Alive || (e.Pos - at).magnitude > radius + e.R) continue;
                FormStrike(e, dmg, 0.2f, card, s, form, at, e.Pos - at, 1f, src, true);
                if (!e.Alive) continue;
                if (napalm) AddBurn(e, c.burn * Mathf.Max(1, card.Level(Trait.Fire))); // Напалм: взрыв поджигает
                if (implode) { e.Pull = 0.3f; e.PullTo = at; e.PullSpeed = 8f; } // Схлопывание: выживших тянет к центру
            }
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = at, Radius = radius, Color = implode ? 5 : 3 });
        }

        /// <summary>Молния: от врага к ближайшему, ещё не задетому, и дальше — до count раз.</summary>
        void Chain(Enemy from, float dmg, int count, SkillCard card, FormSlot s, FormKind form, Effect src)
        {
            var c = FormCfg;
            var hit = new HashSet<Enemy> { from };
            var cur = from.Pos;
            for (int k = 0; k < count; k++)
            {
                Enemy best = null;
                float bd = c.chainRange;
                foreach (var e in Enemies)
                {
                    if (!e.Alive || hit.Contains(e)) continue;
                    float d = (e.Pos - cur).magnitude - e.R;
                    if (d < bd) { bd = d; best = e; }
                }
                if (best == null) break;
                hit.Add(best);
                Events.Add(new SimEvent { Kind = EventKind.Zap, Pos = cur, To = best.Pos });
                cur = best.Pos;
                LightningStrike(best, dmg, 0.15f, card, s, form, cur, best.Pos - cur, 1f, src);
            }
        }

        // ---------- статусы ----------

        /// <summary>Scorch Гестии: запас урона копится и выгорает тиками раз в 1/6 с (DamageInterval) за burnTime.</summary>
        void AddBurn(Enemy e, float amount)
        {
            e.Burn += amount;
            e.BurnT = FormCfg.burnTime;
        }

        void AddFrost(Enemy e, int stacks)
        {
            var c = FormCfg;
            e.Frost += stacks;
            e.FrostT = c.frostTime;
            if (e.Frost < c.frostToFreeze - (TrinketIII(TrinketKind.IceShard) ? 1 : 0)) return; // Осколок льда III: с 2 стаков
            e.Frost = 0;
            e.Frozen = c.freezeTime;
            ApplyCc(e, CcKind.Stun, c.freezeTime);
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = e.Pos, Radius = 0.8f, Color = 2 });
        }

        void AddRot(Enemy e, int lvl)
        {
            e.RotT = FormCfg.rotTime;
            e.RotLvl = Mathf.Max(e.RotLvl, lvl);
        }

        void AddDoom(Enemy e, int lvl, Effect spirit = null, SkillCard card = null)
        {
            if (e.DoomT > 0f) return; // уже приговорён — новая метка не ставится
            e.DoomT = FormCfg.doomTime;
            e.DoomAcc = 0f;
            e.DoomFrac = FormCfg.doomFraction * lvl;
            e.DoomBy = spirit;
            e.DoomCard = card;
        }

        void DoomBurst(Enemy e)
        {
            e.DoomT = 0f;
            e.DoomBy = null;
            float dmg = e.DoomAcc * e.DoomFrac;
            e.DoomAcc = 0f;
            var card = e.DoomCard;
            e.DoomCard = null;
            if (dmg <= 0f || e.Dead) return;
            // Двойной приговор (Приговор + Эхо): через echoDelay ещё раз, долей первого
            if (Bonds.Has(card, Bond.DoubleDoom)) { e.DoomEchoT = FormCfg.echoDelay; e.DoomEchoDmg = dmg * FormCfg.bondDoubleDoom; }
            doomBursting = true;
            Damage(e, dmg, true, true, DotKind.Hex);
            doomBursting = false;
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = e.Pos, Radius = 0.9f, Color = 5 });
        }

        /// <summary>Отброс свойства: удар о стену или другого врага наносит урон (UpdateEnemy).</summary>
        void Shove(Enemy e, Vector2 dir, float speed, float collide)
        {
            if (!e.Alive) return;
            e.KnockVel = (dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.up) * speed;
            e.KnockT = FormCfg.pushTime;
            e.KnockDmg = collide;
            InterruptAttack(e);
        }

        /// <summary>Тики статусов форм в UpdateEnemy. Возвращает множитель скорости ходьбы (иней, зона).</summary>
        float UpdateFormStatuses(Enemy e, float dt)
        {
            var c = FormCfg;
            if (e.Burn > 0f)
            {
                e.BurnTick -= dt;
                e.BurnT -= dt;
                if (e.BurnTick <= 0f)
                {
                    e.BurnTick += 1f / 6f;
                    float d = e.BurnT <= 0f ? e.Burn : e.Burn * Mathf.Clamp01((1f / 6f) / Mathf.Max(e.BurnT, 1f / 6f));
                    e.Burn -= d;
                    if (e.Burn < 0.5f) e.Burn = 0f;
                    Damage(e, d, false, true, DotKind.Burn);
                    if (e.Dead) return 1f;
                }
            }
            else e.BurnTick = 0f;
            e.FrostT -= dt; if (e.FrostT <= 0f) e.Frost = 0;
            e.Frozen -= dt; e.RotT -= dt; e.LinkT -= dt; e.ZoneSlowT -= dt;
            if (e.RotT > 0f && TrinketIII(TrinketKind.RotBone))
            {
                // Гнилая кость III: заражённые получают урон гнили
                e.RotTick -= dt;
                if (e.RotTick <= 0f)
                {
                    e.RotTick += 0.5f;
                    Damage(e, TrinketCfg.rotBoneDps * 0.5f, false, true, DotKind.Poison);
                    if (e.Dead) return 1f;
                }
            }
            if (e.DoomT > 0f) { e.DoomT -= dt; if (e.DoomT <= 0f) DoomBurst(e); }
            if (e.DoomEchoT > 0f && (e.DoomEchoT -= dt) <= 0f && e.DoomEchoDmg > 0f && !e.Dead)
            {
                float d = e.DoomEchoDmg;
                e.DoomEchoDmg = 0f;
                doomBursting = true;
                Damage(e, d, true, true, DotKind.Hex);
                doomBursting = false;
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = e.Pos, Radius = 0.9f, Color = 5 });
                if (e.Dead) return 1f;
            }
            float mul = 1f;
            if (e.Frost > 0) mul *= 1f - c.frostSlowPerStack * e.Frost;
            if (e.ZoneSlowT > 0f) mul *= c.zoneSlow;
            return mul;
        }

        /// <summary>Hitch Геры: часть урона по связанному врагу получают остальные связанные (не дальше Range 1200).</summary>
        void ShareDamage(Enemy src, float amount)
        {
            sharing = true;
            float range = 1200f * HadesScale.Unit;
            foreach (var o in Enemies)
                if (o != src && o.Alive && o.LinkT > 0f && (o.Pos - src.Pos).magnitude <= range)
                {
                    Damage(o, amount * FormCfg.linkShare, false, true, DotKind.Light);
                    Events.Add(new SimEvent { Kind = EventKind.Zap, Pos = src.Pos, To = o.Pos, Color = 1 });
                }
            sharing = false;
        }

        void SpreadRot(Enemy dead)
        {
            var c = FormCfg;
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = dead.Pos, Radius = c.rotSpread, Color = 4 });
            foreach (var o in Enemies)
                if (o != dead && o.Alive && (o.Pos - dead.Pos).magnitude <= c.rotSpread) AddRot(o, dead.RotLvl);
        }

        /// <summary>Добивание любым ударом, пока стоит барьер с Жатвой: +1 прочность.</summary>
        void OnKillForms(Enemy dead)
        {
            DropGrace(dead);
            if (dead.Burn > 0f && TrinketIII(TrinketKind.Ember))
            {
                // Уголёк III: умерший с горением поджигает соседей
                float r = TrinketCfg.ember3Radius;
                foreach (var o in Enemies) if (o != dead && o.Alive && (o.Pos - dead.Pos).magnitude <= r + o.R) AddBurn(o, FormCfg.burn);
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = dead.Pos, Radius = r, Color = 3 });
            }
            if (TrinketIII(TrinketKind.TimeSand))
                foreach (var s in Forms) if (s.Cd > 0f && s.Cd < 90f) s.Cd = Mathf.Max(0f, s.Cd - TrinketCfg.timeSandKill); // Песок времени III
            foreach (var s in Forms)
            {
                if (s.Card == null || s.Card.Form != FormKind.Barrier) continue;
                int reap = s.Card.Level(Trait.Reap);
                if (reap == 0) continue;
                foreach (var f in s.Active)
                    if (f.Kind == EffectKind.FormBarrier && !f.Done) f.Charges = Mathf.Min(FormCfg.barrierHits + 2 * reap, f.Charges + reap);
            }
        }

        // ---------- барьер: поглощение ----------

        Effect ActiveBarrier()
        {
            foreach (var s in Forms)
                foreach (var f in s.Active)
                    if (f.Kind == EffectKind.FormBarrier && !f.Done) return f;
            return null;
        }

        /// <summary>Удар врага по герою за барьером: поглощён. attacker — ударивший (для свойств), bolt — снаряд.</summary>
        bool BarrierAbsorb(Vector2 from, Effect bolt)
        {
            var b = ActiveBarrier();
            if (b == null) return false;
            var c = FormCfg;
            var card = b.Card;
            var s = b.FSlot;
            Enemy attacker = null;
            foreach (var e in Enemies) if (e.Alive && (e.Pos - from).sqrMagnitude < 0.01f) attacker = e;
            if (bolt != null) attacker = bolt.Target;
            b.Charges--;
            b.Mul += 1f; // сколько ударов поглощено — для Взрыва
            int lvl;
            if (attacker != null && attacker.Alive && (attacker.Pos - Player.Pos).magnitude < 3f)
            {
                if ((lvl = card.Level(Trait.Fire)) > 0) AddBurn(attacker, c.burn * lvl);
                if ((lvl = card.Level(Trait.Frost)) > 0) AddFrost(attacker, c.frostToFreeze);
                if ((lvl = card.Level(Trait.Rot)) > 0) AddRot(attacker, lvl);
                if ((lvl = card.Level(Trait.Doom)) > 0) AddDoom(attacker, lvl, null, card);
            }
            if ((lvl = card.Level(Trait.Lightning)) > 0)
            {
                var near = new List<Enemy>();
                foreach (var e in Enemies) if (e.Alive && (e.Pos - Player.Pos).magnitude <= 4f) near.Add(e);
                near.Sort((x, y) => (x.Pos - Player.Pos).sqrMagnitude.CompareTo((y.Pos - Player.Pos).sqrMagnitude));
                for (int k = 0; k < Mathf.Min(near.Count, c.chainTargets * lvl); k++)
                {
                    Events.Add(new SimEvent { Kind = EventKind.Zap, Pos = Player.Pos, To = near[k].Pos });
                    LightningStrike(near[k], c.chainMin * 3f * lvl, 0.2f, card, s, FormKind.Barrier, Player.Pos, near[k].Pos - Player.Pos, 1f, b);
                }
            }
            if ((lvl = card.Level(Trait.Grow)) > 0) { b.Life += c.growBarrierTime * lvl; b.Grow = Mathf.Min(c.growMax, b.Grow + 0.15f * lvl); }
            if (bolt != null)
            {
                // Рикошет, Гниль и Наведение: снаряд летит обратно во врагов (с гнилью и прочими статусами барьера); Возврат — позже.
                // С Наведением — догоняет цель (Phase 2)
                if (card.Has(Trait.Ricochet) || card.Has(Trait.Rot) || card.Has(Trait.Homing))
                {
                    var target = NearestEnemy(Player.Pos, 9f);
                    var dir = target != null ? (target.Pos - Player.Pos).normalized : -bolt.Dir;
                    Add(new Effect
                    {
                        Kind = EffectKind.FormBolt, Pos = Player.Pos + dir * 0.6f, Dir = dir, Speed = c.shotSpeed, Life = 1.2f, R = 0.22f,
                        Card = card, FSlot = s, As = FormKind.Shot, Damage = c.shotDamage * 0.5f * Mathf.Max(1, card.Level(Trait.Ricochet)), Target = target,
                        Phase = card.Has(Trait.Homing) && target != null ? 2 : 0,
                    });
                }
                if (card.Has(Trait.Return) && bolt.Target != null) b.Marks.Add(bolt.Target);
            }
            Events.Add(new SimEvent { Kind = EventKind.PlayerBlocked, Pos = Player.Pos });
            HealPlayer(c.siphonBarrier * card.Level(Trait.Siphon));
            SnipeBolt(card, s, FormKind.Barrier, Player.Pos, c.shotDamage * 0.5f);
            if (attacker != null && attacker.Alive && TrinketIII(TrinketKind.Ward))
            {
                // Щит-оберег III: поглощённый удар отражается в ударившего
                var d = (attacker.Pos - Player.Pos).normalized;
                Add(new Effect { Kind = EffectKind.FormBolt, Pos = Player.Pos + d * 0.6f, Dir = d, Speed = c.shotSpeed, Life = 2f, R = 0.22f, Card = card, FSlot = s, As = FormKind.Barrier, Damage = c.shotDamage, Target = attacker, Phase = 2 });
            }
            FireTrigger(s, card, Player.Pos, attacker != null ? attacker.Pos - Player.Pos : Player.Face);
            if (b.Charges <= 0) BarrierEnd(b);
            return true;
        }

        void BarrierEnd(Effect b)
        {
            if (b.Done) return;
            b.Done = true;
            var c = FormCfg;
            var card = b.Card;
            var s = b.FSlot;
            int blast = card.Level(Trait.Blast);
            if (blast > 0) FormBlast(card, s, FormKind.Barrier, Player.Pos, 2f, c.blastDamage * blast * (1f + 0.5f * (b.Mul - 1f)), b);
            // Возврат: поглощённые снаряды летят обратно в тех, кто стрелял
            foreach (var shooter in b.Marks)
            {
                if (shooter == null || !shooter.Alive) continue;
                var dir = (shooter.Pos - Player.Pos).normalized;
                Add(new Effect { Kind = EffectKind.FormBolt, Pos = Player.Pos + dir * 0.6f, Dir = dir, Speed = c.shotSpeed, Life = 2f, R = 0.22f, Card = card, FSlot = s, As = FormKind.Barrier, Damage = c.shotDamage, Target = shooter, Phase = 2 });
            }
            b.Marks.Clear();
            foreach (var w in s.Active) if (w.Kind == EffectKind.FormWall) w.Done = true;
            Events.Add(new SimEvent { Kind = EventKind.ShieldBreak, Pos = Player.Pos });
            if (card.Has(Trait.Echo) && !b.Echoed) SpawnBarrier(s, card, 1f, true);
        }

        /// <summary>Снаряд Casket у барьера или его копии-стены: поглощён до попадания в героя.</summary>
        bool CatchBolt(Effect bolt)
        {
            foreach (var s in Forms)
                foreach (var w in s.Active)
                {
                    if (w.Done) continue;
                    if (w.Kind == EffectKind.FormWall && (bolt.Pos - w.Pos).magnitude <= w.R + bolt.R) { bolt.Done = true; Events.Add(new SimEvent { Kind = EventKind.PlayerBlocked, Pos = bolt.Pos }); return true; }
                    if (w.Kind == EffectKind.FormBarrier && (bolt.Pos - Player.Pos).magnitude <= w.R * w.Grow + bolt.R)
                    {
                        bolt.Done = true;
                        BarrierAbsorb(bolt.Pos, bolt);
                        return true;
                    }
                }
            return false;
        }

        Enemy NearestEnemy(Vector2 from, float range, Enemy except = null, HashSet<Enemy> skip = null)
        {
            Enemy best = null;
            float bd = range;
            foreach (var e in Enemies)
            {
                if (!e.Alive || e == except || (skip != null && skip.Contains(e))) continue;
                float d = (e.Pos - from).magnitude;
                if (d < bd) { bd = d; best = e; }
            }
            return best;
        }

        // ---------- эффекты форм ----------

        void UpdateFormEffect(Effect f, float dt)
        {
            switch (f.Kind)
            {
                case EffectKind.FormShot: UpdateFormShot(f, dt); break;
                case EffectKind.FormBolt: UpdateFormBolt(f, dt); break;
                case EffectKind.FormZone: UpdateFormZone(f, dt); break;
                case EffectKind.FormSpirit: UpdateSpirit(f, dt); break;
                case EffectKind.FormBarrier: UpdateBarrier(f, dt); break;
                case EffectKind.FormTrail: UpdateFormTrail(f, dt); break;
                case EffectKind.FormShade: UpdateShade(f, dt); break;
                case EffectKind.FormVortex:
                    f.Life -= dt;
                    foreach (var e in Enemies)
                        if (e.Alive && (e.Pos - f.Pos).magnitude <= f.R + e.R) { e.Pull = 0.1f; e.PullTo = f.Pos; e.PullSpeed = FormCfg.pullSpeed * f.Mul; }
                    if (f.Life <= 0f) f.Done = true;
                    break;
                case EffectKind.FormEchoShot:
                    f.T -= dt;
                    if (f.T > 0f) break;
                    f.Done = true;
                    SpreadShots(f.FSlot, f.Card, f.Pos, f.Dir, f.Mul, true);
                    if (f.Gen == 0 && TrinketIII(TrinketKind.TuningFork)) // Камертон III: эхо повторяется
                        Add(new Effect { Kind = EffectKind.FormEchoShot, Pos = f.Pos, Dir = f.Dir, T = FormCfg.echoDelay, Card = f.Card, FSlot = f.FSlot, Mul = f.Mul * 0.5f, Gen = 1 });
                    break;
                case EffectKind.FormDashReturn:
                    f.T -= dt;
                    if (f.T > 0f) break;
                    f.Done = true;
                    // откат в точку старта: быстрый рывок назад с касаниями, без перезарядки
                    if (!fDashing && Player.Hidden <= 0f)
                    {
                        var d = f.Pos - Player.Pos;
                        if (d.magnitude > 0.3f) BeginDash(f.FSlot, d.normalized, true, f.Pos);
                    }
                    break;
                case EffectKind.FormWall:
                    f.Life -= dt;
                    if (f.Life <= 0f) f.Done = true;
                    break;
                case EffectKind.Grace: UpdateGraceOrb(f, dt); break;
                case EffectKind.EliteZone: UpdateEliteZone(f, dt); break;
                case EffectKind.EliteSpirit: UpdateEliteSpirit(f, dt); break;
                case EffectKind.EliteShield: UpdateEliteShield(f, dt); break;
                case EffectKind.MegaStrike: UpdateMegaStrike(f, dt); break;
                case EffectKind.Sardis: UpdateSardisOrb(f, dt); break;
                case EffectKind.Fountain: break; // стоит, пока этаж
                case EffectKind.FormDetonation:
                    f.T -= dt;
                    if (f.T > 0f) break;
                    f.Done = true;
                    FormBlast(f.Card, f.FSlot, f.As, f.Pos, FormCfg.blastRadius, FormCfg.blastDamage * f.Card.Level(Trait.Blast) * FormCfg.bondChainDetonation, null);
                    break;
            }
        }

        void UpdateFormShot(Effect f, float dt)
        {
            var c = FormCfg;
            var card = f.Card;
            if (f.Phase == 3) { UpdateOrbit(f, dt); return; }
            float step;
            if (f.Phase == 1)
            {
                // Возврат: летит к герою, задевая ещё раз
                var to = Player.Pos - f.Pos;
                float d = to.magnitude;
                f.Life -= dt;
                if (d < 0.5f || f.Life <= 0f) { f.Done = true; return; }
                f.Dir = to / d;
                step = c.shotSpeed * dt;
            }
            else
            {
                f.Speed = Mathf.Max(1.5f, f.Speed + c.shotAccel * dt);
                f.Life -= dt;
                step = f.Speed * dt;
                Steer(f, card.Level(Trait.Homing), dt);
                f.Length += step * (Bonds.Has(card, Bond.Hunter) ? 2f : 1f); // путь для Дальнобоя (Охотник — вдвое)
            }
            var prev = f.Pos;
            f.Pos += f.Dir * step;
            HitRoomObjects(f.Pos, f.R); // снаряд поджигает фитили взрывных кубов и разбивает урны по пути
            if (f.Phase == 0 && (!InsideRoom(f.Pos, 0f) || BlocksShot(f.Pos, f.R * 0.5f)))
            {
                if (f.Bounces > 0)
                {
                    f.Pos = prev;
                    f.Dir = BounceDir(f, prev, step);
                    f.Bounces--; f.Life += c.shotBounceFuse;
                    Bounced(f, null);
                }
                else { EndShot(f, prev); return; }
            }
            bool pierce = card.Has(Trait.Pierce) || f.Phase == 1;
            foreach (var e in Enemies)
            {
                if (!e.Alive || f.Hit.Contains(e) || (e.Pos - f.Pos).magnitude > f.R * f.Grow + e.R) continue;
                f.Hit.Add(e);
                ShotHit(f, e);
                if (pierce) continue;
                if (f.Bounces > 0)
                {
                    // рикошет от врага — к следующему ближайшему
                    var next = NearestEnemy(e.Pos, 5f, e, f.Hit);
                    f.Dir = next != null ? (next.Pos - f.Pos).normalized : Reflect(prev, f.Dir, step);
                    f.Bounces--; f.Life += c.shotBounceFuse; f.Speed = Mathf.Max(f.Speed, c.shotSpeed * 0.8f);
                    Bounced(f, e);
                    break;
                }
                EndShot(f, e.Pos);
                return;
            }
            if (f.Life <= 0f) EndShot(f, f.Pos);
        }

        void ShotHit(Effect f, Enemy e)
        {
            var c = FormCfg;
            var card = f.Card;
            if (f.As != FormKind.Shot)
            {
                // осколок орбиты рывка или барьера: урон и свойства своей формы
                FormStrike(e, f.Damage * f.Mul, 0.1f, card, f.FSlot, f.As, f.Pos, f.Dir, f.Mul, f);
                return;
            }
            FormStrike(e, c.shotDamage * f.Mul * f.Grow * SnipeMul(card.Level(Trait.Snipe), f.Length), c.shotStun, card, f.FSlot, FormKind.Shot, f.Pos, f.Dir, f.Mul, f);
            if (!f.Payload && !f.Triggered && card.Has(Trait.Trigger)) { f.Triggered = true; FireTrigger(f.FSlot, card, e.Pos, f.Dir, e); }
            int blast = card.Level(Trait.Blast);
            if (blast > 0) FormBlast(card, f.FSlot, FormKind.Shot, e.Pos, c.blastRadius * Mathf.Sqrt(f.Grow), c.blastDamage * blast * f.Mul, f);
            int grow = card.Level(Trait.Grow);
            if (grow > 0) f.Grow = Mathf.Min(c.growMax, f.Grow + c.growPerHit * grow);
        }

        void EndShot(Effect f, Vector2 at)
        {
            var c = FormCfg;
            var card = f.Card;
            int pull = card.Level(Trait.Pull);
            if (pull > 0 && f.Phase == 0) Add(new Effect { Kind = EffectKind.FormVortex, Pos = at, R = c.pullRadius, Life = c.pullTime, Mul = pull });
            if (card.Has(Trait.Return) && f.Phase == 0)
            {
                f.Phase = 1; f.Hit.Clear(); f.Life = 2f; f.Pos = at;
                return;
            }
            f.Done = true;
        }

        /// <summary>Отскок от стены. Умный рикошет (Наведение + Рикошет) — в ближайшего врага, иначе — отражение.</summary>
        Vector2 BounceDir(Effect f, Vector2 at, float step) =>
            Bonds.Has(f.Card, Bond.SmartRicochet) && NearestEnemy(at, 8f, null, f.Hit) is Enemy t ? (t.Pos - at).normalized : Reflect(at, f.Dir, step);

        /// <summary>После отскока: Зеркальце III — +20% урона за отскок; Шрапнель — осколок.</summary>
        void Bounced(Effect f, Enemy hit)
        {
            if (TrinketIII(TrinketKind.Mirror)) f.Mul *= 1f + TrinketCfg.mirrorBounce;
            Shrapnel(f, hit);
        }

        /// <summary>Шрапнель (Рикошет + Раздвоение): отскок выпускает осколок под 30° — без отскоков, долей bondShrapnel.</summary>
        void Shrapnel(Effect f, Enemy hit)
        {
            if (!Bonds.Has(f.Card, Bond.Shrapnel)) return;
            var c = FormCfg;
            var dir = Rotate(f.Dir, (rng.Next(2) == 0 ? -30f : 30f) * Mathf.Deg2Rad);
            var o = f.Kind == EffectKind.FormShot
                ? SpawnShot(f.FSlot, f.Card, f.Pos, dir, f.Mul * c.bondShrapnel, true)
                : Add(new Effect
                {
                    Kind = EffectKind.FormBolt, Pos = f.Pos, Dir = dir, Speed = f.Speed, Life = 0.6f, R = f.R, Card = f.Card, FSlot = f.FSlot, As = f.As,
                    Damage = f.Damage, Mul = f.Mul * c.bondShrapnel, Parent = f.Parent, Payload = f.Payload,
                });
            if (o == null) return;
            o.Bounces = 0;
            if (hit != null) o.Hit.Add(hit);
        }

        /// <summary>Отражение от препятствия: пробуем развернуть по горизонтали, по вертикали, иначе назад.</summary>
        Vector2 Reflect(Vector2 at, Vector2 dir, float step)
        {
            float probe = Mathf.Max(step, 0.15f);
            var hx = new Vector2(-dir.x, dir.y);
            var hy = new Vector2(dir.x, -dir.y);
            bool Free(Vector2 d) => InsideRoom(at + d * probe, 0f) && !BlocksShot(at + d * probe, 0.1f);
            bool blockedX = Mathf.Abs(dir.x) > 0.01f && !Free(new Vector2(Mathf.Sign(dir.x), 0f));
            bool blockedY = Mathf.Abs(dir.y) > 0.01f && !Free(new Vector2(0f, Mathf.Sign(dir.y)));
            if (blockedX && !blockedY) return hx;
            if (blockedY && !blockedX) return hy;
            if (Free(hx)) return hx;
            if (Free(hy)) return hy;
            return -dir;
        }

        /// <summary>Снаряд духа или отражённый барьером: летит прямо, бьёт первого (Phase 2 — летит в свою цель).</summary>
        void UpdateFormBolt(Effect f, float dt)
        {
            var c = FormCfg;
            var card = f.Card;
            if (f.Phase == 2 && f.Target != null && f.Target.Alive) f.Dir = (f.Target.Pos - f.Pos).normalized;
            if (f.Phase == 0 && f.As == FormKind.Summon) Steer(f, card.Level(Trait.Homing), dt);
            if (f.Phase == 1)
            {
                // Возврат: снаряд духа летит обратно к духу
                var home = f.Parent != null && !f.Parent.Done ? f.Parent.Pos : Player.Pos;
                var to = home - f.Pos;
                if (to.magnitude < 0.4f) { f.Done = true; return; }
                f.Dir = to.normalized;
            }
            f.Life -= dt;
            var prev = f.Pos;
            float step = f.Speed * dt;
            f.Pos += f.Dir * step;
            HitRoomObjects(f.Pos, f.R); // выстрел духа — тоже
            if (f.Phase != 1 && (!InsideRoom(f.Pos, 0f) || BlocksShot(f.Pos, f.R * 0.5f)))
            {
                if (f.Bounces > 0) { f.Pos = prev; f.Dir = BounceDir(f, prev, step); f.Bounces--; f.Life += 0.3f; Bounced(f, null); }
                else { EndBolt(f); return; }
            }
            foreach (var e in Enemies)
            {
                if (!e.Alive || f.Hit.Contains(e) || (e.Pos - f.Pos).magnitude > f.R + e.R) continue;
                f.Hit.Add(e);
                FormStrike(e, f.Damage * f.Mul, 0.15f, card, f.FSlot, f.As, f.Pos, f.Dir, f.Mul, f);
                if (f.As == FormKind.Summon && !f.Payload) FireTrigger(f.FSlot, card, e.Pos, f.Dir, e);
                if (f.Phase == 1) continue;
                if (f.Bounces > 0)
                {
                    var next = NearestEnemy(e.Pos, 5f, e, f.Hit);
                    f.Dir = next != null ? (next.Pos - f.Pos).normalized : -f.Dir;
                    f.Bounces--; f.Life += 0.3f;
                    Bounced(f, e);
                    break;
                }
                EndBolt(f);
                return;
            }
            if (f.Life <= 0f) EndBolt(f);
        }

        void EndBolt(Effect f)
        {
            if (f.Phase == 0 && f.As == FormKind.Summon && f.Card.Has(Trait.Return)) { f.Phase = 1; f.Hit.Clear(); f.Life = 2f; return; }
            f.Done = true;
        }

        bool ZoneContains(Effect z, Enemy e)
        {
            if (z.Phase == 1) return DistToSegment(e.Pos, z.Start, z.To) <= FormCfg.zoneRiftWidth * z.Grow + e.R;
            return (e.Pos - z.Pos).magnitude <= z.R * z.Grow + e.R;
        }

        void UpdateFormZone(Effect z, float dt)
        {
            var c = FormCfg;
            var card = z.Card;
            var s = z.FSlot;
            if (z.Phase == 2)
            {
                // Возврат: зона стягивается к герою, нанося урон по пути
                var to = Player.Pos - z.Pos;
                float d = to.magnitude;
                float step = 9f * dt;
                z.Pos += to.normalized * Mathf.Min(step, d);
                z.Grow = Mathf.Max(0.35f, z.Grow - dt * 1.5f);
                foreach (var e in Enemies)
                    if (e.Alive && !z.Hit.Contains(e) && (e.Pos - z.Pos).magnitude <= z.R * z.Grow + e.R)
                    {
                        z.Hit.Add(e);
                        FormStrike(e, c.zoneDamage * 3f * z.Mul, 0.2f, card, s, FormKind.Zone, z.Pos, e.Pos - z.Pos, z.Mul, z);
                    }
                if (d <= step + 0.2f) z.Done = true;
                return;
            }
            if (z.Phase == 0)
            {
                if (card.Has(Trait.Orbit)) z.Pos = Player.Pos; // Орбита: аура вокруг Смерти
                else if (card.Level(Trait.Homing) is int homing and > 0 && NearestEnemy(z.Pos, c.homingRange + z.R) is Enemy t)
                    z.Pos = Vector2.MoveTowards(z.Pos, t.Pos, c.homingZoneSpeed * homing * dt); // Наведение: ползёт к врагу
            }
            HitRoomObjects(z.Pos, z.R * z.Grow); // зона накрыла куб или урну
            int pull = card.Level(Trait.Pull), doom = card.Level(Trait.Doom);
            foreach (var e in Enemies)
            {
                if (!e.Alive || !ZoneContains(z, e)) continue;
                e.ZoneSlowT = 0.1f;
                if (doom > 0) e.LinkT = c.linkTime;
                if (pull > 0 && z.Phase == 0) { e.Pull = 0.1f; e.PullTo = z.Pos; e.PullSpeed = c.pullSpeed * pull; }
            }
            z.Tick -= dt;
            if (z.Tick <= 0f)
            {
                z.Tick += c.zoneTick;
                int grow = card.Level(Trait.Grow), inside = 0;
                float kinetic = KineticMul(card);
                foreach (var e in Enemies)
                {
                    if (!e.Alive || !ZoneContains(z, e)) continue;
                    inside++;
                    if (z.Hit.Add(e) && grow > 0) z.Grow = Mathf.Min(1.6f, z.Grow + 0.08f * grow);
                    FormStrike(e, c.zoneDamage * z.Mul * kinetic, 0f, card, s, FormKind.Zone, z.Pos, e.Pos - z.Pos, z.Mul, z);
                }
                HealPlayer(c.siphonZone * c.zoneTick * card.Level(Trait.Siphon) * inside); // вытягивает жизнь
                int lightning = card.Level(Trait.Lightning);
                if (lightning > 0)
                {
                    // молния бьёт врагов рядом с зоной, но снаружи
                    int n = 0;
                    foreach (var e in Enemies)
                    {
                        if (n >= lightning || !e.Alive || ZoneContains(z, e) || (e.Pos - z.Pos).magnitude > z.R * z.Grow + 3f) continue;
                        n++;
                        Events.Add(new SimEvent { Kind = EventKind.Zap, Pos = z.Pos, To = e.Pos });
                        LightningStrike(e, c.chainMin * 2f * z.Mul, 0.2f, card, s, FormKind.Zone, z.Pos, e.Pos - z.Pos, z.Mul, z);
                    }
                }
            }
            z.Life -= dt;
            if (z.Life > 0f) return;
            // конец: взрыв, эхо, рикошет к ближайшей группе, возврат к герою
            int blast = card.Level(Trait.Blast);
            if (blast > 0) FormBlast(card, s, FormKind.Zone, z.Pos, z.Phase == 1 ? c.blastRadius : z.R * z.Grow, c.blastDamage * blast * z.Mul, z);
            if (card.Has(Trait.Echo) && !z.Echoed) SpawnZoneChild(z, z.Pos, Mathf.Min(1f, c.echoFraction * card.Level(Trait.Echo)), true, 0).Gen = 1;
            else if (z.Gen == 1 && TrinketIII(TrinketKind.TuningFork)) SpawnZoneChild(z, z.Pos, z.Mul * 0.5f, true, 0).Gen = 2; // Камертон III
            if (!z.Payload) FireTrigger(s, card, z.Pos, Player.Face);
            if (z.Bounces > 0)
            {
                Enemy target = null;
                float bd = 9f;
                foreach (var e in Enemies)
                    if (e.Alive && !ZoneContains(z, e)) { float d = (e.Pos - z.Pos).magnitude; if (d < bd) { bd = d; target = e; } }
                if (target != null) SpawnZoneChild(z, target.Pos, z.Mul * 0.6f, z.Echoed, z.Bounces - 1);
            }
            if (card.Has(Trait.Return)) { z.Phase = 2; z.Hit.Clear(); z.Life = 0f; return; }
            z.Done = true;
        }

        Effect SpawnZoneChild(Effect parent, Vector2 at, float mul, bool echoed, int bounces)
        {
            var z = SpawnZone(parent.FSlot, parent.Card, at, mul, echoed, bounces);
            if (parent.Phase == 1) { z.Start = parent.Start + (at - parent.Pos); z.To = parent.To + (at - parent.Pos); z.Pos = at; }
            if (parent.Payload) { z.Payload = true; parent.FSlot.Active.Remove(z); }
            return z;
        }

        void UpdateSpirit(Effect f, float dt)
        {
            var c = FormCfg;
            var card = f.Card;
            var s = f.FSlot;
            var p = Player;
            f.Life -= dt;
            if (f.Life <= 0f) { SpiritEnd(f); return; }
            float mul = f.Mul * f.Grow;
            int blast = card.Level(Trait.Blast);
            if (blast > 0)
            {
                // камикадзе: летит в ближайшего врага и взрывается
                var t = NearestEnemy(f.Pos, 12f);
                if (t != null)
                {
                    var to = t.Pos - f.Pos;
                    f.Pos += to.normalized * (7f * dt);
                    if (to.magnitude <= 0.5f + t.R) { FormBlast(card, s, FormKind.Summon, f.Pos, c.blastRadius * 1.3f, c.blastDamage * 1.5f * blast * mul, f); SpiritEnd(f); }
                    return;
                }
            }
            if (f.Phase == 4)
            {
                // таран (Пронзание): насквозь через цель
                var to = f.To - f.Pos;
                float step = 12f * dt;
                f.Pos += to.normalized * Mathf.Min(step, to.magnitude);
                foreach (var e in Enemies)
                    if (e.Alive && !f.Hit.Contains(e) && (e.Pos - f.Pos).magnitude <= 0.5f + e.R)
                    {
                        f.Hit.Add(e);
                        FormStrike(e, c.summonRamDamage * card.Level(Trait.Pierce) * mul, 0.3f, card, s, FormKind.Summon, f.Pos, to, f.Mul, f);
                    }
                if (to.magnitude <= step) f.Phase = 0;
            }
            else if (card.Level(Trait.Orbit) is int orbit and > 0)
            {
                // Орбита: дух кружит вокруг Смерти и таранит касанием
                f.Angle += c.orbitSpeed * 0.8f * dt;
                float a = f.Angle * Mathf.Deg2Rad;
                var home = p.Pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (c.orbitRadius + 0.4f);
                f.Pos = Vector2.MoveTowards(f.Pos, home, Mathf.Max(12f, (home - f.Pos).magnitude * 8f) * dt);
                f.Hold += dt;
                if (f.Hold >= c.orbitRehit) { f.Hold = 0f; f.Hit.Clear(); }
                foreach (var e in Enemies)
                    if (e.Alive && !f.Hit.Contains(e) && (e.Pos - f.Pos).magnitude <= 0.5f + e.R)
                    {
                        f.Hit.Add(e);
                        FormStrike(e, c.orbitSpiritRam * orbit * mul, 0.1f, card, s, FormKind.Summon, f.Pos, e.Pos - f.Pos, f.Mul, f);
                    }
            }
            else
            {
                var home = p.Pos + Rotate(-p.Face, f.Back * 0.9f) * c.summonOffset;
                f.Pos = Vector2.MoveTowards(f.Pos, home, Mathf.Max(c.summonSpeed, (home - f.Pos).magnitude * 4f) * dt);
            }
            int pull = card.Level(Trait.Pull);
            if (pull > 0)
                foreach (var e in Enemies)
                    if (e.Alive && (e.Pos - f.Pos).magnitude <= 3f + e.R && (e.Pos - f.Pos).magnitude > 0.9f) { e.Pull = 0.1f; e.PullTo = f.Pos; e.PullSpeed = 1.5f * pull; }
            int lightning = card.Level(Trait.Lightning);
            if (lightning > 0)
            {
                f.Tick -= dt;
                if (f.Tick <= 0f)
                {
                    f.Tick += 0.3f;
                    foreach (var e in Enemies)
                        if (e.Alive && DistToSegment(e.Pos, p.Pos, f.Pos) <= 0.35f + e.R)
                            LightningStrike(e, 8f * HadesScale.Hp * lightning * mul, 0.1f, card, s, FormKind.Summon, f.Pos, e.Pos - f.Pos, f.Mul, f);
                }
            }
            f.T -= dt;
            if (f.T > 0f || f.Phase == 4) return;
            int snipe = card.Level(Trait.Snipe);
            float range = c.summonRange * (snipe > 0 ? c.snipeSummonRange : 1f);
            var target = NearestEnemy(f.Pos, range);
            if (target == null) return;
            int pierce = card.Level(Trait.Pierce);
            if (pierce > 0)
            {
                f.Phase = 4; f.Hit.Clear();
                var d = (target.Pos - f.Pos).normalized;
                f.To = target.Pos + d * 1.5f;
                CollideCircle(ref f.To, 0.3f);
                f.T = c.summonRamInterval;
                return;
            }
            var dir = (target.Pos - f.Pos).normalized;
            Add(new Effect
            {
                Kind = EffectKind.FormBolt, Pos = f.Pos + dir * 0.3f, Dir = dir, Speed = c.shotSpeed, Life = range / c.shotSpeed + 0.2f, R = 0.2f,
                Card = card, FSlot = s, As = FormKind.Summon, Damage = c.summonDamage * SnipeMul(snipe, (target.Pos - f.Pos).magnitude),
                Mul = mul * KineticMul(card), Parent = f, Bounces = c.ricochetBounces * card.Level(Trait.Ricochet),
                Payload = f.Payload,
            });
            f.T = c.summonFireInterval;
        }

        void SpiritEnd(Effect f)
        {
            if (f.Done) return;
            f.Done = true;
            var c = FormCfg;
            var card = f.Card;
            int frost = card.Level(Trait.Frost);
            if (frost > 0)
            {
                foreach (var e in Enemies) if (e.Alive && (e.Pos - f.Pos).magnitude <= 2f + 0.3f * frost + e.R) AddFrost(e, c.frostToFreeze);
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = f.Pos, Radius = 2f + 0.3f * frost, Color = 2 });
            }
            foreach (var e in Enemies) if (e.Alive && e.DoomBy == f && e.DoomT > 0f) DoomBurst(e);
            if (TrinketIII(TrinketKind.Bell))
            {
                // Колокольчик III: дух, исчезая, взрывается со свойствами призыва
                float r = c.blastRadius;
                FormBlast(card, f.FSlot, FormKind.Summon, f.Pos, r, c.blastDamage * f.Mul, f);
                foreach (var e in Enemies) if (e.Alive && (e.Pos - f.Pos).magnitude <= r + e.R) FormStrike(e, 0f, 0f, card, f.FSlot, FormKind.Summon, f.Pos, e.Pos - f.Pos, f.Mul, f);
            }
            if (card.Has(Trait.Echo) && !f.Echoed) SpawnSpirit(f.FSlot, card, f.Back, f.Mul * Mathf.Min(1f, c.echoFraction * card.Level(Trait.Echo)), true);
            Events.Add(new SimEvent { Kind = EventKind.Teleport, Pos = f.Pos, To = f.Pos, Color = 5 });
        }

        void UpdateBarrier(Effect b, float dt)
        {
            var c = FormCfg;
            var card = b.Card;
            b.Pos = Player.Pos;
            int fire = card.Level(Trait.Fire);
            if (fire > 0)
            {
                b.Tick -= dt;
                if (b.Tick <= 0f)
                {
                    b.Tick += 0.5f;
                    foreach (var e in Enemies) if (e.Alive && (e.Pos - b.Pos).magnitude <= b.R * b.Grow + 0.6f + e.R) AddBurn(e, c.burn * 0.5f * fire);
                }
            }
            int kinetic = card.Level(Trait.Kinetic);
            if (kinetic > 0 && RunFactor > 0.2f)
            {
                // Разгон: на бегу щит таранит касанием, того же врага — не чаще kineticBarrierRehit
                b.Hold += dt;
                if (b.Hold >= c.kineticBarrierRehit) { b.Hold = 0f; b.Hit.Clear(); }
                foreach (var e in Enemies)
                    if (e.Alive && !b.Hit.Contains(e) && (e.Pos - b.Pos).magnitude <= b.R * b.Grow + 0.3f + e.R)
                    {
                        b.Hit.Add(e);
                        FormStrike(e, c.kineticBarrierRam * kinetic * RunFactor, 0.2f, card, b.FSlot, FormKind.Barrier, b.Pos, e.Pos - b.Pos, 1f, b);
                    }
            }
            b.Life -= dt;
            if (b.Life <= 0f) BarrierEnd(b);
        }

        void UpdateFormTrail(Effect f, float dt)
        {
            var c = FormCfg;
            f.Life -= dt;
            if (f.Life <= 0f) { f.Done = true; return; }
            f.Tick -= dt;
            bool tick = f.Tick <= 0f;
            if (tick) f.Tick += 0.5f;
            var a = f.Points[0];
            var b = f.Points[1];
            foreach (var e in Enemies)
            {
                if (!e.Alive || DistToSegment(e.Pos, a, b) > f.R + e.R) continue;
                if (f.Phase == 1) e.ZoneSlowT = 0.1f; // ледяная дорожка: замедление, пока на ней
                if (!tick) continue;
                if (f.Phase == 0) AddBurn(e, c.burn * 0.5f * f.Mul);
                else AddFrost(e, 1);
            }
        }

        /// <summary>Тень рывка (Раздвоение — зеркальный рывок, Эхо — повтор через 1 с): касается врагов по своему пути.</summary>
        void UpdateShade(Effect f, float dt)
        {
            if (f.T > 0f) { f.T -= dt; f.Pos = f.Start; return; }
            var c = FormCfg;
            float k = Mathf.Clamp01(f.Hold += dt / c.dashTime);
            f.Pos = Vector2.Lerp(f.Start, f.To, k);
            foreach (var e in Enemies)
                if (e.Alive && !f.Hit.Contains(e) && (e.Pos - f.Pos).magnitude <= c.dashTouch + e.R)
                {
                    f.Hit.Add(e);
                    DashTouch(e, f.Card, f.FSlot, f.Pos, f.To - f.Start, f.Mul, f, (f.Pos - f.Start).magnitude);
                }
            if (k < 1f) return;
            f.Done = true;
            DashEndHooks(f.Card, f.FSlot, f.Start, f.To, f.Mul);
        }
    }
}
