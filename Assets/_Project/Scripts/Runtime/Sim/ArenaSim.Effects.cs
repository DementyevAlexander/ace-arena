using UnityEngine;

namespace Arena.Sim
{
    public sealed partial class ArenaSim
    {
        void UpdateEffects(float dt)
        {
            for (int i = 0; i < Effects.Count; i++)
            {
                var f = Effects[i];
                if (f.Done) continue;
                f.Age += dt;
                switch (f.Kind)
                {
                    case EffectKind.Dagger:
                        if (f.T > 0f) { f.T -= dt; break; } // кинжалы висят 0.4 с, потом летят
                        Fly(f, dt, pierce: true);
                        break;
                    case EffectKind.FiveCard:
                    case EffectKind.Rose:
                        if (f.T > 0f) { f.T -= dt; break; }
                        Fly(f, dt, pierce: false);
                        break;
                    case EffectKind.WildCard:
                        Fly(f, dt, pierce: false);
                        break;
                    case EffectKind.Missile:
                        UpdateMissile(f, dt);
                        break;
                    case EffectKind.Bat:
                        UpdateBat(f, dt);
                        break;
                    case EffectKind.BlackHole:
                        UpdateBlackHole(f, dt);
                        break;
                    case EffectKind.DashHit:
                        f.Life -= dt;
                        f.Pos = Player.Pos;
                        HitRoomObjects(f.Pos, f.R * 0.5f);
                        foreach (var e in Enemies)
                            if (e.Alive && !f.Hit.Contains(e) && (e.Pos - f.Pos).magnitude < f.R + e.R) { f.Hit.Add(e); HitEnemy(e, f.Src); }
                        if (f.Life <= 0f) f.Done = true;
                        break;
                    case EffectKind.FireTrail:
                        UpdateTrail(f, dt);
                        break;
                    case EffectKind.Cloud:
                        UpdateCloud(f, dt);
                        break;
                    case EffectKind.Tether:
                        UpdateTether(f, dt);
                        break;
                    case EffectKind.AegisShield:
                        f.Pos = Player.Pos;
                        if (Player.Shield <= 0f) f.Done = true;
                        break;
                    default:
                        f.Life -= dt;
                        if (f.Life <= 0f) f.Done = true;
                        break;
                }
            }
        }

        void Fly(Effect f, float dt, bool pierce)
        {
            f.Life -= dt;
            f.Pos += f.Dir * (f.Speed * dt);
            if (f.Life <= 0f || !InsideRoom(f.Pos, 0f) || HitsObstacle(f.Pos, f.R * 0.5f)) { HitRoomObjects(f.Pos, f.R); f.Done = true; return; }
            HitRoomObjects(f.Pos, f.R);
            foreach (var e in Enemies)
            {
                if (!e.Alive || e == f.Exclude || f.Hit.Contains(e)) continue;
                if ((e.Pos - f.Pos).magnitude >= f.R + e.R) continue;
                f.Hit.Add(e);
                HitEnemy(e, f.Src);
                if (!pierce) { f.Done = true; return; }
            }
        }

        void UpdateMissile(Effect f, float dt)
        {
            f.Life -= dt;
            f.Pos += f.Dir * (f.Speed * dt);
            bool boom = f.Life <= 0f || !InsideRoom(f.Pos, 0.2f) || HitsObstacle(f.Pos, f.R);
            foreach (var e in Enemies) if (e.Alive && (e.Pos - f.Pos).magnitude < f.R + e.R) boom = true;
            if (!boom) return;
            HitRoomObjects(f.Pos, 0.9f);
            // Чем дольше держали, тем больше взрыв (оценка радиуса: 0.9…1.5)
            Blast(f.Pos, 0.9f * (1f + 0.7f * f.Hold), f.Src, 3);
            f.Done = true;
        }

