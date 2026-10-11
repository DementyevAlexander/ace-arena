using UnityEngine;

namespace Arena.Sim
{
    public sealed partial class ArenaSim
    {
        void UpdateEnemy(Enemy e, float dt)
        {
            if (e.Dead) { e.DeathT -= dt; return; }
            e.Flash -= dt; e.Alert -= dt;
            if (e.State == AiState.Spawn)
            {
                e.T -= dt;
                if (e.T <= 0f) { e.State = AiState.Wander; e.T = 0f; e.SpawnInvuln = ArenaCfg.spawnInvulnerability; }
                return;
            }
            e.SpawnInvuln -= dt; e.MarkBleed -= dt; e.MarkPoison -= dt;
            var before = e.Pos;
            UpdateDots(e, dt);
            if (e.Dead) return;
            e.Cd -= dt;

            if (!UpdateStatuses(e, dt))
            {
                if (e.Kind == EnemyKind.Ghost) UpdateGhost(e, dt);
                else UpdateStomper(e, dt);
            }
            CollideCircle(ref e.Pos, e.R);
            e.Moved = (e.Pos - before).magnitude;
        }

        /// <summary>Контроль и отбрасывание. true — враг в этом кадре не действует сам.</summary>
        bool UpdateStatuses(Enemy e, float dt)
        {
            var toPlayer = Player.Pos - e.Pos;
            if (e.KnockT > 0f) { e.KnockT -= dt; e.Pos += e.KnockVel * (dt * Mathf.Clamp01(e.KnockT / 0.25f + 0.3f)); return true; }
            if (e.Stun > 0f) { e.Stun -= dt; return true; }
            if (e.PullAce > 0f)
            {
                e.PullAce -= dt;
                if (toPlayer.magnitude > 0.9f) e.Pos += toPlayer.normalized * (7f * dt);
                return true;
            }
            if (e.Pull > 0f)
            {
                e.Pull -= dt;
                var d = e.PullTo - e.Pos;
                if (d.magnitude > 0.15f) e.Pos += d.normalized * (2.4f * dt);
                return true;
            }
            if (e.Fear > 0f)
            {
                e.Fear -= dt;
                float speed = e.Kind == EnemyKind.Ghost ? GhostCfg.fearSpeed : StomperCfg.fearSpeed;
                var away = -toPlayer.normalized;
                e.Pos += away * (speed * dt);
                e.Face = away;
                return true;
            }
            return false;
        }

        // ---------- Enraged Ghost: двойной удар по области ----------

