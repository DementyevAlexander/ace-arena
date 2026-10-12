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
        public bool Charging, Queued; // снаряд: кнопка зажата / короткое нажатие ждёт выстрела
        public float Charge, FireT;
        public float Haste;           // Жатва снаряда: двойная скорострельность
        public float ReapLock;
        public bool FreeDash;         // Рикошет рывка: второй рывок уже бесплатный
        public readonly List<Effect> Active = new();

        public bool Ready => Card != null && Cd <= 0f;

        public void Reset()
        {
            Cd = CdTotal = 0f; Charging = Queued = false; Charge = FireT = Haste = ReapLock = 0f; FreeDash = false;
            Active.Clear();
        }
    }

    /// <summary>Режим форм: 4 слота (ЛКМ, ПКМ, Q — снаряд, зона, призыв или барьер; Space — рывок), в каждом карта
    /// «форма + свойства». После каждой второй волны — выбор из трёх карточек: поставить в слот или влить душу в карту.
    /// Числа — FormTuning (по Hades II). Свойства на каждой форме — FormText.Cell.</summary>
    public sealed partial class ArenaSim
    {
        public readonly FormTuning FormCfg = new();
        public bool FormsMode { get; private set; }
        public readonly FormSlot[] Forms = { new(), new(), new(), new() };
        public readonly List<SkillCard> Offers = new();
        public bool RewardPending;
        public int Rewards;
        public const int DashSlot = 3;
        int rewardWave = -1;

        // рывок формы: из A в B равномерно (BlinkEaseIn/Out 0)
        bool fDashing, fDashReturn, fDashWall, fDashTouched, fDashKilled;
        Vector2 fDashFrom, fDashTo, fDashDir;
        float fDashT, fDashDur, fDashRange;
        FormSlot fDashSlot;
        readonly HashSet<Enemy> fDashHit = new();
        float fShotSlow;
        bool doomBursting, sharing;

        public bool FormDashing => fDashing;

        /// <summary>Включает режим форм и начинает забег заново: снаряд в ЛКМ, рывок в Space, сразу — первый выбор.</summary>
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
            fDashing = false; fShotSlow = 0f; Rewards = 0; rewardWave = -1;
            OpenReward();
        }

        // ---------- карточки ----------

        void OpenReward()
        {
            RewardPending = true;
            Offers.Clear();
            for (int guard = 0; Offers.Count < 3 && guard < 100; guard++)
            {
                double r = rng.NextDouble() * 4.6;
                var form = r < 1.0 ? FormKind.Shot : r < 1.6 ? FormKind.Dash : r < 2.6 ? FormKind.Zone : r < 3.6 ? FormKind.Summon : FormKind.Barrier;
                var trait = (Trait)rng.Next(15);
                bool dup = false;
                foreach (var o in Offers) if (o.Form == form || o.Own == trait) dup = true;
                if (!dup) Offers.Add(new SkillCard(form, trait));
            }
            Events.Add(new SimEvent { Kind = EventKind.RewardReady });
        }

        /// <summary>Поставить карточку в слот: рывок — только в Space, остальные — в ЛКМ, ПКМ, Q. Прежняя карта пропадает.</summary>
        public bool CanTake(int offer, int slot) =>
            RewardPending && offer >= 0 && offer < Offers.Count && slot >= 0 && slot < 4 && (Offers[offer].Form == FormKind.Dash) == (slot == DashSlot);

        /// <summary>Влить душу карточки (её свойство) в карту слота: форма остаётся от карты слота.</summary>
        public bool CanFuse(int offer, int slot) =>
            RewardPending && offer >= 0 && offer < Offers.Count && slot >= 0 && slot < 4 && Forms[slot].Card != null && Forms[slot].Card.CanFuse;

        public void TakeOffer(int offer, int slot)
        {
            if (!CanTake(offer, slot)) return;
            foreach (var f in Forms[slot].Active) f.Done = true;
            Forms[slot].Card = Offers[offer];
            Forms[slot].Reset();
            FinishReward();
        }

        public void FuseOffer(int offer, int slot)
        {
            if (!CanFuse(offer, slot)) return;
            Forms[slot].Card.Souls.Add(Offers[offer].Own.Value);
            FinishReward();
        }

        void FinishReward()
        {
            RewardPending = false;
            Offers.Clear();
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
            for (int i = 0; i < 4; i++)
            {
                var s = Forms[i];
                s.Haste -= dt; s.ReapLock -= dt;
                s.Active.RemoveAll(f => f.Done);
                if (s.Card == null) continue;
                // зона и барьер: новая — только когда прежняя исчезла (ActiveProjectileCap 1)
                if (s.Card.Form is FormKind.Zone or FormKind.Barrier)
                {
                    float left = 0f;
                    foreach (var f in s.Active) if (f.Kind is EffectKind.FormZone or EffectKind.FormBarrier) left = Mathf.Max(left, f.Life + (f.Kind == EffectKind.FormZone && f.Card.Has(Trait.Return) ? 0.5f : 0f));
                    if (left > 0f) { float extra = s.Card.Form == FormKind.Zone ? c.zoneCooldown : c.barrierCooldown; s.Cd = Mathf.Max(s.Cd, left + extra); s.CdTotal = Mathf.Max(s.CdTotal, s.Cd); }
                }
                s.Cd -= dt;
                switch (s.Card.Form)
                {
                    case FormKind.Shot:
                        if (UpdateShotSlot(s, i, dt, input)) shooting = true;
                        break;
                    case FormKind.Dash:
                        if (input.IsPressed(i) && s.Ready && !fDashing) StartFormDash(s, input);
                        break;
                    default:
                        if (input.IsPressed(i) && s.Ready && !fDashing) ActivateForm(s, input);
                        break;
                }
            }

            p.Moving = false;
            if (fDashing) UpdateFormDash(dt);
            else if (p.KnockT > 0f) { p.KnockT -= dt; p.Pos += p.KnockVel * dt; }
            else if (p.HitStun <= 0f && input.Move.sqrMagnitude > 0.0001f)
            {
                var dir = input.Move.normalized;
                float speed = PlayerCfg.speed * (fShotSlow > 0f ? c.shotMoveSlow : 1f) * (p.Slow > 0f ? CasketCfg.slow : 1f);
                p.Pos += dir * (speed * dt);
                p.Moving = true;
                if (!shooting) p.Face = dir;
            }
            if (p.Lock != null && (!p.Lock.Alive || input.Held == 0)) p.Lock = null;
            p.Aim = shooting ? p.Face : ComputeAimPreview(input);
            CollideCircle(ref p.Pos, PlayerCfg.radius, before);
        }

        /// <summary>Снаряд — атака факелов Hades II: держать — первый выстрел через ChargeTime 0.25 с, дальше каждые 0.38 с;
        /// короткое нажатие — один выстрел. Пока заряжается и 0.1 с после выстрела — бег × 0.35. true — идёт стрельба.</summary>
        bool UpdateShotSlot(FormSlot s, int i, float dt, SimInput input)
        {
            var c = FormCfg;
            if (input.IsPressed(i) && !s.Charging) { s.Charging = true; s.Queued = true; s.Charge = 0f; s.FireT = 0f; }
            if (!s.Charging) return false;
            if (fDashing) return true;
            var dir = AimWith(c.shotLockRange, 180f, input);
            Player.Face = dir;
            s.Charge += dt;
            fShotSlow = Mathf.Max(fShotSlow, dt * 1.5f);
            if (s.Charge < c.shotCharge) return true;
            s.FireT -= dt;
            if (s.FireT <= 0f)
            {
                FireShot(s, i, dir);
                s.FireT += c.shotInterval * (s.Haste > 0f ? 0.5f : 1f);
                s.Queued = false;
                fShotSlow = c.shotFireSlowTime;
            }
            if (!input.IsHeld(i) && !s.Queued) { s.Charging = false; return false; }
            return true;
        }

        void FireShot(FormSlot s, int i, Vector2 dir)
        {
            var c = FormCfg;
            var card = s.Card;
            var origin = Player.Pos + dir * 0.4f;
            SpreadShots(s, card, origin, dir, 1f, false);
            int echo = card.Level(Trait.Echo);
            if (echo > 0)
                Add(new Effect { Kind = EffectKind.FormEchoShot, Pos = origin, Dir = dir, T = c.echoDelay, Card = card, FSlot = s, Mul = Mathf.Min(1f, c.echoFraction * echo) });
            Events.Add(new SimEvent { Kind = EventKind.CardCast, Pos = Player.Pos, To = dir, Value = i, Color = 2 });
        }

        /// <summary>Раздвоение: веер из 1 + уровень снарядов.</summary>
        void SpreadShots(FormSlot s, SkillCard card, Vector2 origin, Vector2 dir, float mul, bool echoed)
        {
            int n = 1 + card.Level(Trait.Split);
            for (int k = 0; k < n; k++)
            {
                float a = (k - (n - 1) * 0.5f) * FormCfg.splitAngle * Mathf.Deg2Rad;
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
            return Add(new Effect
            {
                Kind = EffectKind.FormShot, Pos = pos, Dir = dir, Speed = c.shotSpeed, Life = c.shotFuse, R = c.shotRadius,
                Card = card, FSlot = s, As = FormKind.Shot, Mul = mul, Echoed = echoed, Bounces = c.ricochetBounces * card.Level(Trait.Ricochet),
            });
        }

        // ---------- активация зоны, призыва, барьера ----------

        void ActivateForm(FormSlot s, SimInput input)
        {
            var c = FormCfg;
            var card = s.Card;
            var p = Player;
            switch (card.Form)
            {
                case FormKind.Zone:
                {
                    var at = ZoneTarget(input);
                    SpawnZone(s, card, at, 1f, false, card.Level(Trait.Ricochet));
                    if (card.Has(Trait.Split))
                    {
                        var mirror = p.Pos - (at - p.Pos);
                        CollideCircle(ref mirror, 0.3f);
                        SpawnZone(s, card, mirror, 1f, false, card.Level(Trait.Ricochet));
                    }
                    s.Cd = s.CdTotal = c.zoneLife + c.zoneCooldown;
                    break;
                }
                case FormKind.Summon:
                {
                    int alive = 0;
                    foreach (var f in s.Active) if (f.Kind == EffectKind.FormSpirit && f.Phase != 3) alive++;
                    if (alive >= 2 && card.Has(Trait.Reap)) foreach (var f in s.Active) { if (f.Kind == EffectKind.FormSpirit) { f.Life = 0f; break; } }
                    if (card.Has(Trait.Split))
                    {
                        SpawnSpirit(s, card, -1f, c.summonSplit, false);
                        SpawnSpirit(s, card, 1f, c.summonSplit, false);
                    }
                    else SpawnSpirit(s, card, alive > 0 ? 1f : -1f, 1f, false);
                    s.Cd = s.CdTotal = c.summonCooldown;
                    break;
                }
                case FormKind.Barrier:
                    SpawnBarrier(s, card, c.barrierLife, false);
                    s.Cd = s.CdTotal = c.barrierLife + c.barrierCooldown;
                    break;
            }
            Events.Add(new SimEvent { Kind = EventKind.CardCast, Pos = p.Pos, To = p.Face, Value = System.Array.IndexOf(Forms, s), Color = 1 });
        }

        /// <summary>Точка зоны: мышь — курсор (не дальше zoneCastRange), геймпад — цель автолока, иначе 3.5 перед героем.</summary>
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
                var dir = AimWith(c.zoneCastRange, 75f, input);
                at = p.Lock != null ? p.Lock.Pos : p.Pos + dir * 3.5f;
                p.Face = dir;
            }
            Vector2 mn = RoomMin, mx = RoomMax;
            at.x = Mathf.Clamp(at.x, mn.x + 0.5f, mx.x - 0.5f);
            at.y = Mathf.Clamp(at.y, mn.y + 0.5f, mx.y - 0.5f);
            return at;
        }

        Effect SpawnZone(FormSlot s, SkillCard card, Vector2 at, float mul, bool echoed, int bounces)
        {
            var c = FormCfg;
            var p = Player;
            var z = new Effect
            {
                Kind = EffectKind.FormZone, Pos = at, R = c.zoneRadius, Life = c.zoneLife * (echoed ? 0.5f : 1f), Tick = 0f,
                Card = card, FSlot = s, As = FormKind.Zone, Mul = mul, Echoed = echoed, Bounces = bounces,
            };
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
                    if (e.Alive && ZoneContains(z, e)) Shove(e, e.Pos - z.Pos, c.pushSpeed * (1f + 0.3f * (push - 1)), c.pushCollide * mul);
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
                Card = card, FSlot = s, As = FormKind.Summon, Mul = mul, Echoed = echo, Back = side, T = 0.3f,
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
                foreach (var e in Enemies) if (e.Alive && (e.Pos - p.Pos).magnitude <= 2.5f + e.R) Shove(e, e.Pos - p.Pos, c.pushSpeed * (1f + 0.3f * (push - 1)), c.pushCollide);
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
            return b;
        }

        // ---------- рывок ----------

        void StartFormDash(FormSlot s, SimInput input)
        {
            var p = Player;
            var dir = input.Move.sqrMagnitude > 0.0001f ? input.Move.normalized : p.Face;
            if (input.UseMouse && input.HasCursor && PlayerCfg.dashAtCursor > 0f && (input.Cursor - p.Pos).sqrMagnitude > 0.01f) dir = (input.Cursor - p.Pos).normalized;
            BeginDash(s, dir, false);
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

        void BeginDash(FormSlot s, Vector2 dir, bool back, Vector2? target = null)
        {
            var c = FormCfg;
            var p = Player;
            var card = s.Card;
            fDashRange = c.dashRange * (card.Has(Trait.Pierce) ? c.dashPierceRange : 1f);
            fDashFrom = p.Pos;
            fDashTo = target ?? TravelTarget(p.Pos, dir, fDashRange);
            fDashDir = dir;
            fDashWall = !target.HasValue && (fDashTo - fDashFrom).magnitude < fDashRange - 0.15f;
            fDashT = 0f;
            fDashDur = c.dashTime * (back ? 0.75f : 1f);
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
            int grow = card.Level(Trait.Grow);
            foreach (var e in Enemies)
            {
                if (!e.Alive || fDashHit.Contains(e) || (e.Pos - p.Pos).magnitude > c.dashTouch + e.R) continue;
                fDashHit.Add(e);
                fDashTouched = true;
                if (DashTouch(e, card, s, p.Pos, fDashDir, 1f, null)) fDashKilled = true;
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
            DashEndHooks(card, s, fDashFrom, fDashTo, 1f);
            if (fDashReturn) return;
            s.Cd = s.CdTotal = c.dashCooldown;
            // Рикошет: упёрся в стену или задел врага — второй рывок бесплатно (один раз подряд)
            if (card.Has(Trait.Ricochet) && !s.FreeDash && (fDashWall || fDashTouched)) { s.Cd = 0f; s.FreeDash = true; }
            else s.FreeDash = false;
            if (fDashKilled && card.Has(Trait.Reap)) s.Cd = 0f;
        }

        /// <summary>Касание рывка (героя или тени): урон только с Пронзанием, остальное — свойства. true — враг убит.</summary>
        bool DashTouch(Enemy e, SkillCard card, FormSlot s, Vector2 from, Vector2 dir, float mul, Effect src)
        {
            var c = FormCfg;
            int pierce = card.Level(Trait.Pierce);
            FormStrike(e, pierce > 0 ? c.dashPierceDamage * pierce * mul : 0f, pierce > 0 ? c.dashPierceStun : 0f, card, s, FormKind.Dash, from, e.Pos - from, mul, src);
            return e.Dead;
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
                    if (e.Alive && DistToSegment(e.Pos, from, to) <= 0.5f + e.R) FormStrike(e, c.chainMin * 2f * lightning * mul, 0.2f, card, s, FormKind.Dash, from, to - from, mul, null, true);
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
            if (dmg > 0f) Damage(e, dmg, false); else e.Flash = 0.08f;
            if (e.Dead) { OnFormKill(card, s, form, src); return; }
            if (stun > 0f) ApplyCc(e, CcKind.Stun, stun);
            if (secondary) return;
            int lvl;
            if ((lvl = card.Level(Trait.Fire)) > 0 && form is FormKind.Shot or FormKind.Zone or FormKind.Summon) AddBurn(e, c.burn * lvl * mul);
            if ((lvl = card.Level(Trait.Frost)) > 0 && form is FormKind.Shot or FormKind.Zone) AddFrost(e, lvl);
            if ((lvl = card.Level(Trait.Rot)) > 0 && form != FormKind.Barrier) AddRot(e, lvl);
            if ((lvl = card.Level(Trait.Doom)) > 0 && form is FormKind.Shot or FormKind.Dash or FormKind.Summon) AddDoom(e, lvl, form == FormKind.Summon ? src?.Parent ?? src : null);
            if ((lvl = card.Level(Trait.Push)) > 0 && form is FormKind.Shot or FormKind.Dash or FormKind.Summon)
                Shove(e, form == FormKind.Dash ? e.Pos - Player.Pos : dir, c.pushSpeed * (1f + 0.3f * (lvl - 1)), c.pushCollide * mul);
            if ((lvl = card.Level(Trait.Lightning)) > 0 && form == FormKind.Shot) Chain(e, Mathf.Max(c.chainMin, dmg * c.chainFraction), c.chainTargets * lvl, card, s, form, src);
        }

        /// <summary>Добивание формой: Жатва и Рост духа.</summary>
        void OnFormKill(SkillCard card, FormSlot s, FormKind form, Effect src)
        {
            var c = FormCfg;
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
            foreach (var e in Enemies) if (e.Alive && (e.Pos - at).magnitude <= radius + e.R) FormStrike(e, dmg, 0.2f, card, s, form, at, e.Pos - at, 1f, src, true);
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = at, Radius = radius, Color = 3 });
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
                FormStrike(best, dmg, 0.15f, card, s, form, cur, best.Pos - cur, 1f, src, true);
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
            if (e.Frost < c.frostToFreeze) return;
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

        void AddDoom(Enemy e, int lvl, Effect spirit = null)
        {
            if (e.DoomT > 0f) return; // уже приговорён — новая метка не ставится
            e.DoomT = FormCfg.doomTime;
            e.DoomAcc = 0f;
            e.DoomFrac = FormCfg.doomFraction * lvl;
            e.DoomBy = spirit;
        }

        void DoomBurst(Enemy e)
        {
            e.DoomT = 0f;
            e.DoomBy = null;
            float dmg = e.DoomAcc * e.DoomFrac;
            e.DoomAcc = 0f;
            if (dmg <= 0f || e.Dead) return;
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
            if (e.DoomT > 0f) { e.DoomT -= dt; if (e.DoomT <= 0f) DoomBurst(e); }
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
        void OnKillForms()
        {
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
                if ((lvl = card.Level(Trait.Doom)) > 0) AddDoom(attacker, lvl);
            }
            if ((lvl = card.Level(Trait.Lightning)) > 0)
            {
                var near = new List<Enemy>();
                foreach (var e in Enemies) if (e.Alive && (e.Pos - Player.Pos).magnitude <= 4f) near.Add(e);
                near.Sort((x, y) => (x.Pos - Player.Pos).sqrMagnitude.CompareTo((y.Pos - Player.Pos).sqrMagnitude));
                for (int k = 0; k < Mathf.Min(near.Count, c.chainTargets * lvl); k++)
                {
                    Events.Add(new SimEvent { Kind = EventKind.Zap, Pos = Player.Pos, To = near[k].Pos });
                    FormStrike(near[k], c.chainMin * 3f * lvl, 0.2f, card, s, FormKind.Barrier, Player.Pos, near[k].Pos - Player.Pos, 1f, b, true);
                }
            }
            if ((lvl = card.Level(Trait.Grow)) > 0) { b.Life += c.growBarrierTime * lvl; b.Grow = Mathf.Min(c.growMax, b.Grow + 0.15f * lvl); }
            if (bolt != null)
            {
                // Рикошет и Гниль: снаряд летит обратно во врагов (с гнилью и прочими статусами барьера); Возврат — позже
                if (card.Has(Trait.Ricochet) || card.Has(Trait.Rot))
                {
                    var target = NearestEnemy(Player.Pos, 9f);
                    var dir = target != null ? (target.Pos - Player.Pos).normalized : -bolt.Dir;
                    Add(new Effect
                    {
                        Kind = EffectKind.FormBolt, Pos = Player.Pos + dir * 0.6f, Dir = dir, Speed = c.shotSpeed, Life = 1.2f, R = 0.22f,
                        Card = card, FSlot = s, As = FormKind.Shot, Damage = c.shotDamage * 0.5f * Mathf.Max(1, card.Level(Trait.Ricochet)), Target = target,
                    });
                }
                if (card.Has(Trait.Return) && bolt.Target != null) b.Marks.Add(bolt.Target);
            }
            Events.Add(new SimEvent { Kind = EventKind.PlayerBlocked, Pos = Player.Pos });
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
            }
        }

        void UpdateFormShot(Effect f, float dt)
        {
            var c = FormCfg;
            var card = f.Card;
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
            }
            var prev = f.Pos;
            f.Pos += f.Dir * step;
            if (f.Phase == 0 && (!InsideRoom(f.Pos, 0f) || BlocksShot(f.Pos, f.R * 0.5f)))
            {
                if (f.Bounces > 0)
                {
                    f.Pos = prev;
                    f.Dir = Reflect(prev, f.Dir, step);
                    f.Bounces--; f.Life += c.shotBounceFuse;
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
            FormStrike(e, c.shotDamage * f.Mul * f.Grow, c.shotStun, card, f.FSlot, FormKind.Shot, f.Pos, f.Dir, f.Mul, f);
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
            if (f.Phase != 1 && (!InsideRoom(f.Pos, 0f) || BlocksShot(f.Pos, f.R * 0.5f)))
            {
                if (f.Bounces > 0) { f.Pos = prev; f.Dir = Reflect(prev, f.Dir, step); f.Bounces--; f.Life += 0.3f; }
                else { EndBolt(f); return; }
            }
            foreach (var e in Enemies)
            {
                if (!e.Alive || f.Hit.Contains(e) || (e.Pos - f.Pos).magnitude > f.R + e.R) continue;
                f.Hit.Add(e);
                FormStrike(e, f.Damage * f.Mul, 0.15f, card, f.FSlot, f.As, f.Pos, f.Dir, f.Mul, f);
                if (f.Phase == 1) continue;
                if (f.Bounces > 0)
                {
                    var next = NearestEnemy(e.Pos, 5f, e, f.Hit);
                    f.Dir = next != null ? (next.Pos - f.Pos).normalized : -f.Dir;
                    f.Bounces--; f.Life += 0.3f;
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
                int grow = card.Level(Trait.Grow);
                foreach (var e in Enemies)
                {
                    if (!e.Alive || !ZoneContains(z, e)) continue;
                    if (z.Hit.Add(e) && grow > 0) z.Grow = Mathf.Min(1.6f, z.Grow + 0.08f * grow);
                    FormStrike(e, c.zoneDamage * z.Mul, 0f, card, s, FormKind.Zone, z.Pos, e.Pos - z.Pos, z.Mul, z);
                }
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
                        FormStrike(e, c.chainMin * 2f * z.Mul, 0.2f, card, s, FormKind.Zone, z.Pos, e.Pos - z.Pos, z.Mul, z, true);
                    }
                }
            }
            z.Life -= dt;
            if (z.Life > 0f) return;
            // конец: взрыв, эхо, рикошет к ближайшей группе, возврат к герою
            int blast = card.Level(Trait.Blast);
            if (blast > 0) FormBlast(card, s, FormKind.Zone, z.Pos, z.Phase == 1 ? c.blastRadius : z.R * z.Grow, c.blastDamage * blast * z.Mul, z);
            if (card.Has(Trait.Echo) && !z.Echoed) SpawnZoneChild(z, z.Pos, Mathf.Min(1f, c.echoFraction * card.Level(Trait.Echo)), true, 0);
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

        void SpawnZoneChild(Effect parent, Vector2 at, float mul, bool echoed, int bounces)
        {
            var z = SpawnZone(parent.FSlot, parent.Card, at, mul, echoed, bounces);
            if (parent.Phase == 1) { z.Start = parent.Start + (at - parent.Pos); z.To = parent.To + (at - parent.Pos); z.Pos = at; }
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
                            FormStrike(e, 8f * HadesScale.Hp * lightning * mul, 0.1f, card, s, FormKind.Summon, f.Pos, e.Pos - f.Pos, f.Mul, f, true);
                }
            }
            f.T -= dt;
            if (f.T > 0f || f.Phase == 4) return;
            var target = NearestEnemy(f.Pos, c.summonRange);
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
                Kind = EffectKind.FormBolt, Pos = f.Pos + dir * 0.3f, Dir = dir, Speed = c.shotSpeed, Life = c.summonRange / c.shotSpeed + 0.2f, R = 0.2f,
                Card = card, FSlot = s, As = FormKind.Summon, Damage = c.summonDamage, Mul = mul, Parent = f, Bounces = c.ricochetBounces * card.Level(Trait.Ricochet),
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
                    DashTouch(e, f.Card, f.FSlot, f.Pos, f.To - f.Start, f.Mul, f);
                }
            if (k < 1f) return;
            f.Done = true;
            DashEndHooks(f.Card, f.FSlot, f.Start, f.To, f.Mul);
        }
    }
}
