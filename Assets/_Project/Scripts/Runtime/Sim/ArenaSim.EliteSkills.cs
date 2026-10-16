using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Элитки (мини-боссы этажа): у каждой — два случайных скилла из вражеских версий форм игрока, каждый со своей
    /// перезарядкой (ArenaTuning.eliteSkillCd). Скилл начинается, когда Смерть ближе eliteSkillRange и элитка не замахивается
    /// своей атакой: красный телеграф eliteCastTime (элитка стоит), потом применение.
    /// Залп — веер самонаводящихся снарядов Casket; Круг — красная зона под Смертью: бьёт и замедляет, пока стоишь;
    /// Дух — рядом с элиткой, стреляет в Смерть; Щит — поглощает удары (горение и прочий урон со временем проходят);
    /// Рывок — по линии к Смерти, задевает её.</summary>
    public sealed partial class ArenaSim
    {
        /// <summary>Выдать элитке два разных скилла; первые применит через 1.5–3 с.</summary>
        void GiveEliteSkills(Enemy e)
        {
            var all = (EliteSkill[])System.Enum.GetValues(typeof(EliteSkill));
            int a = rng.Next(all.Length), b = (a + 1 + rng.Next(all.Length - 1)) % all.Length;
            e.Skills = new[] { all[a], all[b] };
            e.SkillCd = new[] { Rand(1.5f, 3f), Rand(3f, 5f) };
            e.Casting = -1;
        }

        /// <summary>Задать элитке скиллы (проверки, Arena → Spawn): готовы через 0.1 с.</summary>
        public void SetEliteSkills(Enemy e, params EliteSkill[] skills)
        {
            e.Skills = skills;
            e.SkillCd = new float[skills.Length];
            for (int i = 0; i < skills.Length; i++) e.SkillCd[i] = 0.1f;
            e.Casting = -1;
        }

        /// <summary>Шаг скиллов элитки. true — элитка занята (телеграф или рывок): своя атака и ходьба в этот кадр не идут.</summary>
        bool UpdateEliteSkills(Enemy e, float dt)
        {
            if (e.Skills == null) return false;
            var c = ArenaCfg;
            e.ShieldT -= dt;
            if (e.ShieldT <= 0f) e.Shield = 0;
            if (e.DashT > 0f)
            {
                // рывок: по прямой к точке, Смерть на пути — удар
                e.DashT -= dt;
                float k = Mathf.Clamp01(1f - e.DashT / c.eliteDashTime);
                e.Pos = Vector2.Lerp(e.DashFrom, e.CastAt, k);
                if (!e.DashHit && (Player.Pos - e.Pos).magnitude <= e.R + PlayerCfg.radius + 0.2f && HurtPlayer(c.eliteDashDamage, e.Pos))
                    e.DashHit = true;
                if (e.DashT <= 0f) e.Tele = null;
                return true;
            }
            if (e.Casting >= 0)
            {
                e.CastT -= dt;
                e.Vel = Vector2.zero;
                if (e.Tele != null) e.Tele.T = Mathf.Max(0f, e.CastT);
                if (e.CastT > 0f) return true;
                FireEliteSkill(e, e.Skills[e.Casting]);
                e.Casting = -1;
                if (e.DashT <= 0f) e.Tele = null;
                return e.DashT > 0f;
            }
            for (int i = 0; i < e.SkillCd.Length; i++) e.SkillCd[i] -= dt;
            bool busy = e.Tele != null || e.State is AiState.Windup or AiState.Strike or AiState.Lunge or AiState.PreFire or AiState.Spawn;
            if (busy || (Player.Pos - e.Pos).magnitude > c.eliteSkillRange || Player.Hidden > 0f) return false;
            for (int i = 0; i < e.Skills.Length; i++)
            {
                if (e.SkillCd[i] > 0f) continue;
                StartEliteSkill(e, i);
                return true;
            }
            return false;
        }

        void StartEliteSkill(Enemy e, int i)
        {
            var c = ArenaCfg;
            var skill = e.Skills[i];
            e.SkillCd[i] = c.eliteSkillCd[(int)skill];
            e.Casting = i;
            e.CastT = c.eliteCastTime;
            var to = Player.Pos - e.Pos;
            e.CastDir = to.sqrMagnitude > 0.0001f ? to.normalized : Vector2.up;
            e.Face = e.CastDir;
            float total = c.eliteCastTime;
            e.Tele = skill switch
            {
                EliteSkill.Zone => new Telegraph { Shape = TeleShape.Ellipse, Center = Player.Pos, Dir = Vector2.up, Forward = c.eliteZoneRadius, Side = c.eliteZoneRadius, Total = total, T = total },
                EliteSkill.Dash => new Telegraph { Shape = TeleShape.Lane, Center = e.Pos, Dir = e.CastDir, Forward = c.eliteDashRange, Side = e.R, Total = total, T = total },
                EliteSkill.Shot => new Telegraph { Shape = TeleShape.Fan, Center = e.Pos, Dir = e.CastDir, Forward = 3f, Angle = c.eliteShotSpread * (c.eliteShotCount - 1) * 0.5f + 6f, Total = total, T = total },
                _ => new Telegraph { Shape = TeleShape.Ellipse, Center = e.Pos, Dir = Vector2.up, Forward = e.R + 0.6f, Side = e.R + 0.6f, Total = total, T = total },
            };
            e.CastAt = skill == EliteSkill.Zone ? Player.Pos : e.Pos;
        }

        void FireEliteSkill(Enemy e, EliteSkill skill)
        {
            var c = ArenaCfg;
            Events.Add(new SimEvent { Kind = EventKind.EliteSkillCast, Pos = e.Pos, To = e.CastDir, Enemy = e, Value = (int)skill });
            switch (skill)
            {
                case EliteSkill.Shot:
                    for (int k = 0; k < c.eliteShotCount; k++)
                    {
                        float a = (k - (c.eliteShotCount - 1) * 0.5f) * c.eliteShotSpread * Mathf.Deg2Rad;
                        EliteBolt(e, e.Pos, Rotate(e.CastDir, a));
                    }
                    break;
                case EliteSkill.Zone:
                    Add(new Effect { Kind = EffectKind.EliteZone, Pos = e.CastAt, R = c.eliteZoneRadius, Life = c.eliteZoneLife, Target = e });
                    break;
                case EliteSkill.Summon:
                    Add(new Effect { Kind = EffectKind.EliteSpirit, Pos = e.Pos + Rotate(e.CastDir, 1.6f) * 1.2f, Life = c.eliteSpiritLife, T = 0.6f, Target = e, R = 0.35f });
                    break;
                case EliteSkill.Barrier:
                    e.Shield = c.eliteShieldHits;
                    e.ShieldT = c.eliteShieldTime;
                    Add(new Effect { Kind = EffectKind.EliteShield, Pos = e.Pos, Target = e, R = e.R + 0.45f, Life = c.eliteShieldTime });
                    break;
                case EliteSkill.Dash:
                    // по линии телеграфа до стены, не дальше eliteDashRange
                    e.DashFrom = e.Pos;
                    e.CastAt = TravelTarget(e.Pos, e.CastDir, c.eliteDashRange);
                    e.DashT = (e.CastAt - e.Pos).magnitude > 0.2f ? c.eliteDashTime : 0f;
                    e.DashHit = false;
                    break;
            }
        }

        /// <summary>Снаряд элитки — как у Casket: самонаводящийся, урон снаряда Casket.</summary>
        void EliteBolt(Enemy e, Vector2 from, Vector2 dir)
        {
            var c = CasketCfg;
            Add(new Effect
            {
                Kind = EffectKind.EnemyBolt, Pos = from + dir * (e != null ? e.R + 0.2f : 0.3f), Dir = dir, Speed = c.boltSpeed, Life = c.boltLife,
                R = c.boltRadius, Damage = c.damage, Target = e,
            });
        }

        /// <summary>Круг элитки: пока Смерть внутри — урон раз в eliteZoneTick и замедление.</summary>
        void UpdateEliteZone(Effect f, float dt)
        {
            var c = ArenaCfg;
            f.Life -= dt;
            if (f.Life <= 0f) { f.Done = true; return; }
            if ((Player.Pos - f.Pos).magnitude > f.R + PlayerCfg.radius * 0.5f) return;
            Player.Slow = Mathf.Max(Player.Slow, 0.1f);
            f.Tick -= dt;
            if (f.Tick > 0f) return;
            f.Tick += c.eliteZoneTick;
            HurtPlayer(c.eliteZoneDamage, f.Pos, HitStop.HurtSmall, 0f);
        }

        /// <summary>Дух элитки: держится рядом с ней и стреляет в Смерть; элитка умерла — исчезает.</summary>
        void UpdateEliteSpirit(Effect f, float dt)
        {
            var c = ArenaCfg;
            f.Life -= dt;
            var owner = f.Target;
            if (f.Life <= 0f || owner == null || !owner.Alive) { f.Done = true; Events.Add(new SimEvent { Kind = EventKind.Teleport, Pos = f.Pos, To = f.Pos, Color = 0 }); return; }
            f.Angle += 90f * dt;
            float a = f.Angle * Mathf.Deg2Rad;
            var home = owner.Pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (owner.R + 1.1f);
            f.Pos = Vector2.MoveTowards(f.Pos, home, 6f * dt);
            f.T -= dt;
            if (f.T > 0f) return;
            f.T = c.eliteSpiritFire;
            var to = Player.Pos - f.Pos;
            if (to.sqrMagnitude > 0.0001f && Player.Hidden <= 0f) EliteBolt(null, f.Pos, to.normalized);
        }

        void UpdateEliteShield(Effect f, float dt)
        {
            var e = f.Target;
            bool on = e != null && e.Alive && (e.Kind == EnemyKind.Megaera ? e.Invuln > 0f : e.Shield > 0 && e.ShieldT > 0f);
            if (!on) { f.Done = true; return; }
            f.Pos = e.Pos;
            f.Charges = e.Shield;
        }

        /// <summary>Щит элитки: удар (не урон со временем) поглощён. true — поглощён.</summary>
        bool EliteShieldAbsorb(Enemy e, bool dot)
        {
            if (dot || e.Shield <= 0 || e.ShieldT <= 0f) return false;
            e.Shield--;
            e.Flash = 0.05f;
            Events.Add(new SimEvent { Kind = EventKind.PlayerBlocked, Pos = e.Pos });
            return true;
        }
    }
}
