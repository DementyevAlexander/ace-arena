using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Враги Hades II — Wastrel (Brawler) и Casket (Mage), стартовые враги Эреба: AggroAI, комбо Wastrel с выпадом
    /// и дугой, HideAndPeekAI Casket с залпом самонаводящихся снарядов. Обозначения атак — зоны на земле, как в ветке Hades II.</summary>
    public sealed partial class ArenaSim
    {
        /// <summary>Отталкивание свойства: отброшенный враг ударился о стену или другого врага — урон обоим.</summary>
        void KnockImpact(Enemy e, bool wall)
        {
            Enemy other = null;
            foreach (var o in Enemies)
                if (o != e && o.Alive && (o.Pos - e.Pos).magnitude <= o.R + e.R + 0.05f) { other = o; break; }
            if (!wall && other == null) return;
            float dmg = e.KnockDmg;
            e.KnockDmg = 0f;
            e.KnockT = Mathf.Min(e.KnockT, 0.05f);
            Damage(e, dmg, false);
            if (other != null) { Damage(other, dmg, false); if (other.Alive) ApplyCc(other, Arena.Core.CcKind.Stun, 0.3f); }
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = e.Pos, Radius = 0.6f, Color = 1 });
        }

        // ---------- Общее: AggroAI из Hades II ----------

        /// <summary>Пока не заметил: бродит случайным шагом ±AIWanderDistance от прошлой точки (раз в AIWanderFrequency + 0.5 с).
        /// Заметил Смерть ближе AIAggroRange (или остался последним — AggroIfLastAlive) — через задержку реакции вступает в бой.
        /// true — уже агрессивен.</summary>
        bool UpdateAggro(Enemy e, float dt, float aggroRange, float reactMin, float reactMax, float wanderDist, float wanderEvery, float speed)
        {
            // в бой сразу, если враг уже не в состоянии блуждания (например, поставлен в погоню тестом)
            if (e.Aggro || e.State != AiState.Wander) { e.Aggro = true; return true; }
            float dist = (Player.Pos - e.Pos).magnitude;
            if (e.React < 0f && Player.Hidden <= 0f && (dist <= aggroRange || AliveCount() == 1)) e.React = Rand(reactMin, reactMax);
            if (e.React >= 0f)
            {
                e.React -= dt;
                if (e.React <= 0f) { MakeAggro(e); return true; }
            }
            e.T -= dt;
            if (e.T <= 0f)
            {
                e.Dir += new Vector2(Rand(-wanderDist, wanderDist), Rand(-wanderDist, wanderDist)); // e.Dir — точка блуждания
                e.T = wanderEvery;
            }
            var to = e.Dir - e.Pos;
            if (to.magnitude > 0.1f) { var d = Steer(e, to.normalized); e.Pos += d * (speed * dt); e.Face = d; }
            return false;
        }

        void MakeAggro(Enemy e)
        {
            if (e.Aggro) return;
            e.Aggro = true; e.Alert = 0.6f; e.React = -1f;
            Events.Add(new SimEvent { Kind = EventKind.EnemyAlert, Pos = e.Pos, Enemy = e });
            // Casket после того, как заметил, ещё AggroDuration стоит и смотрит (Enemy_Mage_Alert); соседей поднимает после неё
            if (e.Kind == EnemyKind.Casket) { e.State = AiState.Alert; e.T = CasketCfg.aggroDuration; }
            else { e.State = AiState.Chase; e.T = 0f; e.ChaseT = 0f; ChainAggro(e); }
        }

        /// <summary>ChainAggroAllEnemies: поднимает тех, кто сам ближе своей AIAggroRange к этому врагу.</summary>
        void ChainAggro(Enemy e)
        {
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

        static Vector2 TurnToward(Vector2 from, Vector2 to, float maxDeg)
        {
            float a = Mathf.Clamp(Vector2.SignedAngle(from, to), -maxDeg, maxDeg) * Mathf.Deg2Rad;
            return new Vector2(from.x * Mathf.Cos(a) - from.y * Mathf.Sin(a), from.x * Mathf.Sin(a) + from.y * Mathf.Cos(a)).normalized;
        }

        // ---------- Wastrel (Brawler): двухударное комбо ----------

        void UpdateWastrel(Enemy e, float dt)
        {
            var c = WastrelCfg;
            if (!UpdateAggro(e, dt, c.aggroRange, c.reactionMin, c.reactionMax, c.wanderDistance, c.wanderInterval, c.speed)) return;
            var toPlayer = Player.Pos - e.Pos;
            float dist = toPlayer.magnitude;
            var dirP = dist > 0.0001f ? toPlayer / dist : e.Face;
            switch (e.State)
            {
                case AiState.Chase:
                    // замах, сорванный оглушением, повторяется с места, без подхода (EnemyAILogic: ожидание CanAttack и повтор)
                    if (e.Retry) { e.Retry = false; BeginBrawlerHit(e, 1); break; }
                    // MoveWithinRange: идёт, пока не будет в AttackDistance − буфер и с прямой видимостью (RequireUnitLoS);
                    // за 9 с не вышло — бьёт как есть (тайм-аут MoveWithinRange)
                    e.Face = dirP;
                    e.ChaseT += dt;
                    bool inRange = dist <= c.attackDistance && ClearWalk(e.Pos, Player.Pos, c.losBuffer);
                    if (Player.Hidden <= 0f && (inRange || e.ChaseT >= c.approachTimeout)) BeginBrawlerHit(e, 1);
                    else e.Pos += Steer(e, dirP) * (c.speed * dt);
                    break;
                case AiState.Windup:
                    if (e.Strike == 2)
                    {
                        // BrawlerMeleeClose доворачивает медленно: RotationSpeed 1200 × PreAttackRotationDampening 0.02
                        e.Dir = TurnToward(e.Dir, dirP, c.hit2WindupTurn * dt);
                        e.Tele.Dir = ArcDir(e.Dir); e.Tele.Center = e.Pos;
                    }
                    e.Face = e.Dir;
                    e.T -= dt;
                    e.Tele.T = e.T;
                    if (e.T <= 0f) { e.State = AiState.Strike; e.T = 0f; e.HitDone = false; }
                    break;
                case AiState.Strike:
                    StrikeWastrel(e, dt, dirP);
                    break;
                case AiState.Recover:
                    e.T -= dt;
                    if (e.T <= 0f) { e.State = AiState.Chase; e.ChaseT = 0f; }
                    break;
                default:
                    e.State = AiState.Chase;
                    break;
            }
        }

        /// <summary>Удар Wastrel: хитбокс прикреплён к нему (AttachToOwner) и живёт StartDelay … StartDelay + Fuse.
        /// Удар 1 — выпад FireSelfVelocity 1800 с торможением AttackHighGrip до остановки через 0.3 с (HaltOnEnd).</summary>
        void StrikeWastrel(Enemy e, float dt, Vector2 dirP)
        {
            var c = WastrelCfg;
            bool first = e.Strike == 1;
            e.T += dt;
            if (first)
            {
                float v = Mathf.Max(0f, c.lungeSpeed - c.lungeDecel * e.T);
                if (e.T <= c.lungeTime) e.Pos += e.Dir * (v * dt);
            }
            else
            {
                e.Dir = TurnToward(e.Dir, dirP, c.hit2FireTurn * dt); // FireRotationDampening 0.1
                float v = Mathf.Max(0f, c.stepSpeed - c.lungeDecel * e.T);
                if (e.T <= c.lungeTime) e.Pos += e.Dir * (v * dt);
                if (e.Tele != null) { e.Tele.Dir = ArcDir(e.Dir); e.Tele.Center = e.Pos; }
            }
            float delay = first ? c.hit1Delay : c.hit2Delay, active = first ? c.hit1Active : c.hit2Active;
            if (e.T >= delay && e.T - dt < delay)
                Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos + e.Dir * 0.8f, To = e.Dir, Enemy = e, Radius = first ? c.hit1TipHalf : c.hit2Reach });
            if (!e.HitDone && e.T >= delay && e.T <= delay + active && WastrelHitbox(e, first).Contains(Player.Pos, PlayerCfg.radius))
            {
                e.HitDone = true;
                // HeroOnHitStun 0.2 с (стоит, не поворачивается, атаковать может), ImpactVelocity 200 под GRIP 0.3 с
                HurtPlayer(first ? c.damage : c.damage2, e.Pos, first ? HitStop.WastrelHit1 : HitStop.WastrelHit2, c.heroKnockSpeed, c.heroStun, 0.05f, c.heroKnockTime);
            }
            if (e.T >= (first ? c.fire1 : c.fire2))
            {
                e.Tele = null;
                if (first) BeginBrawlerHit(e, 2); // PostAttackDuration 0 → сразу ChainedWeapon
                else { e.State = AiState.Recover; e.T = c.recovery; }
            }
        }

        /// <summary>Оглушили во время удара: хитбокс уже выпущен и доживает свой Fuse на месте; потом — снова в бой.</summary>
        void FiredHitboxWhileStunned(Enemy e, float dt)
        {
            var c = WastrelCfg;
            bool first = e.Strike == 1;
            e.T += dt;
            float delay = first ? c.hit1Delay : c.hit2Delay, active = first ? c.hit1Active : c.hit2Active;
            if (!e.HitDone && e.T >= delay && e.T <= delay + active && WastrelHitbox(e, first).Contains(Player.Pos, PlayerCfg.radius))
            {
                e.HitDone = true;
                HurtPlayer(first ? c.damage : c.damage2, e.Pos, first ? HitStop.WastrelHit1 : HitStop.WastrelHit2, c.heroKnockSpeed, c.heroStun, 0.05f, c.heroKnockTime);
            }
            if (e.T > delay + active) { e.Tele = null; e.State = AiState.Chase; e.ChaseT = 0f; e.Retry = false; }
        }

        /// <summary>Хитбокс удара там, где Wastrel сейчас: трапеция (−10, ±40)…(230, ±100) или дуга −110…80° радиусом 220.</summary>
        Telegraph WastrelHitbox(Enemy e, bool first)
        {
            var c = WastrelCfg;
            if (first) return new Telegraph { Shape = TeleShape.Trapezoid, Center = e.Pos - e.Dir * c.hit1Back, Dir = e.Dir, Forward = c.hit1Reach + c.hit1Back, Base = c.hit1BaseHalf, Side = c.hit1TipHalf };
            return new Telegraph { Shape = TeleShape.Fan, Center = e.Pos, Dir = ArcDir(e.Dir), Forward = c.hit2Reach, Angle = (c.arcEnd - c.arcStart) * 0.5f };
        }

        Vector2 ArcDir(Vector2 dir)
        {
            var c = WastrelCfg;
            float a = (c.arcStart + c.arcEnd) * 0.5f * Mathf.Deg2Rad;
            return new Vector2(dir.x * Mathf.Cos(a) - dir.y * Mathf.Sin(a), dir.x * Mathf.Sin(a) + dir.y * Mathf.Cos(a));
        }

        /// <summary>Удар 1 — направление в начале замаха (PreAttackAngleTowardTarget), дальше не следит; обозначена вся полоса
        /// выпада. Удар 2 — дуга 190° вокруг себя, медленно доворачивает.</summary>
        void BeginBrawlerHit(Enemy e, int index)
        {
            var c = WastrelCfg;
            e.Strike = index;
            e.State = AiState.Windup;
            if (index == 1)
            {
                var to = Player.Pos - e.Pos;
                e.Dir = to.sqrMagnitude > 0.0001f ? to.normalized : e.Face;
                e.T = c.windup1;
                float t = Mathf.Min(c.lungeTime, c.lungeSpeed / c.lungeDecel);
                float lunge = c.lungeSpeed * t - 0.5f * c.lungeDecel * t * t;
                e.Tele = Trapezoid(e.Pos - e.Dir * c.hit1Back, e.Dir, lunge + c.hit1Reach + c.hit1Back, c.hit1BaseHalf, c.hit1TipHalf, e.T);
            }
            else
            {
                e.T = c.windup2;
                var hb = WastrelHitbox(e, false);
                e.Tele = new Telegraph { Shape = TeleShape.Fan, Center = hb.Center, Dir = hb.Dir, Forward = hb.Forward, Angle = hb.Angle, Total = e.T, T = e.T };
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
                case AiState.Alert: // AggroDuration: заметил — пауза, смотрит на Смерть, потом поднимает соседей
                    e.Face = dirP;
                    e.T -= dt;
                    if (e.T <= 0f) { ChainAggro(e); BeginHide(e); }
                    break;
                case AiState.Chase: // в бой без паузы (поставлен тестом или волной) — сразу к укрытию
                    if (e.Retry) { e.Retry = false; BeginCasketWindup(e, dirP, dist); break; } // замах, сорванный оглушением
                    BeginHide(e);
                    break;
                case AiState.Hide:
                {
                    // за ближайшее укрытие: на CoverHugDistance от его центра с дальней от Смерти стороны, 1.5–2.2 с
                    e.T -= dt;
                    if (FindCover(e.Pos, out var cover, out _))
                    {
                        var away = cover - Player.Pos;
                        var spot = cover + (away.sqrMagnitude > 0.0001f ? away.normalized : Vector2.up) * c.coverHug;
                        var to = spot - e.Pos;
                        if (to.magnitude > 0.38f) { var d = Steer(e, to.normalized); e.Pos += d * (c.speed * dt); e.Face = d; }
                        else e.Face = dirP;
                    }
                    if (e.T > -c.peekDelay) break; // выглянул: ещё 0.1 с
                    int attackers = 0;
                    foreach (var o in Enemies) if (o.Alive && o.Kind == EnemyKind.Casket && o.Attacker) attackers++;
                    if (attackers < c.maxAttackers) { e.Attacker = true; e.State = AiState.Approach; e.T = Rand(c.approachTimeoutMin, c.approachTimeoutMax); }
                    else BeginHide(e);
                    break;
                }
                case AiState.Approach:
                    // MoveWithinRange: ближе AttackDistance − буфер и с прямой видимостью (RequireProjectileLoS, LoSBuffer 25);
                    // за 7–10 с не вышло — стреляет как есть
                    e.Face = dirP;
                    e.T -= dt;
                    if (Player.Hidden <= 0f && ((dist <= c.attackDistance && ClearShot(e.Pos, Player.Pos, c.losBuffer)) || e.T <= 0f))
                    {
                        BeginCasketWindup(e, dirP, dist);
                        break;
                    }
                    e.Pos += Steer(e, dirP) * (c.speed * dt);
                    break;
                case AiState.Windup:
                    // TrackTargetDuringCharge с PreAttackRotationDampening 0.08: прицел доворачивает ≈ 96°/с
                    e.Dir = TurnToward(e.Dir, dirP, c.windupTurn * dt); e.Face = e.Dir;
                    e.T -= dt;
                    e.Tele.Center = e.Pos; e.Tele.Dir = e.Dir; e.Tele.Forward = Mathf.Min(dist, c.boltRange); e.Tele.T = e.T;
                    if (e.T <= 0f) { e.Tele = null; e.State = AiState.Strike; e.Strike = 0; e.T = 0f; }
                    break;
                case AiState.Strike:
                    // FireTicks 3 через FireInterval 0.17 с (пауза и после последнего), затем FireDuration 0.2 с
                    e.T -= dt;
                    if (e.T > 0f) break;
                    if (e.Strike >= c.shots) { e.State = AiState.Recover; e.T = Rand(c.recoveryMin, c.recoveryMax); break; }
                    e.Face = e.Dir;
                    Add(new Effect
                    {
                        Kind = EffectKind.EnemyBolt, Pos = e.Pos + e.Dir * 0.42f, Dir = e.Dir, Speed = c.boltSpeed, Life = c.boltLife,
                        R = c.boltRadius, Damage = c.damage, Target = e, // Target — кто стрелял (Возврат барьера)
                    });
                    e.Vel = -e.Dir * (c.kickSpeed * Mathf.Pow(c.kickFalloff, e.Strike));
                    Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos + e.Dir * 0.42f, To = e.Dir, Enemy = e, Radius = 0.3f });
                    e.Strike++;
                    e.T = c.shotInterval + (e.Strike >= c.shots ? c.fireDuration : 0f);
                    break;
                case AiState.Recover:
                    e.Face = dirP;
                    e.T -= dt;
                    if (e.T <= 0f) BeginHide(e);
                    break;
                default:
                    BeginHide(e);
                    break;
            }
        }

        void BeginCasketWindup(Enemy e, Vector2 dirP, float dist)
        {
            var c = CasketCfg;
            e.Attacker = true;
            e.State = AiState.Windup; e.T = c.windup; e.Dir = dirP; // WaitForAngleTowardTarget: сначала разворот к цели
            e.Tele = new Telegraph { Shape = TeleShape.Lane, Center = e.Pos, Dir = dirP, Forward = Mathf.Min(dist, c.boltRange), Side = 0.18f, Total = c.windup, T = c.windup };
            Events.Add(new SimEvent { Kind = EventKind.TelegraphStart, Pos = e.Pos, Enemy = e });
        }

        void BeginHide(Enemy e)
        {
            e.State = AiState.Hide;
            e.T = Rand(CasketCfg.coverMin, CasketCfg.coverMax);
            e.Tele = null;
            e.Attacker = false; // место атакующего освобождается (SurroundEnemiesAttacking)
        }

        /// <summary>Ближайшее к точке укрытие: стоящий предмет, который держит снаряды (группа Standing и
        /// StopsProjectiles в Hades) — колонны, здания, стены, бочки, ящики, амфоры, обломки, деревья.</summary>
        bool FindCover(Vector2 from, out Vector2 center, out float radius)
        {
            center = default; radius = 0f;
            float best = float.MaxValue;
            foreach (var o in Obstacles)
            {
                if (!o.Shots || o.Height <= 0f && o.Type != null && o.Type.StartsWith("_Impassable")) continue;
                if (o.Type is "wall" or "gap" or "door" or "Hole" || o.R > 4f) continue;
                float d = (o.Pos - from).sqrMagnitude;
                if (d < best) { best = d; center = o.Pos; radius = o.R; }
            }
            return best < float.MaxValue;
        }

        /// <summary>Прямая видимость для снаряда: на отрезке нет препятствий, которые его остановят (с запасом buffer).</summary>
        bool ClearShot(Vector2 a, Vector2 b, float buffer)
        {
            var d = b - a;
            float len = d.magnitude;
            for (float t = 0.4f; t < len - 0.3f; t += 0.25f)
                if (BlocksShot(a + d * (t / len), buffer)) return false;
            return true;
        }

        /// <summary>Прямая видимость для удара (RequireUnitLoS): на отрезке нет препятствий, которые держат юнитов.</summary>
        bool ClearWalk(Vector2 a, Vector2 b, float buffer)
        {
            var d = b - a;
            float len = d.magnitude;
            for (float t = 0.4f; t < len - 0.3f; t += 0.25f)
                if (HitsObstacle(a + d * (t / len), buffer)) return false;
            return true;
        }
    }
}
