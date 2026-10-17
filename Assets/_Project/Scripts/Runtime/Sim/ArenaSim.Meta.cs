using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Мета-прогресс в симуляции: коллекция (что выпадает), наборы Наставника, лавка Торговца за Сардис, летопись забегов,
    /// Сардис с боссов (тёмно-красный ромбик), источник в фойе босса. Сохраняет GameController (Meta.Dirty).</summary>
    public sealed partial class ArenaSim
    {
        /// <summary>Мета-прогресс. По умолчанию всё открыто (проверки); игра ставит сохранённый (SetMeta).</summary>
        public MetaProgress Meta { get; private set; } = MetaProgress.AllUnlocked();

        /// <summary>Наборы Наставника: три по две случайные карты из открытой коллекции; взять можно один.</summary>
        public readonly List<SkillCard[]> MentorSets = new();
        /// <summary>Взятый набор (−1 — не брал): его карты встают в ПКМ и Q (рывок — в Space) в начале забега.</summary>
        public int ChosenSet { get; private set; } = -1;
        bool runRecorded;

        public void SetMeta(MetaProgress meta)
        {
            Meta = meta ?? MetaProgress.AllUnlocked();
            NewMentorSets();
        }

        // ---------- Наставник ----------

        /// <summary>Три новых набора по две карты (редкие) из открытых форм и свойств; в наборе не больше одного рывка и нет
        /// двух одинаковых карт.</summary>
        void NewMentorSets()
        {
            MentorSets.Clear();
            ChosenSet = -1;
            var forms = new List<FormKind>();
            var traits = new List<Trait>();
            foreach (FormKind f in System.Enum.GetValues(typeof(FormKind))) if (Meta.Has(f)) forms.Add(f);
            for (int i = 0; i < Traits.Count; i++) if (Meta.Has((Trait)i)) traits.Add((Trait)i);
            if (forms.Count == 0 || traits.Count == 0) return;
            for (int k = 0; k < 3; k++)
            {
                var a = new SkillCard(forms[rng.Next(forms.Count)], traits[rng.Next(traits.Count)]);
                SkillCard b = null;
                for (int t = 0; t < 30; t++)
                {
                    b = new SkillCard(forms[rng.Next(forms.Count)], traits[rng.Next(traits.Count)]);
                    bool twoDashes = a.Form == FormKind.Dash && b.Form == FormKind.Dash;
                    if (!twoDashes && !(a.Form == b.Form && a.Own == b.Own)) break;
                }
                if (a.Form == FormKind.Dash && b.Form == FormKind.Dash) b = new SkillCard(FormKind.Shot, b.Own);
                MentorSets.Add(new[] { a, b });
            }
        }

        /// <summary>Взять набор: в хабе его карты сразу в слотах — попробовать на мишенях; в забег идёт с ним.</summary>
        public void ChooseSet(int i)
        {
            if (i < 0 || i >= MentorSets.Count) return;
            ChosenSet = i;
            Menu = HubRole.None;
            if (HubMode) ResetForms();
            Events.Add(new SimEvent { Kind = EventKind.HubTalk, Pos = Player.Pos });
        }

        /// <summary>Начало забега (и хаб): к Снаряду в ЛКМ и Рывку в Space — карты взятого набора в ПКМ и Q, рывок — вместо простого.</summary>
        void ApplyMentorSet()
        {
            if (ChosenSet < 0 || ChosenSet >= MentorSets.Count) return;
            int next = 1;
            foreach (var proto in MentorSets[ChosenSet])
            {
                var card = new SkillCard(proto.Form, proto.Own);
                if (card.Form == FormKind.Dash) Forms[DashSlot].Card = card;
                else if (next <= 2) Forms[next++].Card = card;
            }
        }

        // ---------- Торговец ----------

        public bool CanBuy(Unlockable u) => !Meta.Has(u) && Meta.sardis >= u.Price;

        /// <summary>Открыть за Сардис: форма, свойство или тринкет начинают попадаться в забеге.</summary>
        public bool Buy(Unlockable u)
        {
            if (!CanBuy(u)) return false;
            Meta.sardis -= u.Price;
            Meta.Unlock(u);
            Events.Add(new SimEvent { Kind = EventKind.SardisSpent, Pos = Player.Pos, Value = u.Price });
            return true;
        }

        /// <summary>Сардис (проверки, отладка).</summary>
        public void AddSardis(int n) { Meta.sardis = Mathf.Max(0, Meta.sardis + n); Meta.Dirty = true; }

        // ---------- летопись ----------

        /// <summary>Конец забега по этажам: в летопись — этаж, победа, убийства, комнаты, время. Один раз за забег.</summary>
        void RecordRun(bool won)
        {
            if (!FloorRun || runRecorded) return;
            runRecorded = true;
            int floor = FloorIndex + 1 + FloorLap * FloorOrder.Length;
            Meta.runs++;
            if (won) Meta.wins++;
            Meta.bestFloor = Mathf.Max(Meta.bestFloor, floor);
            Meta.totalKills += Kills;
            Meta.history.Insert(0, new RunRecord { floor = floor, won = won, kills = Kills, rooms = RunRoomsCleared, time = Time });
            while (Meta.history.Count > MetaProgress.HistorySize) Meta.history.RemoveAt(Meta.history.Count - 1);
            Meta.Dirty = true;
        }

        /// <summary>Связки, которые сейчас на картах в слотах, — в коллекцию Хранителя.</summary>
        void MarkBonds()
        {
            foreach (var s in Forms)
                foreach (var b in Bonds.Of(s.Card))
                    if (!Meta.Has(b)) { Meta.bonds.Add((int)b); Meta.Dirty = true; }
        }

        // ---------- Сардис ----------

        /// <summary>Босс роняет Сардис — тёмно-красный ромбик; подбирается, как Милость, и сразу сохраняется.</summary>
        void DropSardis(Vector2 at, int n)
        {
            for (int i = 0; i < n; i++)
                Add(new Effect { Kind = EffectKind.Sardis, Pos = at, Dir = Rotate(Vector2.up, i * 2.4f), Speed = ArenaCfg.graceScatter * 0.6f, T = ArenaCfg.graceSettle, Charges = 1, R = 0.3f, Life = 999f });
        }

        void UpdateSardisOrb(Effect f, float dt)
        {
            var c = ArenaCfg;
            f.T -= dt;
            if (f.Speed > 0f && f.Hold <= 0f)
            {
                var next = f.Pos + f.Dir * (f.Speed * dt);
                if (InsideRoom(next, 0.15f) && !BlocksShot(next, 0.1f)) f.Pos = next;
                f.Speed = Mathf.Max(0f, f.Speed - 10f * dt);
            }
            var to = Player.Pos - f.Pos;
            float d = to.magnitude;
            if (d <= c.gracePickup)
            {
                f.Done = true;
                Meta.sardis += f.Charges;
                Meta.Dirty = true;
                Events.Add(new SimEvent { Kind = EventKind.SardisCollected, Pos = Player.Pos, Value = f.Charges });
                return;
            }
            if (f.T > 0f || (d > c.graceMagnet && f.Hold <= 0f)) return;
            f.Hold += dt;
            f.Pos += to / d * Mathf.Min(d, c.graceFlySpeed * Mathf.Clamp01(0.35f + f.Hold * 2f) * dt);
        }

        // ---------- источник в фойе босса ----------

        /// <summary>Источник перед боссом (как в Hades перед фурией): в фойе босса, один раз лечит на fountainHeal здоровья.</summary>
        void PlaceFountain()
        {
            if (!FormsMode || Floor == null) return;
            for (int i = 0; i < Floor.Rooms.Count; i++)
            {
                if (Floor.Rooms[i].Kind != EtgRoomKind.BossFoyer) continue;
                var c = EtgFloorGenerator.FloorCellNear(Floor, Floor.Rooms[i].Center);
                if (c == null) continue;
                Add(new Effect { Kind = EffectKind.Fountain, Pos = new Vector2(c.Value.x + 0.5f, c.Value.y + 0.5f), R = 0.7f, Life = 1e9f });
            }
        }

        /// <summary>Источник рядом, ещё не выпит (null — нет).</summary>
        public Effect FountainNear
        {
            get
            {
                if (HubMode || RewardPending || GameOver) return null;
                foreach (var f in Effects)
                    if (f.Kind == EffectKind.Fountain && !f.Done && f.Phase == 0 && (f.Pos - Player.Pos).magnitude <= ArenaCfg.lootReach + f.R) return f;
                return null;
            }
        }

        /// <summary>Испить: здоровье +fountainHeal от наибольшего (без лимита Поглощения), источник иссякает.</summary>
        bool DrinkFountain()
        {
            var f = FountainNear;
            if (f == null) return false;
            f.Phase = 1;
            var p = Player;
            float heal = Mathf.Min(ArenaCfg.fountainHeal * PlayerCfg.hp, PlayerCfg.hp - p.Hp);
            p.Hp += heal;
            Events.Add(new SimEvent { Kind = EventKind.PlayerHealed, Pos = p.Pos, Value = heal });
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = f.Pos, Radius = 1.4f, Color = 2 });
            return true;
        }
    }
}