        /// <summary>Dio: вылет 0.15 с на 40 юнит/с, висит до 1.5 с, затем (или по повторному нажатию) возвращается к Ace.</summary>
        void UpdateBat(Effect f, float dt)
        {
            if (f.Phase == 0)
            {
                f.T -= dt;
                f.Pos += f.Dir * (f.Speed * dt);
                CollideCircle(ref f.Pos, 0.2f);
                if (f.T <= 0f) f.Phase = 1;
            }
            else if (f.Phase == 1)
            {
                if (f.Age >= f.Life) { f.Phase = 2; f.Hit.Clear(); }
            }
            else
            {
                var d = Player.Pos - f.Pos;
                float step = f.Speed * dt;
                if (d.magnitude <= step + 0.3f)
                {
                    f.Done = true;
                    if (f.Slot != null && f.Slot.Bat == f) f.Slot.Bat = null;
                    return;
                }
                f.Pos += d.normalized * step;
            }
            HitRoomObjects(f.Pos, f.R * 0.5f);
            foreach (var e in Enemies)
                if (e.Alive && !f.Hit.Contains(e) && (e.Pos - f.Pos).magnitude < f.R + e.R) { f.Hit.Add(e); HitEnemy(e, f.Src); }
        }

        void UpdateBlackHole(Effect f, float dt)
        {
            f.Life -= dt;
            f.Tick -= dt;
            HitSource tickSrc = null;
            if (f.Tick <= 0f)
            {
                f.Tick += 0.3f;
                var s = f.Src;
                tickSrc = new HitSource { Slot = s.Slot, Card = s.Card, Up = s.Up, Base = s.Base, Div = s.Div, UpgradeApplies = false };
            }
            foreach (var e in Enemies)
            {
                if (!e.Alive || (e.Pos - f.Pos).magnitude > f.R + e.R) continue;
                e.Pull = 0.1f; e.PullTo = f.Pos;
                // Эффект улучшения — при первом касании, дальше только урон каждые 0.3 с (бонус урона считается на каждом тике)
                if (f.Hit.Add(e)) ApplyUpgrade(e, f.Src);
                if (tickSrc != null) HitEnemy(e, tickSrc);
            }
            if (f.Life <= 0f) f.Done = true;
        }

        void UpdateTrail(Effect f, float dt)
        {
            var pts = f.Points;
            for (int i = 0; i < f.PointAge.Count; i++) f.PointAge[i] += dt;
            bool growing = f == Player.ShoeTrail && Player.Shoes > 0f;
            while (pts.Count > 0 && f.PointAge[0] > 3f) { pts.RemoveAt(0); f.PointAge.RemoveAt(0); }
            if (!growing) f.Life -= dt;
            if (pts.Count == 0 || f.Life <= 0f) { f.Done = true; return; }
            // Огонь поджигает каждого врага один раз за 0.5 с
            f.Tick -= dt;
            if (f.Tick > 0f) return;
            f.Tick = 0.1f;
            foreach (var e in Enemies)
            {
                if (!e.Alive) continue;
                bool inside = false;
                for (int i = 0; i < pts.Count && !inside; i++)
                {
                    var a = pts[i];
                    var b = i + 1 < pts.Count ? pts[i + 1] : a;
                    if (DistToSegment(e.Pos, a, b) < f.R + e.R) inside = true;
                }
                if (!inside) continue;
                bool recent = false;
                foreach (var d in e.Dots) if (d.Kind == DotKind.Lava && d.Left > 17) recent = true;
                if (!recent) AddDot(e, DotKind.Lava, f.Damage);
            }
        }

        void UpdateCloud(Effect f, float dt)
        {
            f.Life -= dt;
            f.Tick -= dt;
            if (f.Tick <= 0f)
            {
                f.Tick += 0.3f;
                float duration = f.Src.Card == null ? 2f : 3f;
                float perTick = f.Damage / (duration / 0.3f);
                foreach (var e in Enemies)
                    if (e.Alive && (e.Pos - f.Pos).magnitude <= f.R + e.R) Damage(e, perTick, false, true, DotKind.Noxious);
            }
            if (f.Life <= 0f) f.Done = true;
        }

        void UpdateTether(Effect f, float dt)
        {
            f.Life -= dt;
            var e = f.Target;
            if (e == null || !e.Alive || (e.Pos - Player.Pos).magnitude > f.R || f.Life <= 0f) { f.Done = true; return; }
            f.Pos = e.Pos;
            f.T -= dt;
            if (f.T <= 0f)
            {
                f.T += f.Tick;
                Damage(e, f.Damage * f.Tick / 2f, false, true, DotKind.Light);
            }
        }

        static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude < 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }
    }
}
