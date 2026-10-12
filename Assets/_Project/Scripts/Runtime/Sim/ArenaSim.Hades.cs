using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Враги Hades — Wretched Thug и Wretched Witch, их снаряды, ловушки и опасный пол комнат.
    /// Телеграф как в Hades: зоны на земле нет — враг замирает, поворачивается к Ace, в конце замаха белеет и дрожит
    /// (PreAttackEnd: последние min(0.5 с, замах)). Направление удара фиксируется в начале замаха (AITrackTargetDuringCharge = false).</summary>
    public sealed partial class ArenaSim
    {
        // ---------- Агрессия (AggroAI) ----------

        /// <summary>Враг замечает Ace: удар по нему, Ace в радиусе агрессии, последний живой (AggroIfLastAlive).</summary>
        void Aggro(Enemy e)
        {
            if (e.Aggro) return;
            e.Aggro = true;
            if (e.State == AiState.Wander) { e.State = e.Kind == EnemyKind.Witch ? AiState.Cover : AiState.Chase; e.T = 0f; }
            Events.Add(new SimEvent { Kind = EventKind.EnemyAlert, Pos = e.Pos, Enemy = e });
            e.Alert = 0.6f;
        }

        /// <summary>Пока не заметил: бродит в квадрате ±AIWanderDistance вокруг точки появления, раз в ≤1.5 с смотрит, близко ли Ace.</summary>
        void HadesIdle(Enemy e, float dt, float aggroRange, float speed, float wander)
        {
            if (e.AggroT > 0f)
            {
                e.AggroT -= dt;
                if (e.AggroT <= 0f && ((Player.Pos - e.Pos).magnitude < aggroRange || AliveCount() == 1 || e.Aggro)) { e.Aggro = false; Aggro(e); }
                return;
            }
            e.T -= dt;
            if (e.T <= 0f)
            {
                e.T = Rand(0.5f, 1.5f);
                if ((Player.Pos - e.Pos).magnitude < aggroRange || AliveCount() == 1) { e.AggroT = Rand(0.05f, 0.2f); return; }
                e.CoverPos = e.Home + new Vector2(Rand(-wander, wander), Rand(-wander, wander));
            }
            var d = e.CoverPos - e.Pos;
            if (wander > 0f && d.magnitude > 0.05f)
            {
                e.Face = d.normalized;
                e.Pos += e.Face * (Mathf.Min(speed * 0.5f, d.magnitude / dt) * dt);
            }
        }

        int AliveCount()
        {
            int n = 0;
            foreach (var e in Enemies) if (!e.Dead) n++;
            return n;
        }

        /// <summary>Начать замах: враг останавливается и смотрит на Ace; последние min(0.5, замах) с — белая вспышка и дрожь.</summary>
        void BeginWindup(Enemy e, float preAttack)
        {
            var to = Player.Pos - e.Pos;
            e.Dir = to.sqrMagnitude > 0.0001f ? to.normalized : e.Face;
            e.Face = e.Dir;
            e.State = AiState.Windup;
            e.T = preAttack;
            e.WarnTotal = Mathf.Min(0.5f, preAttack);
            e.WarnT = 0f;
            e.HitDone = false;
            Events.Add(new SimEvent { Kind = EventKind.TelegraphStart, Pos = e.Pos, Enemy = e });
        }

        void TickWindup(Enemy e, float dt)
        {
            e.Face = e.Dir;
            e.T -= dt;
            e.WarnT = e.T <= e.WarnTotal ? Mathf.Clamp01(1f - e.T / Mathf.Max(0.0001f, e.WarnTotal)) : 0f;
        }

        // ---------- Wretched Thug ----------

        void UpdateThug(Enemy e, float dt)
        {
            var c = ThugCfg;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            switch (e.State)
            {
                case AiState.Wander:
                    HadesIdle(e, dt, e.PreSpawned && Wave <= 1 ? Hades.U(800f) : c.aggroRange, c.speed, e.PreSpawned && Wave <= 1 ? 0f : c.wanderDistance);
                    if (e.Aggro) e.State = AiState.Chase;
                    break;
                case AiState.Chase:
                    // AttackerAI: подойти на AIAttackDistance, затем AttackOnce
                    e.Face = toPlayer.normalized;
                    if (dist > c.attackDistance + e.R) e.Pos += Steer(e, toPlayer.normalized) * (c.speed * dt);
                    else if (Player.Hidden <= 0f) BeginWindup(e, c.preAttack);
                    break;
                case AiState.Windup:
                    TickWindup(e, dt);
                    if (e.T <= 0f) { e.State = AiState.PreFire; e.T = c.preFire; e.WarnT = 1f; }
                    break;
                case AiState.PreFire:
                    e.T -= dt;
                    if (e.T <= 0f)
                    {
                        // Выпад SelfVelocity 1000 с торможением 4800 (~104 ед.) и трапеция удара на 0.18 с
                        e.State = AiState.Strike; e.T = c.fire; e.WarnT = 0f;
                        e.ExtVel = e.Dir * c.lungeSpeed; e.Grip = c.gripDuringLunge;
                        Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos + e.Dir * ((c.hitFront + c.hitBack) * 0.5f), To = e.Dir, Enemy = e, Radius = c.hitFront });
                    }
                    break;
                case AiState.Strike:
                    e.T -= dt;
                    if (!e.HitDone && c.fire - e.T <= c.hitActive && Geo.Overlaps(ThugHitShape(e), Player.Pos, PlayerCfg.radius))
                    {
                        e.HitDone = true;
                        HurtPlayer(c.damage, e.Pos, HitStop.HurtHeavy, c.knockback, c.stunHero, 0.05f, c.knockbackGripTime);
                    }
                    if (e.T <= 0f) { e.State = AiState.Recover; e.T = c.postAttack; }
                    break;
                case AiState.Recover:
                    e.T -= dt;
                    if (e.T <= 0f) e.State = AiState.Chase;
                    break;
                default:
                    e.State = AiState.Wander;
                    break;
            }
        }

        /// <summary>Хитбокс удара Thug: трапеция перед ним (EnemyProjectiles.sjson:410, точки 310/±80 и −10/±40), повёрнута по удару.</summary>
        public Vector2[] ThugHitShape(Enemy e)
        {
            var c = ThugCfg;
            var f = e.Dir;
            var s = new Vector2(-f.y, f.x);
            return new[]
            {
                e.Pos + f * c.hitFront + s * c.hitFrontHalf,
                e.Pos + f * c.hitBack + s * c.hitBackHalf,
                e.Pos + f * c.hitBack - s * c.hitBackHalf,
                e.Pos + f * c.hitFront - s * c.hitFrontHalf,
            };
        }

        // ---------- Wretched Witch ----------

        void UpdateWitch(Enemy e, float dt)
        {
            var c = WitchCfg;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            switch (e.State)
            {
                case AiState.Wander:
                    HadesIdle(e, dt, c.aggroRange, c.speed, Hades.U(100f));
                    if (e.Aggro) { e.State = AiState.Cover; e.T = 0f; }
                    break;
                case AiState.Cover:
                    // HideAndPeekAI: встать за ближайшее укрытие (в 150 от него, со стороны от Ace) и ждать 2.3 с
                    if (e.T <= 0f) { e.CoverPos = FindCover(e); e.T = c.takeCover + 3f; }
                    e.T -= dt;
                    var d = e.CoverPos - e.Pos;
                    if (d.magnitude > 0.1f && e.T > c.takeCover) { e.Face = d.normalized; e.Pos += Steer(e, d.normalized) * (Mathf.Min(c.speed, d.magnitude / dt) * dt); }
                    else if (e.T > c.takeCover) e.T = c.takeCover;
                    if (e.T <= 0f) { e.State = AiState.Peek; e.T = Rand(c.moveTimeoutMin, c.moveTimeoutMax); }
                    break;
                case AiState.Peek:
                    // выйти, пока Ace не окажется в 1200 и на линии огня, не дольше 2–4 с
                    e.T -= dt;
                    bool los = LineOfSight(e.Pos, Player.Pos, c.losBuffer);
                    if (dist <= c.attackDistance && los && Player.Hidden <= 0f) { BeginWindup(e, c.preAttack); break; }
                    if (e.T <= 0f) { e.State = AiState.Cover; e.T = 0f; break; }
                    e.Face = toPlayer.normalized;
                    e.Pos += Steer(e, toPlayer.normalized) * (c.speed * dt);
                    break;
                case AiState.Windup:
                    TickWindup(e, dt);
                    if (e.T <= 0f)
                    {
                        e.WarnT = 0f;
                        Bolts.Add(new Bolt
                        {
                            Pos = e.Pos + e.Dir * c.barrel, Dir = e.Dir, Speed = c.boltSpeed, Range = c.boltRange, R = c.boltRadius,
                            Damage = c.damage, Stun = c.stunHero, Stop = HitStop.HurtLight,
                        });
                        Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos + e.Dir * c.barrel, To = e.Dir, Enemy = e, Radius = 0.3f });
                        e.State = AiState.Recover; e.T = c.postAttack;
                    }
                    break;
                case AiState.Recover:
                    e.T -= dt;
                    if (e.T <= 0f) { e.State = AiState.Cover; e.T = 0f; }
                    break;
                default:
                    e.State = AiState.Wander;
                    break;
            }
        }

        /// <summary>Точка за ближайшим препятствием относительно Ace. Без препятствий — остаётся на месте.</summary>
        Vector2 FindCover(Enemy e)
        {
            Obstacle best = null;
            float bestD = Hades.U(1500f);
            foreach (var o in Obstacles)
            {
                float d = (o.Pos - e.Pos).magnitude - o.R;
                if (d < bestD && o.R < 4f) { bestD = d; best = o; }
            }
            if (best == null) return e.Pos;
            var away = best.Pos - Player.Pos;
            away = away.sqrMagnitude > 0.0001f ? away.normalized : Vector2.up;
            var p = best.Pos + away * (best.R + WitchCfg.coverHug + e.R);
            CollideCircle(ref p, e.R);
            return p;
        }

        // ---------- Снаряды врагов ----------

        void UpdateBolts(float dt)
        {
            foreach (var b in Bolts)
            {
                if (b.Done) continue;
                float step = b.Speed * dt;
                b.Pos += b.Dir * step;
                b.Travelled += step;
                if (b.Travelled >= b.Range || !InsideRoom(b.Pos, b.R * 0.5f)) { b.Done = true; continue; }
                var p = Player;
                if (p.Hidden <= 0f && !b.Hit.Contains(p) && (p.Pos - b.Pos).magnitude < b.R + PlayerCfg.radius)
                {
                    b.Hit.Add(p);
                    if (!b.Pierce) b.Done = true;
                    if (HurtPlayer(b.Damage, b.Pos - b.Dir, b.Stop, b.Knock, b.Stun)) Events.Add(new SimEvent { Kind = EventKind.BoltHit, Pos = b.Pos });
                    if (b.Done) continue;
                }
                if (!b.FromTrap) continue;
                foreach (var e in Enemies)
                {
                    if (!e.Alive || e.SpawnInvuln > 0f || b.Hit.Contains(e) || (e.Pos - b.Pos).magnitude >= b.R + e.R) continue;
                    b.Hit.Add(e);
                    Damage(e, b.EnemyDamage, false);
                    if (!b.Pierce) { b.Done = true; break; }
                }
            }
        }

        // ---------- Опасный пол ----------

        void UpdateHazards(float dt)
        {
            foreach (var h in Hazards)
            {
                h.Tick -= dt;
                if (h.Tick > 0f) continue;
                h.Tick = 0.5f;
                var p = Player;
                if (p.Hidden <= 0f && !p.Dashing && Geo.InPoly(h.Poly, p.Pos)) HurtPlayer(h.Damage * Hades.EnemyDamageScale, p.Pos, HitStop.None, 0f);
                foreach (var e in Enemies)
                    if (e.Alive && Geo.InPoly(h.Poly, e.Pos)) Damage(e, h.Damage * Hades.EnemyHpScale, false, true, DotKind.Lava);
            }
        }
    }
}
