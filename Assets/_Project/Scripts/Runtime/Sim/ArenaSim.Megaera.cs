using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Мегера — босс этажа (Harpy в Hades, числа — MegaeraTuning). Четыре фазы по здоровью (75 / 50 / 25%): удар не проходит
    /// ниже порога, на пороге она 2 с неуязвима (BossStageTransition) и берёт новое оружие. Фаза 1 — рывок и кнут; 2 — ещё веер или
    /// молнии; 3 — подмога «Ко мне!»; 4 — оставшееся из веера и молний. Оружие выбирается как в Hades (SelectWeapon): подмога — как
    /// только можно, иначе случайно из доступных (MinAttacksBetweenUse, MaxConsecutiveUses, дистанция кнута), ничего — рывок.
    /// Атака: подойти на дистанцию оружия (или по таймауту), замах с красным телеграфом, удар, пауза. Волн нет — подмога только
    /// её «Ко мне!» (фоновую подмогу BossHarpy1 не делаем). Умерла — подмога умирает с ней.</summary>
    public sealed partial class ArenaSim
    {
        Enemy megaera;
        bool megaFight, eliteOnly;

        /// <summary>Мегера в комнате босса: на дальней от Смерти точке появления, сразу в бою. Этажи 2–5 — пока она же, крепче.</summary>
        void StartMegaeraFight(EtgPlacedRoom room)
        {
            var c = MegaeraCfg;
            var at = room.Center;
            float far = -1f;
            foreach (var s in Room.spawns)
            {
                float d = (s.pos - Player.Pos).sqrMagnitude;
                if (d > far) { far = d; at = s.pos; }
            }
            var e = SpawnMegaera(at);
            e.MaxHp = e.Hp = c.hp * (1f + c.floorHpGrowth * FloorIndex + 0.5f * FloorLap);
            megaera = e;
            megaFight = true;
        }

        /// <summary>Поставить Мегеру (бой босса, проверки): стоит на арене сразу, как в Hades, и вступает в бой через AISetupDelay.</summary>
        public Enemy SpawnMegaera(Vector2 at)
        {
            var e = SpawnEnemy(EnemyKind.Megaera, at, 0f, true);
            e.State = AiState.Recover;
            e.T = MegaeraCfg.setupDelay;
            e.Stage = 0;
            e.Armed = new bool[5];
            e.Ago = new int[5];
            for (int i = 0; i < e.Ago.Length; i++) e.Ago[i] = 99;
            e.Armed[(int)MegaWeapon.Lunge] = e.Armed[(int)MegaWeapon.Whirl] = true;
            e.Weapon = (MegaWeapon)(-1);
            return e;
        }

        /// <summary>Бой с Мегерой — без волн (фоновую подмогу BossHarpy1 не делаем: только её «Ко мне!»). Умерла — подмога тоже,
        /// комната пройдена.</summary>
        void UpdateMegaeraFight(float dt)
        {
            if (megaera == null || megaera.Dead)
            {
                if (megaera != null) { Meta.megaeraKills++; Meta.Dirty = true; DropSardis(megaera.Pos, 1); } // босс — 1 Сардис
                megaFight = false;
                foreach (var o in Enemies) if (!o.Dead && !o.Dummy) Kill(o); // WipeEnemiesOnKill — и те, что ещё проявляются
                megaera = null;
                ClearRoom();
                return;
            }
        }

        // ---------- ИИ ----------

        void UpdateMegaera(Enemy e, float dt)
        {
            var c = MegaeraCfg;
            if (e.Invuln > 0f)
            {
                // смена фазы: стоит, неуязвима
                e.Invuln -= dt;
                e.Vel = Vector2.zero;
                return;
            }
            var to = Player.Pos - e.Pos;
            float dist = to.magnitude;
            switch (e.State)
            {
                case AiState.Windup:
                    e.T -= dt;
                    if (e.Tele != null) e.Tele.T = Mathf.Max(0f, e.T);
                    if (e.T <= 0f) FireMegaera(e);
                    return;
                case AiState.Strike:
                    UpdateMegaeraStrike(e, dt);
                    return;
                case AiState.Recover:
                    e.T -= dt;
                    if (e.T > 0f) return;
                    e.State = AiState.Chase;
                    e.Weapon = (MegaWeapon)(-1);
                    return;
            }
            // подойти на дистанцию оружия
            if ((int)e.Weapon < 0) PickMegaWeapon(e, dist);
            e.State = AiState.Chase;
            e.ChaseT += dt;
            float want = MegaRange(e.Weapon);
            float timeout = e.Weapon == MegaWeapon.Whirl ? c.whirlTimeout : c.chaseTimeout;
            if (dist > want && e.ChaseT < timeout && Player.Hidden <= 0f)
            {
                var dir = to / Mathf.Max(dist, 0.001f);
                e.Face = dir;
                e.Pos += dir * c.speed * dt;
                return;
            }
            BeginMegaAttack(e, to);
        }

        float MegaRange(MegaWeapon w)
        {
            var c = MegaeraCfg;
            return w switch
            {
                MegaWeapon.Lunge => c.lungeRange, MegaWeapon.Whirl => c.whirlRange, MegaWeapon.Beam => c.beamRange,
                MegaWeapon.Lightning => c.boltsRange, _ => 99f,
            };
        }

        /// <summary>SelectWeapon Hades: подмога — как только можно (ForceUseIfReady); иначе случайно из доступных; ничего — рывок.</summary>
        void PickMegaWeapon(Enemy e, float dist)
        {
            var c = MegaeraCfg;
            bool Ok(MegaWeapon w)
            {
                int i = (int)w;
                if (!e.Armed[i]) return false;
                return w switch
                {
                    MegaWeapon.Lunge => e.LungeRun < c.lungeMaxRun,
                    MegaWeapon.Whirl => e.Ago[i] >= c.whirlGap && dist <= c.whirlMaxDistance,
                    MegaWeapon.Beam => e.Ago[i] >= c.beamGap,
                    MegaWeapon.Lightning => e.Ago[i] >= c.boltsGap,
                    _ => e.Ago[i] >= c.spawnGap && OwnSpawns(e) < c.spawnMaxActive,
                };
            }
            MegaWeapon pick;
            if (Ok(MegaWeapon.Spawns)) pick = MegaWeapon.Spawns;
            else
            {
                var options = new List<MegaWeapon>();
                for (int i = 0; i < 4; i++) if (Ok((MegaWeapon)i)) options.Add((MegaWeapon)i);
                pick = options.Count > 0 ? options[rng.Next(options.Count)] : MegaWeapon.Lunge;
            }
            for (int i = 0; i < e.Ago.Length; i++) e.Ago[i]++;
            e.Ago[(int)pick] = 0;
            e.LungeRun = pick == MegaWeapon.Lunge ? e.LungeRun + 1 : 0;
            e.Weapon = pick;
            e.ChaseT = 0f;
        }

        int OwnSpawns(Enemy e)
        {
            int n = 0;
            foreach (var o in Enemies) if (o.Alive && o.Owner == e) n++;
            return n;
        }

        void BeginMegaAttack(Enemy e, Vector2 to)
        {
            var c = MegaeraCfg;
            var dir = to.sqrMagnitude > 0.0001f ? to.normalized : e.Face;
            e.Face = e.CastDir = dir;
            e.Vel = Vector2.zero;
            e.State = AiState.Windup;
            switch (e.Weapon)
            {
                case MegaWeapon.Lunge:
                    e.T = c.lungeWindup;
                    e.Tele = new Telegraph { Shape = TeleShape.Lane, Center = e.Pos, Dir = dir, Forward = c.lungeDistance + e.R, Side = c.lungeWidth, Total = e.T, T = e.T };
                    break;
                case MegaWeapon.Whirl:
                    e.T = c.whirlWindup;
                    e.Tele = new Telegraph { Shape = TeleShape.Ellipse, Center = e.Pos, Dir = Vector2.up, Forward = c.whirlRadius, Side = c.whirlRadius, Total = e.T, T = e.T };
                    break;
                case MegaWeapon.Beam:
                    e.T = c.beamWindup;
                    e.Tele = new Telegraph { Shape = TeleShape.Fan, Center = e.Pos, Dir = dir, Forward = 4f, Angle = c.beamAngle * (c.beamCount - 1) * 0.5f, Total = e.T, T = e.T };
                    break;
                case MegaWeapon.Lightning:
                    e.T = c.boltsWindup;
                    e.Tele = null; // каждая молния — свой красный круг
                    break;
                default:
                    e.T = 0.01f; // подмога — без замаха
                    e.Tele = null;
                    break;
            }
        }

        void FireMegaera(Enemy e)
        {
            var c = MegaeraCfg;
            e.State = AiState.Strike;
            Events.Add(new SimEvent { Kind = EventKind.Attack, Pos = e.Pos, To = e.CastDir, Enemy = e, Radius = 0.5f });
            switch (e.Weapon)
            {
                case MegaWeapon.Lunge:
                    e.DashFrom = e.Pos;
                    e.CastAt = TravelTarget(e.Pos, e.CastDir, c.lungeDistance);
                    e.DashHit = false;
                    e.T = c.lungeTime;
                    break;
                case MegaWeapon.Whirl:
                    e.Tele = null;
                    if ((Player.Pos - e.Pos).magnitude <= c.whirlRadius + PlayerCfg.radius)
                        HurtPlayer(c.whirlDamage, e.Pos, HitStop.HurtSmall, 6f, c.whirlStun);
                    Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = e.Pos, Radius = c.whirlRadius, Color = 0 });
                    EndMegaAttack(e, c.whirlRecover);
                    break;
                case MegaWeapon.Beam:
                    e.Tele = null;
                    e.Ticks = c.beamTicks;
                    e.T = 0f;
                    break;
                case MegaWeapon.Lightning:
                    e.Ticks = c.boltsTicks;
                    e.T = 0f;
                    break;
                default:
                {
                    // «Ко мне!»: шесть Thug или Witch у точек появления рядом с ней, по одному через 0.1 с
                    int n = Mathf.Min(c.spawnCount, c.spawnMaxActive - OwnSpawns(e) + 1);
                    for (int i = 0; i < n; i++)
                    {
                        var kind = rng.Next(2) == 0 ? EnemyKind.Thug : EnemyKind.Witch;
                        var add = SpawnEnemy(kind, MegaSpawnPoint(e), 0.1f * i);
                        add.SpawnAggro = true;
                        add.Owner = e;
                    }
                    EndMegaAttack(e, c.spawnRecover);
                    break;
                }
            }
        }

        Vector2 MegaSpawnPoint(Enemy e)
        {
            var c = MegaeraCfg;
            var near = new List<Vector2>();
            foreach (var s in Room.spawns) if ((s.pos - e.Pos).magnitude <= c.spawnRadius) near.Add(s.pos);
            if (near.Count > 0) return near[rng.Next(near.Count)];
            var p = e.Pos + Rotate(Vector2.right, (float)rng.NextDouble() * Mathf.PI * 2f) * 2f;
            CollideCircle(ref p, 0.5f);
            return p;
        }

        void UpdateMegaeraStrike(Enemy e, float dt)
        {
            var c = MegaeraCfg;
            switch (e.Weapon)
            {
                case MegaWeapon.Lunge:
                {
                    e.T -= dt;
                    float k = Mathf.Clamp01(1f - e.T / c.lungeTime);
                    e.Pos = Vector2.Lerp(e.DashFrom, e.CastAt, k);
                    if (!e.DashHit && (Player.Pos - e.Pos).magnitude <= e.R + PlayerCfg.radius + c.lungeWidth * 0.5f
                        && HurtPlayer(c.lungeDamage, e.Pos, HitStop.HurtSmall, 6f, c.lungeStun))
                        e.DashHit = true;
                    if (e.T <= 0f) { e.Tele = null; EndMegaAttack(e, c.lungeRecover); }
                    break;
                }
                case MegaWeapon.Beam:
                    e.T -= dt;
                    if (e.T > 0f) break;
                    e.T += c.beamTick;
                    {
                        // каждый залп — снова на героя (AIAngleTowardsPlayerWhileFiring)
                        var to = Player.Pos - e.Pos;
                        var dir = to.sqrMagnitude > 0.0001f ? to.normalized : e.Face;
                        e.Face = dir;
                        for (int i = 0; i < c.beamCount; i++)
                        {
                            float a = ((i - (c.beamCount - 1) * 0.5f) * c.beamAngle + Rand(-c.beamSpread, c.beamSpread)) * Mathf.Deg2Rad;
                            var d = Rotate(dir, a);
                            Add(new Effect
                            {
                                Kind = EffectKind.EnemyBolt, Pos = e.Pos + d * (e.R + 0.3f), Dir = d, Speed = c.beamSpeed, Life = 3f,
                                R = Hades.U(14f) + 0.08f, Damage = c.beamDamage, Target = e, Straight = true,
                            });
                        }
                    }
                    if (--e.Ticks <= 0) EndMegaAttack(e, c.beamRecover);
                    break;
                case MegaWeapon.Lightning:
                    e.T -= dt;
                    if (e.T > 0f) break;
                    e.T += c.boltsTick;
                    {
                        // 3–4 из 5 точек вокруг героя: центр и четыре стороны на 425
                        var slots = new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up, Vector2.left, Vector2.down };
                        int n = 3 + rng.Next(2);
                        var at = Player.Pos;
                        for (int i = 0; i < n; i++)
                        {
                            int j = rng.Next(slots.Count);
                            var p = at + slots[j] * c.boltsRing;
                            slots.RemoveAt(j);
                            Add(new Effect { Kind = EffectKind.MegaStrike, Pos = p, R = c.boltsRadius, Life = c.boltsFuse, Target = e });
                        }
                    }
                    if (--e.Ticks <= 0) EndMegaAttack(e, c.boltsRecover);
                    break;
            }
        }

        void EndMegaAttack(Enemy e, float recover)
        {
            e.State = AiState.Recover;
            e.T = recover;
        }

        /// <summary>Молния Мегеры: красный круг, через Fuse — удар по всем внутри.</summary>
        void UpdateMegaStrike(Effect f, float dt)
        {
            f.Life -= dt;
            if (f.Life > 0f) return;
            f.Done = true;
            if ((Player.Pos - f.Pos).magnitude <= f.R + PlayerCfg.radius * 0.5f) HurtPlayer(MegaeraCfg.boltsDamage, f.Pos, HitStop.HurtSmall, 0f);
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = f.Pos, Radius = f.R, Color = 0 });
        }

        // ---------- фазы ----------

        /// <summary>Урон по Мегере: в смене фазы — не проходит; ниже порога фазы — не проходит (Combat.lua: Health − MaxHealth·thr + 1),
        /// на пороге — смена фазы. Возвращает урон, который можно нанести.</summary>
        float MegaeraDamage(Enemy e, float amount, out bool shift)
        {
            shift = false;
            if (e.Invuln > 0f) return 0f;
            var c = MegaeraCfg;
            if (e.Stage >= c.stageAt.Length) return amount;
            float thr = c.stageAt[e.Stage] * e.MaxHp;
            if (e.Hp - amount > thr) return amount;
            shift = true;
            return Mathf.Max(0f, e.Hp - (thr - 1f));
        }

        /// <summary>Порог фазы: 2 с неуязвима, атака обрывается, новое оружие. Фаза 2 — веер или молнии, 3 — подмога, 4 — другое из двух.</summary>
        void MegaeraStageShift(Enemy e)
        {
            var c = MegaeraCfg;
            e.Stage++;
            e.Invuln = c.stageShift;
            e.Tele = null;
            e.State = AiState.Chase;
            e.Weapon = (MegaWeapon)(-1);
            e.ChaseT = 0f;
            int beam = (int)MegaWeapon.Beam, bolts = (int)MegaWeapon.Lightning;
            switch (e.Stage)
            {
                case 1: e.Armed[rng.Next(2) == 0 ? beam : bolts] = true; break;
                case 2: e.Armed[(int)MegaWeapon.Spawns] = true; break;
                default: e.Armed[beam] = e.Armed[bolts] = true; break;
            }
            Add(new Effect { Kind = EffectKind.EliteShield, Pos = e.Pos, Target = e, R = e.R + 0.6f, Life = c.stageShift });
            Events.Add(new SimEvent { Kind = EventKind.BossStage, Pos = e.Pos, Enemy = e, Value = e.Stage });
        }
    }
}
