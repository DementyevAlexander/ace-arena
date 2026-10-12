using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    public sealed partial class ArenaSim
    {
        /// <summary>Удар картой: базовый урон + бонус улучшения по условию, собственный эффект карты, эффект улучшения.</summary>
        public void HitEnemy(Enemy e, HitSource src)
        {
            if (!e.Alive || e.SpawnInvuln > 0f) return;
            var up = src.Up;
            float extra = 0f;
            if (up.IsBonus)
            {
                bool ok = up.Kind switch
                {
                    UpgradeKind.BonusVsStun => e.Stun > 0f,
                    UpgradeKind.BonusVsFear => e.Fear > 0f,
                    _ => e.UnderCc,
                };
                if (ok) extra = Mathf.Round(up.Value / src.Div);
            }
            if (src.Base + extra > 0f) Damage(e, src.Base + extra, extra > 0f);
            if (e.Dead) { if (src.UpgradeApplies) ApplyOnKillUpgrades(e, src); return; }

            if (src.OwnDotDamage > 0f) AddDot(e, src.OwnDot, src.OwnDotDamage);
            if (src.Card != null && src.OwnCcApplies)
            {
                // Stun/Fear из улучшения заменяют собственный контроль главной карты (ReplacesCCEffects)
                bool replaced = up.Kind is UpgradeKind.Stun or UpgradeKind.Fear;
                if (src.Card.OwnCc != CcKind.None && !replaced) ApplyCc(e, src.Card.OwnCc, src.Card.CcDuration);
            }
            if (src.PushSpeed > 0f) Knock(e, src.PushSpeed);
            if (src.UpgradeApplies) ApplyUpgrade(e, src);
        }

        void ApplyOnKillUpgrades(Enemy e, HitSource src)
        {
            var up = src.Up;
            if (up.Kind == UpgradeKind.DeathBleed || up.Kind == UpgradeKind.DeathPoison) SpreadDeath(e, up.Kind);
        }

        void ApplyUpgrade(Enemy e, HitSource src)
        {
            var up = src.Up;
            var s = src.Slot;
            switch (up.Kind)
            {
                case UpgradeKind.Bleed: AddDot(e, DotKind.Bleed, Mathf.Round(up.Value / src.Div)); break;
                case UpgradeKind.Burn: AddDot(e, DotKind.Burn, Mathf.Round(up.Value / src.Div)); break;
                case UpgradeKind.Hex: AddDot(e, DotKind.Hex, Mathf.Round(up.Value / src.Div)); break;
                case UpgradeKind.DeathBleed: e.MarkBleed = 5f; break;
                case UpgradeKind.DeathPoison: e.MarkPoison = 5f; break;
                case UpgradeKind.Stun: ApplyCc(e, CcKind.Stun, up.Duration); break;
                case UpgradeKind.Fear: ApplyCc(e, CcKind.Fear, up.Duration); break;
                case UpgradeKind.Pull: e.PullAce = 0.35f; break;
                case UpgradeKind.Push: Knock(e, 9f); break;
                case UpgradeKind.WildCards:
                    if (s != null && s.SpawnCd > 0f) break;
                    if (s != null) s.SpawnCd = Upgrades.SpawnCooldown;
                    for (int k = 0; k < 5; k++)
                    {
                        var d = Rotate(Vector2.right, k * Mathf.PI * 2f / 5f + Time);
                        Add(new Effect { Kind = EffectKind.WildCard, Pos = e.Pos, Dir = d, Life = 0.35f, Speed = 8f, R = 0.3f, Exclude = e,
                            Src = new HitSource { Slot = s, Base = up.Value, Div = 1f, UpgradeApplies = false } });
                    }
                    break;
                case UpgradeKind.NoxiousCloud:
                    if (s != null && s.SpawnCd > 0f) break;
                    if (s != null) s.SpawnCd = Upgrades.SpawnCooldown;
                    Add(new Effect { Kind = EffectKind.Cloud, Pos = e.Pos, Life = up.Duration, R = 1.5f, Damage = up.Value, Src = new HitSource { Div = 1f } });
                    break;
                case UpgradeKind.GuidedLight:
                    if (s != null && s.SpawnCd > 0f) break;
                    if (s != null) s.SpawnCd = Upgrades.SpawnCooldown;
                    Add(new Effect { Kind = EffectKind.Tether, Target = e, Life = up.Duration, R = 5f, Damage = up.Value, Tick = 0.2f });
                    break;
            }
        }

        public void ApplyCc(Enemy e, CcKind kind, float duration)
        {
            if (kind == CcKind.Stun) e.Stun = Mathf.Max(e.Stun, duration);
            else if (kind == CcKind.Fear) e.Fear = Mathf.Max(e.Fear, duration);
            InterruptAttack(e);
        }

        /// <summary>Контроль срывает замах: обозначение исчезает, удар не наступает.</summary>
        void InterruptAttack(Enemy e)
        {
            if (e.State is AiState.Windup or AiState.Strike)
            {
                e.Tele = null;
                e.State = AiState.Chase;
                e.Cd = Mathf.Max(e.Cd, 0.6f);
            }
        }

        void Knock(Enemy e, float speed)
        {
            var d = e.Pos - Player.Pos;
            e.KnockVel = (d.sqrMagnitude > 0.0001f ? d.normalized : Vector2.up) * speed;
            e.KnockT = 0.25f;
        }

        public void AddDot(Enemy e, DotKind kind, float total)
        {
            if (e.Dead || total <= 0f) return;
            DotTable.Get(kind, out float time, out float freq);
            int ticks = Mathf.Max(1, Mathf.RoundToInt(time / freq));
            e.Dots.Add(new Dot { Kind = kind, Tick = total / ticks, Freq = freq, Left = ticks, T = freq });
        }

        void UpdateDots(Enemy e, float dt)
        {
            for (int i = 0; i < e.Dots.Count; i++)
            {
                var d = e.Dots[i];
                // Hex бьёт, только пока враг двигается
                if (d.Kind == DotKind.Hex && e.Moved < 0.002f) continue;
                d.T -= dt;
                if (d.T <= 0f && d.Left > 0)
                {
                    d.T += d.Freq; d.Left--;
                    Damage(e, d.Tick, false, true, d.Kind);
                    if (e.Dead) return;
                }
            }
            e.Dots.RemoveAll(d => d.Left <= 0);
        }

        public void Damage(Enemy e, float amount, bool crit, bool dot = false, DotKind dotKind = DotKind.Bleed)
        {
            if (e.Dead) return;
            e.Hp -= amount;
            e.Flash = 0.12f;
            Events.Add(new SimEvent { Kind = EventKind.EnemyHit, Pos = e.Pos, Value = amount, Crit = crit, Enemy = e, Color = dot ? 10 + (int)dotKind : -1 });
            if (e.Hp <= 0f) Kill(e);
        }

        void Kill(Enemy e)
        {
            e.Dead = true;
            e.DeathT = 0.8f;
            e.Tele = null;
            Kills++;
            if (Player.Lock == e) Player.Lock = null;
            Events.Add(new SimEvent { Kind = EventKind.EnemyDied, Pos = e.Pos, Enemy = e });
            if (e.MarkBleed > 0f) SpreadDeath(e, UpgradeKind.DeathBleed);
            if (e.MarkPoison > 0f) SpreadDeath(e, UpgradeKind.DeathPoison);
        }

        void SpreadDeath(Enemy dead, UpgradeKind kind)
        {
            var dot = kind == UpgradeKind.DeathBleed ? DotKind.Bleed : DotKind.Poison;
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = dead.Pos, Radius = 2.5f, Color = kind == UpgradeKind.DeathBleed ? 0 : 4 });
            foreach (var o in Enemies)
                if (o != dead && o.Alive && (o.Pos - dead.Pos).magnitude <= 2.5f) AddDot(o, dot, Upgrades.UpgradeDot);
        }

        /// <summary>Круг урона с центром в точке; каждый враг — один раз.</summary>
        void Blast(Vector2 center, float radius, HitSource src, int color)
        {
            for (int i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                if (e.Alive && (e.Pos - center).magnitude <= radius + e.R) HitEnemy(e, src);
            }
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = center, Radius = radius, Color = color });
        }

        /// <summary>Удар по Смерти. Возвращает true, если урон прошёл. knock — короткий отброс от удара.</summary>
        public bool HurtPlayer(float dmg, Vector2 from, bool knock = true)
        {
            var p = Player;
            if (GameOver) return false;
            if (p.Invulnerable) return false;
            if (p.Shield > 0f)
            {
                // Aegis Cloak: щит принимает удар и взрывается
                Events.Add(new SimEvent { Kind = EventKind.PlayerBlocked, Pos = p.Pos });
                BreakShield();
                return false;
            }
            p.Hp -= dmg;
            p.HitInvuln = PlayerCfg.hitInvulnerability;
            if (knock)
            {
                var k = p.Pos - from;
                p.KnockVel = (k.sqrMagnitude > 0.0001f ? k.normalized : Vector2.down) * 6f;
                p.KnockT = 0.12f;
            }
            if (p.Cast != null && Slots[p.Cast.Slot].Card.Interruptible && p.Cast.Phase != CastPhase.Recovery) p.Cast = null;
            Events.Add(new SimEvent { Kind = EventKind.PlayerHit, Pos = p.Pos, Value = dmg });
            if (p.Hp <= 0f) { p.Hp = 0f; GameOver = true; }
            return true;
        }
    }
}
