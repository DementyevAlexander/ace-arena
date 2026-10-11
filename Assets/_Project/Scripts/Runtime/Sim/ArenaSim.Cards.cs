using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    public sealed partial class ArenaSim
    {
        void PressSlot(int i, SimInput input)
        {
            var s = Slots[i];
            if (!s.Main.HasValue) return;
            var p = Player;
            var card = s.Card;
            // Dio: повторное нажатие возвращает мышь (Reactivation)
            if (s.Bat != null && s.Bat.Phase < 2) { s.Bat.Phase = 2; s.Bat.Hit.Clear(); return; }
            if (!s.Ready) return;
            if (p.Cast != null || p.Dashing)
            {
                var cur = p.Cast != null ? Slots[p.Cast.Slot].Card : null;
                // Прерывать могут карты с CanInterrupt, и только прерываемый каст (PlayerCast.CancelSkill)
                bool can = p.Cast != null && card.CanInterrupt && cur.Interruptible && p.Cast.Slot != i;
                if (!can) { p.Buffer = i; return; }
                p.Cast = null;
            }
            StartCast(i, input);
        }

        void ReleaseSlot(int i, SimInput input)
        {
            var c = Player.Cast;
            if (c != null && c.Slot == i && c.Phase == CastPhase.Hold) FireHold(input);
        }

        void StartCast(int i, SimInput input)
        {
            var card = Slots[i].Card;
            Player.Cast = new Cast { Slot = i, Phase = CastPhase.Casting, T = card.Casting, Dir = AimFor(card, input) };
            Player.Buffer = -1;
            Player.Aim = Player.Cast.Dir;
        }

        void UpdateCast(float dt, SimInput input)
        {
            var p = Player;
            var c = p.Cast;
            if (c == null) return;
            var s = Slots[c.Slot];
            var card = s.Card;
            if (card == null) { p.Cast = null; return; }
            // Hades: пока кнопка держится, запрос обновляется каждый кадр — можно доворачивать во время замаха и удержания
            if (c.Phase != CastPhase.Recovery)
            {
                c.Dir = AimFor(card, input);
                p.Aim = c.Dir;
                if (card.Aims) p.Face = c.Dir;
            }
            c.T -= dt;
            if (c.Phase == CastPhase.Casting && c.T <= 0f)
            {
                if (card.HoldTime > 0f)
                {
                    c.Phase = CastPhase.Hold; c.Hold = 0f;
                    if (!input.IsHeld(c.Slot)) FireHold(input);
                }
                else
                {
                    Fire(c.Slot, c.Dir, 0f);
                    if (p.Cast == c) { c.Phase = CastPhase.Recovery; c.T = card.Recovery; }
                }
            }
            else if (c.Phase == CastPhase.Hold)
            {
                c.Hold += dt;
                if (c.Hold >= card.HoldTime || !input.IsHeld(c.Slot)) FireHold(input);
            }
            else if (c.Phase == CastPhase.Recovery && c.T <= 0f)
            {
                p.Cast = null;
                if (p.Buffer >= 0)
                {
                    int b = p.Buffer; p.Buffer = -1;
                    if (Slots[b].Ready) StartCast(b, input);
                }
            }
        }

        void FireHold(SimInput input)
        {
            var c = Player.Cast;
            var card = Slots[c.Slot].Card;
            Fire(c.Slot, c.Dir, Mathf.Clamp01(c.Hold / card.HoldTime));
            c.Phase = CastPhase.Recovery; c.T = card.Recovery;
        }

        void ConsumeCharge(Slot s)
        {
            s.Charges--;
            if (!s.OnCd) { s.OnCd = true; s.CdTimer = s.Card.Cooldown; }
        }

        HitSource Source(Slot s, float baseDamage)
        {
            var card = s.Card;
            return new HitSource { Slot = s, Card = card, Up = s.Upgrade, Base = baseDamage, Div = card.Div };
        }

        /// <summary>Срабатывание карты (OnCastStart в DandyAce).</summary>
        void Fire(int slotIndex, Vector2 dir, float hold)
        {
            var s = Slots[slotIndex];
            var card = s.Card;
            var p = Player;
            ConsumeCharge(s);
            Events.Add(new SimEvent { Kind = EventKind.CardCast, Pos = p.Pos, To = dir, Value = slotIndex, Color = (int)card.Color });
            var up = s.Upgrade;
            if (up.IsCastBuff && s.UpgCd <= 0f)
            {
                s.UpgCd = up.Cooldown;
                if (up.Kind == UpgradeKind.Haste) p.Haste = up.Duration;
                else if (up.Kind == UpgradeKind.Invulnerability) p.BuffInvuln = up.Duration;
                else if (up.Kind == UpgradeKind.BlazingShoes) p.Shoes = up.Duration;
            }
            var perp = new Vector2(-dir.y, dir.x);
            switch (card.Id)
            {
                case CardId.TwinDaggers:
                {
                    var src = Source(s, 100f); src.OwnDot = DotKind.Bleed; src.OwnDotDamage = 63f;
                    foreach (int side in new[] { -1, 1 })
                        Add(new Effect { Kind = EffectKind.Dagger, Pos = p.Pos + dir * 0.6f + perp * (0.3f * side), Dir = dir, T = 0.4f, Life = 0.6f, Speed = 12f, R = 0.4f, Src = src });
                    break;
                }
                case CardId.FiveOfAKind:
                {
                    var src = Source(s, 35f);
                    for (int k = 0; k < 5; k++)
                    {
                        float a = Mathf.Deg2Rad * (-15f + 7.5f * k);
                        var d = Rotate(dir, a);
                        Add(new Effect { Kind = EffectKind.FiveCard, Pos = p.Pos + d * 0.5f, Dir = d, T = 0.01f * k, Life = 0.5f, Speed = 10f, R = 0.3f, Src = src });
                    }
                    break;
                }
                case CardId.Crescendo:
                {
                    var src = Source(s, Mathf.Round(62f * (1f + hold))); src.OwnDot = DotKind.Burn; src.OwnDotDamage = 248f;
                    Add(new Effect { Kind = EffectKind.Missile, Pos = p.Pos + dir * 0.5f, Dir = dir, Life = 0.7f, Speed = 5.5f, R = 0.24f, Hold = hold, Src = src });
                    break;
                }
                case CardId.Roses:
                {
                    var src = Source(s, 25f); src.OwnDot = DotKind.Poison; src.OwnDotDamage = 125f;
                    var meet = p.Pos + dir * 4f;
                    for (int k = -1; k <= 1; k++)
                    {
                        var start = p.Pos + dir * 0.5f + perp * (0.55f * k);
                        Add(new Effect { Kind = EffectKind.Rose, Pos = start, Dir = (meet - start).normalized, Life = 1f, Speed = 8f, R = 0.2f, Src = src });
                    }
                    break;
                }
                case CardId.Dio:
                {
                    var src = Source(s, 100f); src.OwnDot = DotKind.Bleed; src.OwnDotDamage = 50f;
                    s.Bat = Add(new Effect { Kind = EffectKind.Bat, Pos = p.Pos + dir * 1.3f, Dir = dir, T = 0.15f, Life = 1.5f, Speed = 40f, R = 0.5f, Src = src, Slot = s });
                    break;
                }
                case CardId.BlackHole:
                {
                    var center = p.Pos + dir * 3.4f;
                    CollideCircle(ref center, 1f);
                    var src = Source(s, 30f);
                    Add(new Effect { Kind = EffectKind.BlackHole, Pos = center, Life = 3f, R = 2f, Src = src });
                    break;
                }
                case CardId.Dazzling:
                {
                    var src = Source(s, 125f);
                    const float len = 3.2f, half = 35f;
                    foreach (var e in Enemies)
                    {
                        if (!e.Alive) continue;
                        var d = e.Pos - p.Pos;
                        if (d.magnitude - e.R <= len && Vector2.Angle(dir, d) <= half) HitEnemy(e, src);
                    }
                    Add(new Effect { Kind = EffectKind.Cone, Pos = p.Pos, Dir = dir, Life = 0.25f, R = len });
                    break;
                }
                case CardId.Starburst:
                {
                    var src = Source(s, 225f); src.PushSpeed = 12f;
                    Blast(p.Pos, 2.9f * 1.2f, src, 1);
                    break;
                }
                case CardId.Aegis:
                    p.Shield = 1f;
                    Add(new Effect { Kind = EffectKind.AegisShield, Pos = p.Pos, Life = 1f, R = 0.9f, Src = Source(s, 100f) });
                    break;
                case CardId.MasterfulJump:
                {
                    Enemy target = null;
                    float best = 5f;
                    foreach (var e in Enemies)
                    {
                        if (!e.Alive) continue;
                        float d = (e.Pos - p.Pos).magnitude;
                        if (d <= best) { best = d; target = e; }
                    }
                    var to = p.Pos;
                    if (target != null)
                    {
                        var away = p.Pos - target.Pos;
                        to = target.Pos + (away.sqrMagnitude > 0.001f ? away.normalized : Vector2.down) * (target.R + 0.45f);
                        CollideCircle(ref to, PlayerCfg.radius);
                    }
                    Events.Add(new SimEvent { Kind = EventKind.Teleport, Pos = p.Pos, To = to, Color = 1 });
                    p.Hidden = 0.1f; p.HiddenTo = to;
                    jumpSource = Source(s, 220f); jumpSource.FromJump = true;
                    p.Cast = null;
                    break;
                }
                case CardId.Dash:
                case CardId.BlazingSprint:
                case CardId.ShockingArrival:
                {
                    var to = TravelTarget(p.Pos, dir, 2.52f * 1.4f);
                    p.Dashing = true; p.DashFrom = p.Pos; p.DashTo = to; p.DashT = 0f; p.DashDur = card.Recovery;
                    dashSlot = s;
                    float dmg = card.Id == CardId.Dash ? 125f : 25f;
                    var src = Source(s, dmg);
                    float r = 1f;
                    if (card.Id == CardId.ShockingArrival) { src.OwnDot = DotKind.Hex; src.OwnDotDamage = 500f; r = 1.1f; }
                    Add(new Effect { Kind = EffectKind.DashHit, Pos = p.Pos, Life = 0.3f, R = r, Src = src });
                    if (card.Id == CardId.BlazingSprint)
                    {
                        var trail = Add(new Effect { Kind = EffectKind.FireTrail, Life = 3f, R = 0.6f, Damage = 450f, Dot = DotKind.Lava, Src = Source(s, 0f) });
                        trail.Points.Add(p.Pos); trail.PointAge.Add(0f);
                        trail.Points.Add(to); trail.PointAge.Add(0f);
                    }
                    p.Cast = null;
                    break;
                }
                case CardId.JumpScare:
                case CardId.EscapeArtist:
                {
                    var from = p.Pos;
                    var to = TravelTarget(p.Pos, dir, 4f);
                    Events.Add(new SimEvent { Kind = EventKind.Teleport, Pos = from, To = to, Color = 5 });
                    p.Pos = to;
                    if (card.Id == CardId.JumpScare)
                        Blast(to, 1.85f * 1.15f, Source(s, 175f), 5);
                    else
                        Add(new Effect { Kind = EffectKind.Cloud, Pos = from, Life = 3f, R = 2.5f, Damage = 325f, Src = Source(s, 0f) });
                    break;
                }
            }
        }

        HitSource jumpSource;
        Slot dashSlot;

        void ArriveJump()
        {
            var p = Player;
            p.Pos = p.HiddenTo;
            var src = jumpSource;
            if (src == null) return;
            int killsBefore = Kills;
            Blast(p.Pos, 1.875f, src, 1);
            // SkillEventResetCooldown: убийство сбрасывает перезарядку
            if (Kills > killsBefore && src.Slot != null) { src.Slot.Charges = src.Card.Charges; src.Slot.OnCd = false; src.Slot.CdTimer = 0f; }
            jumpSource = null;
            p.Cast = new Cast { Slot = System.Array.IndexOf(Slots, src.Slot), Phase = CastPhase.Recovery, T = src.Card.Recovery, Fired = true };
            if (p.Cast.Slot < 0) p.Cast = null;
        }

        void OnDashEnd()
        {
            var s = dashSlot;
            dashSlot = null;
            if (s?.Main == CardId.ShockingArrival)
            {
                var src = Source(s, 125f); src.OwnDot = DotKind.Hex; src.OwnDotDamage = 500f;
                Blast(Player.Pos, 2f * 1.1f, src, 2);
            }
        }

        void BreakShield()
        {
            var p = Player;
            p.Shield = 0f;
            foreach (var f in Effects)
                if (f.Kind == EffectKind.AegisShield && !f.Done)
                {
                    f.Done = true;
                    Blast(p.Pos, 2.5f, f.Src, 1);
                    Events.Add(new SimEvent { Kind = EventKind.ShieldBreak, Pos = p.Pos });
                }
        }

        /// <summary>Blazing Shoes: 5 с за Ace тянется огонь.</summary>
        void UpdateShoes(float dt)
        {
            var p = Player;
            if (p.Shoes <= 0f) { p.ShoeTrail = null; return; }
            if (p.ShoeTrail == null || p.ShoeTrail.Done)
            {
                p.ShoeTrail = Add(new Effect { Kind = EffectKind.FireTrail, Life = 999f, R = 0.6f, Damage = 585f, Dot = DotKind.Lava, Src = new HitSource { Div = 1f } });
                p.ShoeTrail.Points.Add(p.Pos); p.ShoeTrail.PointAge.Add(0f);
            }
            var pts = p.ShoeTrail.Points;
            if ((pts[pts.Count - 1] - p.Pos).sqrMagnitude > 0.25f) { pts.Add(p.Pos); p.ShoeTrail.PointAge.Add(0f); }
        }

        Effect Add(Effect f)
        {
            f.Id = NextId();
            f.Start = f.Pos;
            Effects.Add(f);
            return f;
        }

        static Vector2 Rotate(Vector2 v, float a)
        {
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }
    }
}
