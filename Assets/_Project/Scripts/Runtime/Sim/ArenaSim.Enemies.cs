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
                if (e.Kind == EnemyKind.Wastrel) UpdateWastrel(e, dt);
                else UpdateCasket(e, dt);
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
                float speed = e.Kind == EnemyKind.Wastrel ? WastrelCfg.fearSpeed : CasketCfg.fearSpeed;
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
            e.Aggro = true; e.Alert = 0.6f;
            // Casket после того, как заметил, ещё AggroDuration стоит и смотрит (Enemy_Mage_Alert)
            if (e.Kind == EnemyKind.Casket) { e.State = AiState.Alert; e.T = CasketCfg.aggroDuration; }
            else { e.State = AiState.Chase; e.T = 0f; }
            Events.Add(new SimEvent { Kind = EventKind.EnemyAlert, Pos = e.Pos, Enemy = e });
            foreach (var o in Enemies)
            {
                if (o == e || !o.Alive || o.Aggro || o.React >= 0f) continue;
                float range = o.Kind == EnemyKind.Wastrel ? WastrelCfg.aggroRange : CasketCfg.aggroRange;
                if ((o.Pos - e.Pos).magnitude <= range)
                    o.React = o.Kind == EnemyKind.Wastrel ? Rand(WastrelCfg.reactionMin, WastrelCfg.reactionMax) : Rand(CasketCfg.reactionMin, CasketCfg.reactionMax);
            }
        }

        static Telegraph Trapezoid(Vector2 from, Vector2 dir, float length, float baseHalf, float tipHalf, float time) => new()
        {
            Shape = TeleShape.Trapezoid, Center = from, Dir = dir, Forward = length, Base = baseHalf, Side = tipHalf, Total = time, T = time,
        };

        // ---------- Wastrel (Brawler): двухударное комбо ----------

        void UpdateWastrel(Enemy e, float dt)
        {
            var c = WastrelCfg;
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
            var c = WastrelCfg;
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

        // ---------- Casket (Mage): HideAndPeekAI ----------

        void UpdateCasket(Enemy e, float dt)
        {
            var c = CasketCfg;
            if (!UpdateAggro(e, dt, c.aggroRange, c.reactionMin, c.reactionMax, c.wanderDistance, c.wanderInterval, c.speed)) return;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            var dirP = dist > 0.0001f ? toPlayer / dist : e.Face;
            // отскок при выстрелах (FireTickSelfVelocity) затухает
            e.Pos += e.Vel * dt;
            e.Vel *= Mathf.Exp(-dt / c.kickDecay);
            switch (e.State)
            {
                case AiState.Alert: // AggroDuration: заметил — пауза, смотрит на Смерть
                    e.Face = dirP;
                    e.T -= dt;
                    if (e.T <= 0f) BeginHide(e);
                    break;
                case AiState.Chase: // в бой без паузы (например, поставлен тестом) — сразу к укрытию
                    BeginHide(e);
                    break;
                case AiState.Hide:
                {
                    // за ближайшее укрытие, с дальней от Смерти стороны (CoverHugDistance), TakeCoverDuration 1.5–2.2 с
                    e.T -= dt;
                    if (FindCover(e.Pos, out var cover, out float coverR))
                    {
                        var away = cover - Player.Pos;
                        var spot = cover + (away.sqrMagnitude > 0.0001f ? away.normalized : Vector2.up) * (coverR + c.coverHug);
                        var to = spot - e.Pos;
                        if (to.magnitude > 0.38f) { var d = Steer(e, to.normalized); e.Pos += d * (c.speed * dt); e.Face = d; }
                        else e.Face = dirP;
                    }
                    if (e.T > 0f) break;
                    // выглянул: атакует, если атакующих меньше MaxAttackers, иначе прячется снова
                    int attackers = 0;
                    foreach (var o in Enemies) if (o.Alive && o.Kind == EnemyKind.Casket && o.Attacker) attackers++;
                    if (attackers < c.maxAttackers) { e.Attacker = true; e.State = AiState.Approach; e.T = Rand(c.approachTimeoutMin, c.approachTimeoutMax); }
                    else BeginHide(e);
                    break;
                }
                case AiState.Approach:
                    // MoveWithinRange: ближе AttackDistance и с прямой видимостью (RequireProjectileLoS)
                    e.Face = dirP;
                    e.T -= dt;
                    if (Player.Hidden <= 0f && dist <= c.attackDistance && ClearShot(e.Pos, Player.Pos))
                    {
                        e.State = AiState.Windup; e.T = c.windup; e.Dir = dirP;
                        e.Tele = new Telegraph { Shape = TeleShape.Lane, Center = e.Pos, Dir = dirP, Forward = Mathf.Min(dist, c.boltRange), Side = 0.18f, Total = c.windup, T = c.windup };
                        Events.Add(new SimEvent { Kind = EventKind.TelegraphStart, Pos = e.Pos, Enemy = e });
                        break;
                    }
                    if (e.T <= 0f) { e.Attacker = false; BeginHide(e); break; }
                    e.Pos += Steer(e, dirP) * (c.speed * dt);
                    break;
                case AiState.Windup:
                    // TrackTargetDuringCharge: прицел следует за Смертью весь замах
                    e.Dir = dirP; e.Face = dirP;
                    e.T -= dt;
                    e.Tele.Center = e.Pos; e.Tele.Dir = dirP; e.Tele.Forward = Mathf.Min(dist, c.boltRange); e.Tele.T = e.T;
                    if (e.T <= 0f) { e.Tele = null; e.State = AiState.Strike; e.Strike = 0; e.T = 0f; }
                    break;
                case AiState.Strike:
                    // FireTicks 3 через FireInterval 0.17 с, после каждого — отскок назад, слабее с каждым разом
                    e.T -= dt;
                    if (e.T > 0f) break;
                    e.Face = dirP;
                    Add(new Effect
                    {
                        Kind = EffectKind.EnemyBolt, Pos = e.Pos + dirP * 0.42f, Dir = dirP, Speed = c.boltSpeed, Life = c.boltLife,
                        R = c.boltRadius, Damage = c.damage,
                    });
                    e.Vel = -dirP * (c.kickSpeed * Mathf.Pow(c.kickFalloff, e.Strike));
                    Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos + dirP * 0.42f, To = dirP, Enemy = e, Radius = 0.3f });
                    e.Strike++;
                    if (e.Strike < c.shots) e.T = c.shotInterval;
                    else { e.State = AiState.Recover; e.T = Rand(c.recoveryMin, c.recoveryMax); }
                    break;
                case AiState.Recover:
                    e.Face = dirP;
                    e.T -= dt;
                    if (e.T <= 0f) { e.Attacker = false; BeginHide(e); }
                    break;
                default:
                    BeginHide(e);
                    break;
            }
        }

        void BeginHide(Enemy e)
        {
            e.State = AiState.Hide;
            e.T = Rand(CasketCfg.coverMin, CasketCfg.coverMax);
            e.Tele = null;
        }

        /// <summary>Ближайшее к точке укрытие: стоящий предмет, который держит снаряды (группа Standing и
        /// StopsProjectiles в Hades) — колонны, здания, стены, бочки, ящики, амфоры, обломки, деревья.</summary>
        bool FindCover(Vector2 from, out Vector2 center, out float radius)
        {
            center = default; radius = 0f;
            float best = float.MaxValue;
            foreach (var o in Obstacles)
            {
                float d = (o.Pos - from).sqrMagnitude;
                if (d < best) { best = d; center = o.Pos; radius = o.R; }
            }
            if (Layout != null)
                foreach (var w in Layout.walls)
                {
                    if (!w.projectiles || w.kind is "wall" or "gap" or "door") continue;
                    var c = (w.Min + w.Max) * 0.5f;
                    float d = (c - from).sqrMagnitude;
                    if (d < best) { best = d; center = c; radius = (w.Max - w.Min).magnitude * 0.5f; }
                }
            return best < float.MaxValue;
        }

        /// <summary>Прямая видимость для снаряда: на отрезке нет препятствий, которые его остановят.</summary>
        bool ClearShot(Vector2 a, Vector2 b)
        {
            var d = b - a;
            float len = d.magnitude;
            for (float t = 0.4f; t < len - 0.3f; t += 0.25f)
                if (BlocksShot(a + d * (t / len), 0.05f)) return false;
            return true;
        }

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