        void UpdateGhost(Enemy e, float dt)
        {
            var c = GhostCfg;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            switch (e.State)
            {
                case AiState.Wander:
                    if (dist < c.noticeRange) { e.State = AiState.Chase; e.Alert = 0.6f; Events.Add(new SimEvent { Kind = EventKind.EnemyAlert, Pos = e.Pos, Enemy = e }); }
                    break;
                case AiState.Chase:
                    e.Face = toPlayer.normalized;
                    if (dist > 1.2f) e.Pos += Steer(e, toPlayer.normalized) * (c.chaseSpeed * dt);
                    if (dist <= c.attackRange && e.Cd <= 0f && Player.Hidden <= 0f) BeginGhostStrike(e, 1);
                    break;
                case AiState.Windup:
                    e.Face = e.Dir;
                    e.T -= dt;
                    e.Tele.T = e.T;
                    if (e.T <= 0f)
                    {
                        // Удар бьёт ровно обозначенную фигуру
                        if (e.Tele.Contains(Player.Pos, PlayerCfg.radius)) HurtPlayer(c.damage, e.Pos);
                        Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Tele.Center, To = e.Dir, Enemy = e, Radius = c.zoneSide });
                        e.Tele = null;
                        e.State = AiState.Strike; e.T = 0.12f;
                    }
                    break;
                case AiState.Strike:
                    e.T -= dt;
                    e.Pos += e.Dir * (c.lunge / 0.12f * dt);
                    if (e.T <= 0f) { e.State = AiState.Recover; e.T = e.Strike == 1 ? c.recovery1 : c.recovery2; }
                    break;
                case AiState.Recover:
                    e.T -= dt;
                    if (e.T <= 0f)
                    {
                        if (e.Strike == 1) BeginGhostStrike(e, 2);
                        else e.State = AiState.Chase;
                    }
                    break;
                default:
                    e.State = AiState.Chase;
                    break;
            }
        }

        void BeginGhostStrike(Enemy e, int index)
        {
            var c = GhostCfg;
            var to = Player.Pos - e.Pos;
            e.Dir = to.sqrMagnitude > 0.0001f ? to.normalized : e.Face;
            e.Strike = index;
            e.State = AiState.Windup;
            e.T = index == 1 ? c.windup1 : c.windup2;
            if (index == 1) e.Cd = c.cooldown;
            e.Tele = new Telegraph
            {
                Shape = TeleShape.Ellipse, Center = e.Pos + e.Dir * (c.lunge + c.zoneDistance), Dir = e.Dir,
                Forward = c.zoneForward, Side = c.zoneSide, Total = e.T, T = e.T,
            };
            Events.Add(new SimEvent { Kind = EventKind.TelegraphStart, Pos = e.Tele.Center, Enemy = e });
        }

        // ---------- Топотун: бродит, «!», погоня, выпад ----------

        void UpdateStomper(Enemy e, float dt)
        {
            var c = StomperCfg;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            switch (e.State)
            {
                case AiState.Wander:
                    if (dist < c.noticeRange && Player.Hidden <= 0f)
                    {
                        e.State = AiState.Alert; e.T = c.alertTime; e.Alert = 0.9f;
                        Events.Add(new SimEvent { Kind = EventKind.EnemyAlert, Pos = e.Pos, Enemy = e });
                        break;
                    }
                    e.T -= dt;
                    if (e.T <= 0f)
                    {
                        if (Rand(0f, 1f) < 0.35f) e.Dir = Vector2.zero;
                        else { float a = Rand(0f, Mathf.PI * 2f); e.Dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); }
                        e.T = Rand(0.8f, 2.2f);
                    }
                    if (e.Dir.sqrMagnitude > 0f) { e.Pos += e.Dir * (c.wanderSpeed * dt); e.Face = e.Dir; }
                    break;
                case AiState.Alert:
                    e.Face = toPlayer.normalized;
                    e.T -= dt;
                    if (e.T <= 0f) e.State = AiState.Chase;
                    break;
                case AiState.Chase:
                    if (dist > c.loseDistance) { e.State = AiState.Wander; e.T = 0.5f; break; }
                    e.Face = toPlayer.normalized;
                    e.Pos += Steer(e, toPlayer.normalized) * (c.chaseSpeed * dt);
                    if (dist < c.lungeDistance && e.Cd <= 0f && Player.Hidden <= 0f)
                    {
                        e.Dir = toPlayer.normalized;
                        e.State = AiState.Windup; e.T = c.telegraph;
                        e.Cd = c.telegraph + c.lungeCooldown;
                        e.Tele = new Telegraph
                        {
                            Shape = TeleShape.Lane, Center = e.Pos, Dir = e.Dir,
                            Forward = c.lungeSpeed * c.lungeTime + e.R, Side = e.R, Total = c.telegraph, T = c.telegraph,
                        };
                        Events.Add(new SimEvent { Kind = EventKind.TelegraphStart, Pos = e.Pos, Enemy = e });
                    }
                    break;
                case AiState.Windup:
                    e.Face = e.Dir;
                    e.T -= dt;
                    e.Tele.T = e.T;
                    if (e.T <= 0f) { e.State = AiState.Lunge; e.T = c.lungeTime; e.HitDone = false; }
                    break;
                case AiState.Lunge:
                    e.T -= dt;
                    e.Pos += e.Dir * (c.lungeSpeed * dt);
                    if (!e.HitDone && (Player.Pos - e.Pos).magnitude < e.R + PlayerCfg.radius)
                    {
                        e.HitDone = true;
                        HurtPlayer(c.damage, e.Pos);
                    }
                    if (e.T <= 0f) { e.State = AiState.Chase; e.Tele = null; }
                    break;
                default:
                    e.State = AiState.Wander;
                    break;
            }
        }

        /// <summary>Простое обтекание колонн: если колонна впереди, уходим вбок.</summary>
        Vector2 Steer(Enemy e, Vector2 dir)
        {
            foreach (var o in Obstacles)
            {
                var d = o.Pos - e.Pos;
                float l = d.magnitude;
                if (l > o.R + e.R + 1f || Vector2.Dot(d, dir) <= 0f) continue;
                var side = new Vector2(-d.y, d.x) / Mathf.Max(l, 0.001f);
                if (Vector2.Dot(side, dir) < 0f) side = -side;
                float w = 1f - Mathf.Clamp01((l - o.R - e.R) / 1f);
                dir = (dir + side * (1.5f * w)).normalized;
            }
            return dir;
        }
    }
}
