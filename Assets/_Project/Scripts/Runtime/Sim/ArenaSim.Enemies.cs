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
            CollideCircle(ref e.Pos, e.R, before);
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

        // ---------- Общее: AggroAI из Hades II ----------

        /// <summary>Пока не заметил: бродит вокруг точки появления (±AIWanderDistance раз в AIWanderFrequency).
        /// Заметил Смерть ближе AIAggroRange — через задержку реакции становится агрессивным и поднимает остальных
        /// (ChainAggroAllEnemies: тех, кто сам ближе своей дальности агрессии к нему). true — уже агрессивен.</summary>
        bool UpdateAggro(Enemy e, float dt, float aggroRange, float reactMin, float reactMax, float wanderDist, float wanderEvery, float speed)
        {
            // в бой сразу, если враг уже не в состоянии блуждания (например, поставлен в погоню тестом)
            if (e.Aggro || e.State != AiState.Wander) { e.Aggro = true; return true; }
            float dist = (Player.Pos - e.Pos).magnitude;
            if (e.React < 0f && dist <= aggroRange && Player.Hidden <= 0f) e.React = Rand(reactMin, reactMax);
            if (e.React >= 0f)
            {
                e.React -= dt;
                if (e.React <= 0f) { MakeAggro(e); return true; }
            }
            e.T -= dt;
            if (e.T <= 0f)
            {
                e.Dir = e.Home + new Vector2(Rand(-wanderDist, wanderDist), Rand(-wanderDist, wanderDist)); // точка блуждания
                e.T = wanderEvery;
            }
            var to = e.Dir - e.Pos;
            if (to.magnitude > 0.1f) { var d = Steer(e, to.normalized); e.Pos += d * (speed * dt); e.Face = d; }
            return false;
        }

        void MakeAggro(Enemy e)
        {
            if (e.Aggro) return;
            e.Aggro = true; e.State = AiState.Chase; e.Alert = 0.6f; e.T = 0f;
            Events.Add(new SimEvent { Kind = EventKind.EnemyAlert, Pos = e.Pos, Enemy = e });
            foreach (var o in Enemies)
            {
                if (o == e || !o.Alive || o.Aggro || o.React >= 0f) continue;
                float range = o.Kind == EnemyKind.Ghost ? GhostCfg.aggroRange : StomperCfg.aggroRange;
                if ((o.Pos - e.Pos).magnitude <= range)
                    o.React = o.Kind == EnemyKind.Ghost ? Rand(GhostCfg.reactionMin, GhostCfg.reactionMax) : Rand(StomperCfg.reactionMin, StomperCfg.reactionMax);
            }
        }

        static Telegraph Trapezoid(Vector2 from, Vector2 dir, float length, float baseHalf, float tipHalf, float time) => new()
        {
            Shape = TeleShape.Trapezoid, Center = from, Dir = dir, Forward = length, Base = baseHalf, Side = tipHalf, Total = time, T = time,
        };

        // ---------- Wastrel (Brawler): двухударное комбо ----------

        void UpdateGhost(Enemy e, float dt)
        {
            var c = GhostCfg;
            if (!UpdateAggro(e, dt, c.aggroRange, c.reactionMin, c.reactionMax, c.wanderDistance, c.wanderInterval, c.speed)) return;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            switch (e.State)
            {
                case AiState.Chase:
                    // MoveWithinRange + StopMoveWithinRange: идёт, пока не окажется в AttackDistance − буфер
                    e.Face = toPlayer.normalized;
                    if (dist <= c.attackDistance && Player.Hidden <= 0f) BeginBrawlerHit(e, 1);
                    else e.Pos += Steer(e, toPlayer.normalized) * (c.speed * dt);
                    break;
                case AiState.Windup:
                    e.Face = e.Dir;
                    e.T -= dt;
                    e.Tele.T = e.T;
                    if (e.T <= 0f) { e.State = AiState.Strike; e.T = 0f; e.HitDone = false; }
                    break;
                case AiState.Strike:
                {
                    e.T += dt;
                    bool first = e.Strike == 1;
                    // FireSelfVelocity: рывок с затуханием
                    float v = (first ? c.lungeSpeed : c.stepSpeed) * Mathf.Exp(-e.T / c.lungeDecay);
                    e.Pos += e.Dir * (v * dt);
                    float delay = first ? c.hit1Delay : c.hit2Delay, active = first ? c.hit1Active : c.hit2Active;
                    if (e.T >= delay && e.T - dt < delay)
                        Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos + e.Dir * 0.8f, To = e.Dir, Enemy = e, Radius = first ? c.hit1TipHalf : c.hit2Reach });
                    // удар бьёт ровно обозначенную фигуру, пока активен хитбокс (StartDelay … StartDelay + Fuse)
                    if (!e.HitDone && e.T >= delay && e.T <= delay + active && e.Tele.Contains(Player.Pos, PlayerCfg.radius))
                    {
                        e.HitDone = true;
                        HurtPlayer(c.damage, e.Pos);
                    }
                    if (e.T >= (first ? c.fire1 : c.fire2))
                    {
                        e.Tele = null;
                        if (first) BeginBrawlerHit(e, 2); // PostAttackDuration 0 → сразу ChainedWeapon
                        else { e.State = AiState.Recover; e.T = c.recovery; }
                    }
                    break;
                }
                case AiState.Recover:
                    e.T -= dt;
                    if (e.T <= 0f) e.State = AiState.Chase;
                    break;
                default:
                    e.State = AiState.Chase;
                    break;
            }
        }

        /// <summary>Удар 1 — трапеция вдоль выпада (обозначена вся полоса, которую пройдёт хитбокс);
        /// удар 2 — дуга 190° вокруг себя, направление почти не меняется (PreAttackRotationDampening 0.02).</summary>
        void BeginBrawlerHit(Enemy e, int index)
        {
            var c = GhostCfg;
            e.Strike = index;
            e.State = AiState.Windup;
            if (index == 1)
            {
                var to = Player.Pos - e.Pos; // PreAttackAngleTowardTarget: разворот в начале замаха, дальше не следит
                e.Dir = to.sqrMagnitude > 0.0001f ? to.normalized : e.Face;
                e.T = c.windup1;
                float lunge = c.lungeSpeed * c.lungeDecay * (1f - Mathf.Exp(-c.fire1 / c.lungeDecay));
                e.Tele = Trapezoid(e.Pos, e.Dir, lunge + c.hit1Reach, c.hit1BaseHalf, c.hit1TipHalf, e.T);
            }
            else
            {
                e.T = c.windup2;
                float mid = (c.arcStart + c.arcEnd) * 0.5f, half = (c.arcEnd - c.arcStart) * 0.5f;
                float a = mid * Mathf.Deg2Rad;
                var dir = new Vector2(e.Dir.x * Mathf.Cos(a) - e.Dir.y * Mathf.Sin(a), e.Dir.x * Mathf.Sin(a) + e.Dir.y * Mathf.Cos(a));
                e.Tele = new Telegraph { Shape = TeleShape.Fan, Center = e.Pos, Dir = dir, Forward = c.hit2Reach, Angle = half, Total = e.T, T = e.T };
            }
            Events.Add(new SimEvent { Kind = EventKind.TelegraphStart, Pos = e.Tele.Center, Enemy = e });
        }

        // ---------- Lubber (ZombieHeavyRanged): бочка по области, отход, толчок вблизи ----------

        void UpdateStomper(Enemy e, float dt)
        {
            var c = StomperCfg;
            if (!UpdateAggro(e, dt, c.aggroRange, c.reactionMin, c.reactionMax, c.wanderDistance, c.wanderInterval, c.speed)) return;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            var dirP = dist > 0.0001f ? toPlayer / dist : e.Face;

            // SurroundAI: атакуют не больше MaxAttackers, место пересматривается раз в SurroundRefreshInterval
            e.Refresh -= dt;
            if (!e.Attacker && e.Refresh <= 0f)
            {
                e.Refresh = c.surroundRefresh;
                int n = 0;
                foreach (var o in Enemies) if (o.Alive && o.Kind == EnemyKind.Stomper && o.Attacker) n++;
                if (n < c.maxAttackers) e.Attacker = true;
            }

            switch (e.State)
            {
                case AiState.Chase:
                case AiState.Wait:
                    e.Face = dirP;
                    if (!e.Attacker) { e.State = AiState.Wait; break; } // ждёт своей очереди, глядя на Смерть
                    e.State = AiState.Chase;
                    if (Player.Hidden > 0f) break;
                    // выбор оружия по Requirements: бросок — дальше MinPlayerDistance, толчок — ближе MaxPlayerDistance
                    bool canToss = dist >= c.tossMinDistance, canShove = dist <= c.shoveMaxDistance;
                    if (e.Strike == 0) e.Strike = canToss && (!canShove || Rand(0f, 1f) < 0.5f) ? 1 : 2;
                    if (e.Strike == 1 && !canToss) e.Strike = 2;
                    float range = e.Strike == 1 ? c.tossDistance : c.shoveDistance;
                    if (dist > range) { e.Pos += Steer(e, dirP) * (c.speed * dt); break; }
                    e.State = AiState.Windup;
                    e.T = e.Strike == 1 ? c.tossWindup + c.tossRelease : c.shoveWindup;
                    e.HitDone = false;
                    e.Dir = dirP;
                    e.Tele = e.Strike == 1
                        ? new Telegraph { Shape = TeleShape.Ellipse, Center = TossTarget(e), Dir = Vector2.up, Forward = c.blastRadius, Side = c.blastRadius, Total = e.T + Flight(e), T = e.T + Flight(e) }
                        : Trapezoid(e.Pos, e.Dir, c.shoveReach, c.shoveBaseHalf, c.shoveTipHalf, e.T);
                    Events.Add(new SimEvent { Kind = EventKind.TelegraphStart, Pos = e.Tele.Center, Enemy = e });
                    break;
                case AiState.Windup:
                    // TrackTargetDuringCharge / WaitForAngleTowardTarget: всё время замаха следит за Смертью
                    e.Dir = dirP; e.Face = dirP;
                    e.T -= dt;
                    if (e.Strike == 1)
                    {
                        // заполнение — за весь замах и полёт: к падению бочки круг полон
                        float flightNow = Flight(e);
                        e.Tele.Center = TossTarget(e);
                        e.Tele.Total = c.tossWindup + c.tossRelease + flightNow; e.Tele.T = Mathf.Max(0f, e.T) + flightNow;
                        if (e.T <= 0f)
                        {
                            // бочка летит в точку, где Смерть стоит сейчас; обозначение переходит к бочке
                            var target = e.Tele.Center;
                            float flight = Flight(e);
                            Add(new Effect
                            {
                                Kind = EffectKind.Barrel, Pos = e.Pos, Start = e.Pos, To = target, Life = flight, T = flight, R = c.blastRadius, Damage = c.damage,
                                Hold = e.Tele.Progress, // доля обозначения, заполненная к броску
                            });
                            e.Tele = null;
                            e.State = AiState.Recover; e.T = c.tossRecovery;
                        }
                    }
                    else
                    {
                        e.Tele.Center = e.Pos; e.Tele.Dir = e.Dir; e.Tele.T = e.T;
                        if (e.T <= 0f) { e.State = AiState.Strike; e.T = 0f; }
                    }
                    break;
                case AiState.Strike: // толчок
                    e.T += dt;
                    if (!e.HitDone && e.T <= c.shoveActive && e.Tele.Contains(Player.Pos, PlayerCfg.radius))
                    {
                        e.HitDone = true;
                        HurtPlayer(c.damage, e.Pos);
                    }
                    if (e.T - dt <= 0f) Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos + e.Dir * (c.shoveReach * 0.5f), To = e.Dir, Enemy = e, Radius = c.shoveTipHalf });
                    if (e.T >= c.shoveFire) { e.Tele = null; e.State = AiState.Recover; e.T = c.shoveRecovery; }
                    break;
                case AiState.Recover:
                    e.Face = dirP;
                    e.T -= dt;
                    if (e.T > 0f) break;
                    // RetreatAfterAttackChance 1.0 — после броска отходит; после толчка — снова в бой
                    if (e.Strike == 1) { e.State = AiState.Retreat; e.T = Rand(c.retreatTimeMin, c.retreatTimeMax); }
                    else e.State = AiState.Chase;
                    e.Strike = 0;
                    break;
                case AiState.Retreat:
                    e.T -= dt;
                    if (dist >= c.retreatDistance || e.T <= 0f) { e.State = AiState.Chase; break; }
                    var away = Steer(e, -dirP);
                    e.Pos += away * (c.speed * dt);
                    e.Face = away;
                    break;
                default:
                    e.State = AiState.Chase;
                    break;
            }
        }

        /// <summary>Точка падения бочки: Смерть сейчас, но не ближе MinRange и не дальше Range от Lubber (без упреждения).</summary>
        Vector2 TossTarget(Enemy e)
        {
            var c = StomperCfg;
            var d = Player.Pos - e.Pos;
            float l = d.magnitude;
            var dir = l > 0.0001f ? d / l : e.Face;
            return e.Pos + dir * Mathf.Clamp(l, c.tossMinRange, c.tossMaxRange);
        }

        float Flight(Enemy e) => (TossTarget(e) - e.Pos).magnitude / StomperCfg.barrelSpeed;

        /// <summary>Простое обтекание: колонна впереди — уходим вбок, стена арены впереди — поворачиваем к свободному направлению.</summary>
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
            if (Layout != null && HitsObstacle(e.Pos + dir * 0.8f, e.R))
            {
                // стена впереди: ищем ближайшее свободное направление, поворачивая в обе стороны
                for (int k = 1; k <= 6; k++)
                    foreach (int sgn in new[] { 1, -1 })
                    {
                        var d = Rotate(dir, sgn * k * 25f * Mathf.Deg2Rad);
                        if (!HitsObstacle(e.Pos + d * 0.8f, e.R)) return d;
                    }
            }
            return dir;
        }

    }
}
