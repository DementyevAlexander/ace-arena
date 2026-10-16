using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Ловушки комнат Hades с параметрами из EnemyData.lua / EnemyWeapons.sjson (поле param комнаты).
    /// Урон по Ace — урон Hades × 10; по врагам — урон × damageMultiplierVsEnemies × 5, как здоровье врагов.
    /// Предупреждения как в Hades: шипы без знака (только щелчок площадки 0.2 с), статуи и пилы белеют в замахе,
    /// газ и яд — растущий круг облака, взрывной куб — красный круг радиуса взрыва, пока горит фитиль.</summary>
    public sealed partial class ArenaSim
    {
        // Hades: облако газа и яда висит и травит, пока не рассеется (BlastSpeed 75 — медленное расширение).
        const float CloudTime = 4f, CloudTick = 0.5f, CloudDamage = 1f;

        void BuildTraps()
        {
            var list = Room.traps;
            for (int i = 0; i < list.Length; i++)
            {
                var t = list[i];
                float a = t.angle * Mathf.Deg2Rad;
                var trap = new Trap
                {
                    Kind = KindOf(t.type), Type = t.type, Pos = t.pos, Dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a)),
                    Poly = t.p != null && t.p.Length >= 3 ? t.p : null, P = t.param ?? new RoomDef.TrapParam(),
                    Regenerating = t.type == "BlastCubeFusedRegenerating",
                };
                // спящие ловушки просыпаются через 5–15 с (WakeUpDelay), пилы и лучники стартуют вразнобой
                if (trap.Kind is TrapKind.Gas or TrapKind.Poison) { trap.Phase = TrapPhase.Cooldown; trap.T = Rand(trap.P.wakeMin, Mathf.Max(trap.P.wakeMin, trap.P.wakeMax)); }
                else if (trap.Kind is TrapKind.Saw or TrapKind.Archer) { trap.Phase = TrapPhase.Cooldown; trap.T = Rand(0.5f, 2.5f); }
                Traps.Add(trap);
                // статуи, пилы, излучатели и топоры — твёрдые
                if (t.solid && trap.Kind != TrapKind.BlastCube)
                {
                    float r = 0.3f;
                    if (trap.Poly != null) foreach (var v in trap.Poly) r = Mathf.Max(r, (v - t.pos).magnitude);
                    Obstacles.Add(new Obstacle { Pos = t.pos, R = Mathf.Min(r, 1.2f), Height = 0f, Type = t.type });
                }
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].link >= 0 && list[i].link < Traps.Count) Traps[i].Link = Traps[list[i].link];
                if (list[i].chain != null)
                    foreach (int c in list[i].chain)
                        if (c >= 0 && c < Traps.Count) Traps[i].Chain.Add(Traps[c]);
            }
        }

        static TrapKind KindOf(string type) => type switch
        {
            "SpikeTrap" => TrapKind.Spike,
            "DartTrap" => TrapKind.DartPlate,
            "DartTrapEmitter" => TrapKind.DartEmitter,
            "AxeTrapTrigger" => TrapKind.AxeTrigger,
            "AxeTrap" => TrapKind.Axe,
            "BlastCubeFused" or "BlastCubeFusedRegenerating" => TrapKind.BlastCube,
            "GasTrapPassive" => TrapKind.Gas,
            "PoisonTrap" => TrapKind.Poison,
            "PhalanxTrap" => TrapKind.Phalanx,
            "SawTrap" => TrapKind.Saw,
            "ArcherTrap" => TrapKind.Archer,
            _ => TrapKind.Inert,
        };

        float HeroDamage(Trap t) => t.P.damage * Hades.EnemyDamageScale;
        float EnemyTrapDamage(Trap t) => t.P.damage * Mathf.Max(1f, t.P.enemyMul) * Hades.EnemyHpScale;

        void SetPhase(Trap t, TrapPhase phase, float time)
        {
            t.Phase = phase;
            t.T = t.Total = time;
        }

        /// <summary>Ловушка взведена, только пока в её комнате бой: на этаже — комната заперта (ActiveRoom), в отдельной комнате
        /// Hades — пока она не зачищена (двери не горят). В хабе и в пройденных комнатах ловушки не работают.</summary>
        public bool TrapArmed(Trap t)
        {
            if (HubMode) return false;
            if (!FloorMode) return !DoorsOpen;
            if (ActiveRoom < 0) return false;
            int x = Mathf.FloorToInt(t.Pos.x), y = Mathf.FloorToInt(t.Pos.y);
            return Floor.RoomAt(x, y) == ActiveRoom || Floor.Rooms[ActiveRoom].Rect.Contains(new Vector2Int(x, y));
        }

        void UpdateTraps(float dt)
        {
            foreach (var t in Traps)
            {
                bool fuseLit = t.Kind == TrapKind.BlastCube && t.Phase is TrapPhase.Pre or TrapPhase.Cooldown; // куб с фитилём взрывается и вне боя
                if (!TrapArmed(t) && !fuseLit)
                {
                    // бой кончился (или не начался): ловушка сброшена и стоит, облако рассеялось; разбитая — так и остаётся
                    if (t.Phase != TrapPhase.Gone) { t.Phase = TrapPhase.Idle; t.T = 0f; }
                    t.Cloud = 0f;
                    continue;
                }
                if (t.Cloud > 0f) UpdateCloud(t, dt);
                switch (t.Kind)
                {
                    case TrapKind.Spike:
                    case TrapKind.DartPlate:
                    case TrapKind.AxeTrigger:
                        UpdatePlate(t, dt);
                        break;
                    case TrapKind.Axe:
                        UpdateAxe(t, dt);
                        break;
                    case TrapKind.BlastCube:
                        UpdateBlastCube(t, dt);
                        break;
                    case TrapKind.Gas:
                    case TrapKind.Poison:
                        UpdateGas(t, dt);
                        break;
                    case TrapKind.Phalanx:
                        UpdatePhalanx(t, dt);
                        break;
                    case TrapKind.Saw:
                    case TrapKind.Archer:
                        UpdateShooter(t, dt);
                        break;
                }
            }
        }

        /// <summary>Площадка: шаг (AIAttackDistance 100) → щелчок 0.2 с → удар → перезарядка 2 с.</summary>
        void UpdatePlate(Trap t, float dt)
        {
            if (t.Phase == TrapPhase.Idle)
            {
                if (SomeoneOnPad(t)) SetPhase(t, TrapPhase.Pre, t.P.pre);
                return;
            }
            t.T -= dt;
            if (t.T > 0f) return;
            if (t.Phase == TrapPhase.Pre)
            {
                switch (t.Kind)
                {
                    case TrapKind.Spike: FireSpikes(t); break;
                    case TrapKind.DartPlate: FireDart(t.Link ?? t, t); break;
                    case TrapKind.AxeTrigger:
                        foreach (var axe in t.Chain)
                            if (axe.Phase == TrapPhase.Idle) SetPhase(axe, TrapPhase.Pre, axe.P.pre);
                        break;
                }
                SetPhase(t, TrapPhase.Cooldown, Mathf.Max(0.1f, t.P.cooldown));
            }
            else SetPhase(t, TrapPhase.Idle, 0f);
        }

        void FireSpikes(Trap t)
        {
            Events.Add(new SimEvent { Kind = EventKind.TrapFire, Pos = t.Pos, Color = 0 });
            var p = Player;
            if (p.Hidden <= 0f && !p.Dashing && OnPad(t, p.Pos, PlayerCfg.radius)) HurtPlayer(HeroDamage(t), t.Pos, HitStop.HurtLight, 0f);
            foreach (var e in Enemies)
            {
                if (!e.Alive || !OnPad(t, e.Pos, e.R)) continue;
                Damage(e, EnemyTrapDamage(t), false);
                if (!e.Dead) ApplyCc(e, CcKind.Stun, 0.35f); // OnHitStun ловушки (кроме героя)
            }
        }

        /// <summary>Излучатель на стене стреляет дротиком: скорость 2500, дальность 4000, 3 урона, отбрасывание 400.</summary>
        void FireDart(Trap emitter, Trap plate)
        {
            var w = plate.P.speed > 0f ? plate.P : emitter.P;
            var dir = emitter.Dir;
            // излучатель смотрит в комнату; если направление не задано — на площадку
            if (emitter != plate && Vector2.Dot(dir, plate.Pos - emitter.Pos) < 0f) dir = (plate.Pos - emitter.Pos).normalized;
            Bolts.Add(new Bolt
            {
                Pos = emitter.Pos + dir * w.barrel, Dir = dir, Speed = w.speed > 0f ? w.speed : Hades.U(2500f), Range = w.range > 0f ? w.range : Hades.U(4000f),
                R = Hades.U(20f), Damage = w.damage * Hades.EnemyDamageScale, EnemyDamage = w.damage * Mathf.Max(1f, plate.P.enemyMul) * Hades.EnemyHpScale,
                Knock = w.knock, FromTrap = true, Stop = HitStop.HurtLight,
            });
            Events.Add(new SimEvent { Kind = EventKind.TrapFire, Pos = emitter.Pos, To = dir, Color = 1 });
        }

        /// <summary>Топор: замах 0.3 с (белеет) → удар 30 по всем у тела → 1 с → 0.5 с.</summary>
        void UpdateAxe(Trap t, float dt)
        {
            if (t.Phase == TrapPhase.Idle) return;
            t.T -= dt;
            if (t.T > 0f) return;
            switch (t.Phase)
            {
                case TrapPhase.Pre:
                    float r = Hades.U(160f);
                    if ((Player.Pos - t.Pos).magnitude <= r + PlayerCfg.radius) HurtPlayer(HeroDamage(t), t.Pos, HitStop.HurtHeavy, Hades.U(600f));
                    foreach (var e in Enemies) if (e.Alive && (e.Pos - t.Pos).magnitude <= r + e.R) Damage(e, EnemyTrapDamage(t), false);
                    Events.Add(new SimEvent { Kind = EventKind.TrapFire, Pos = t.Pos, Radius = r, Color = 2 });
                    SetPhase(t, TrapPhase.Post, Mathf.Max(0.1f, t.P.post));
                    break;
                case TrapPhase.Post: SetPhase(t, TrapPhase.Cooldown, Mathf.Max(0.1f, t.P.cooldown)); break;
                default: SetPhase(t, TrapPhase.Idle, 0f); break;
            }
        }

        /// <summary>Взрывной куб: от удара загорается фитиль (1.5 с, в Элизиуме 2 с), взрыв по кругу.
        /// Восстанавливающийся куб через 6 с снова готов.</summary>
        void UpdateBlastCube(Trap t, float dt)
        {
            if (t.Phase is TrapPhase.Idle) return;
            t.T -= dt;
            if (t.T > 0f) return;
            if (t.Phase == TrapPhase.Pre)
            {
                float radius = t.P.radius > 0f ? t.P.radius : Hades.U(400f);
                if ((Player.Pos - t.Pos).magnitude <= radius + PlayerCfg.radius) HurtPlayer(HeroDamage(t), t.Pos, HitStop.HurtHeavy, Hades.U(800f));
                foreach (var e in Enemies)
                    if (e.Alive && (e.Pos - t.Pos).magnitude <= radius + e.R) Damage(e, EnemyTrapDamage(t), false);
                Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = t.Pos, Radius = radius, Color = 3 });
                if (t.Regenerating) SetPhase(t, TrapPhase.Cooldown, Mathf.Max(0.5f, t.P.dormant));
                else SetPhase(t, TrapPhase.Gone, 0f);
            }
            else if (t.Phase == TrapPhase.Cooldown) SetPhase(t, TrapPhase.Idle, 0f);
        }

        /// <summary>Удар картой рядом с кубом поджигает фитиль.</summary>
        void ArmBlastCubes(Vector2 at, float radius)
        {
            foreach (var t in Traps)
                if (t.Kind == TrapKind.BlastCube && t.Phase == TrapPhase.Idle && (t.Pos - at).magnitude <= radius + Hades.U(60f))
                    SetPhase(t, TrapPhase.Pre, t.P.fuse > 0f ? t.P.fuse : 1.5f);
        }

        /// <summary>Газ (Асфодель, Элизиум) и яд (Стикс): проснулся → замах (3 с / 1.5 с, растущий круг) → облако → долгая перезарядка.</summary>
        void UpdateGas(Trap t, float dt)
        {
            t.T -= dt;
            if (t.T > 0f) return;
            if (t.Phase == TrapPhase.Pre)
            {
                t.Cloud = CloudTime; t.CloudTick = 0f;
                Events.Add(new SimEvent { Kind = EventKind.TrapFire, Pos = t.Pos, Radius = GasRadius(t), Color = 3 });
                float cd = t.P.cooldownMax > 0f ? Rand(t.P.cooldownMin, t.P.cooldownMax) : t.P.cooldown;
                SetPhase(t, TrapPhase.Cooldown, Mathf.Max(1f, cd));
            }
            else SetPhase(t, TrapPhase.Pre, Mathf.Max(0.1f, t.P.pre));
        }

        public float GasRadius(Trap t) => t.P.radius > 0f ? t.P.radius : Hades.U(500f);

        void UpdateCloud(Trap t, float dt)
        {
            t.Cloud -= dt;
            t.CloudTick -= dt;
            if (t.CloudTick > 0f) return;
            t.CloudTick = CloudTick;
            float r = GasRadius(t);
            var p = Player;
            if (p.Hidden <= 0f && !p.Dashing && (p.Pos - t.Pos).magnitude <= r) HurtPlayer(CloudDamage * Hades.EnemyDamageScale, t.Pos, HitStop.None, 0f);
            foreach (var e in Enemies)
                if (e.Alive && (e.Pos - t.Pos).magnitude <= r) Damage(e, CloudDamage * Hades.EnemyHpScale, false, true, DotKind.Poison);
        }

        /// <summary>Статуя-фаланга: кто-то в 100 перед ней (смещение 120) → замах 0.5 с (белеет) → укол копьём на 150 → 1.5 с → 2 с.</summary>
        void UpdatePhalanx(Trap t, float dt)
        {
            if (t.Phase == TrapPhase.Idle)
            {
                var spot = t.Pos + t.Dir * Hades.U(120f);
                float r = t.P.trigger > 0f ? t.P.trigger : Hades.U(100f);
                bool near = (Player.Pos - spot).magnitude <= r + PlayerCfg.radius && Player.Hidden <= 0f;
                foreach (var e in Enemies) if (!near && e.Alive && (e.Pos - spot).magnitude <= r + e.R) near = true;
                if (near) SetPhase(t, TrapPhase.Pre, Mathf.Max(0.05f, t.P.pre));
                return;
            }
            t.T -= dt;
            if (t.T > 0f) return;
            switch (t.Phase)
            {
                case TrapPhase.Pre:
                    var from = t.Pos + t.Dir * t.P.barrel;
                    var to = from + t.Dir * (t.P.range > 0f ? t.P.range : Hades.U(150f));
                    float w = Hades.U(40f);
                    if (DistToSegment(Player.Pos, from, to) <= w + PlayerCfg.radius && Player.Hidden <= 0f) HurtPlayer(HeroDamage(t), t.Pos, HitStop.HurtLight, Hades.U(400f));
                    foreach (var e in Enemies) if (e.Alive && DistToSegment(e.Pos, from, to) <= w + e.R) Damage(e, EnemyTrapDamage(t), false);
                    Events.Add(new SimEvent { Kind = EventKind.TrapFire, Pos = from, To = to, Color = 4 });
                    SetPhase(t, TrapPhase.Fire, Mathf.Max(0.1f, t.P.fire));
                    break;
                case TrapPhase.Fire: SetPhase(t, TrapPhase.Cooldown, Mathf.Max(0.1f, t.P.cooldown)); break;
                default: SetPhase(t, TrapPhase.Idle, 0f); break;
            }
        }

        /// <summary>Пила (Стикс) и лучник (Элизиум): сами по кругу — замах (0.5 / 2 с, белеют) → снаряд вперёд → отдых → перезарядка.</summary>
        void UpdateShooter(Trap t, float dt)
        {
            t.T -= dt;
            if (t.T > 0f) return;
            switch (t.Phase)
            {
                case TrapPhase.Pre:
                    bool saw = t.Kind == TrapKind.Saw;
                    Bolts.Add(new Bolt
                    {
                        Pos = t.Pos + t.Dir * t.P.barrel, Dir = t.Dir, Speed = t.P.speed > 0f ? t.P.speed : Hades.U(600f), Range = Hades.U(6000f),
                        R = saw ? Hades.U(70f) : Hades.U(24f), Damage = HeroDamage(t), EnemyDamage = EnemyTrapDamage(t), Knock = t.P.knock,
                        FromTrap = true, Pierce = saw, Stop = HitStop.HurtHeavy,
                    });
                    Events.Add(new SimEvent { Kind = EventKind.TrapFire, Pos = t.Pos, To = t.Dir, Color = saw ? 5 : 1 });
                    SetPhase(t, TrapPhase.Post, Mathf.Max(0.05f, t.P.fire + t.P.post));
                    break;
                case TrapPhase.Post: SetPhase(t, TrapPhase.Cooldown, Mathf.Max(0.1f, t.P.cooldown)); break;
                default: SetPhase(t, TrapPhase.Pre, Mathf.Max(0.05f, t.P.pre)); break;
            }
        }

        public bool OnPad(Trap t, Vector2 p, float pad)
        {
            if (t.Poly != null) return pad > 0f ? Geo.Overlaps(t.Poly, p, pad * 0.5f) : Geo.InPoly(t.Poly, p);
            return (p - t.Pos).magnitude <= Hades.U(100f) + pad;
        }

        bool SomeoneOnPad(Trap t)
        {
            float trigger = t.P.trigger > 0f ? t.P.trigger : Hades.U(100f);
            var p = Player;
            if (p.Hidden <= 0f && !p.Dashing && (OnPad(t, p.Pos, 0f) || (p.Pos - t.Pos).magnitude < trigger * 0.5f)) return true;
            // летающие враги (Numbskull) шипы не задевают; наши Thug и Witch ходят по земле
            foreach (var e in Enemies)
                if (e.Alive && (OnPad(t, e.Pos, 0f) || (e.Pos - t.Pos).magnitude < trigger * 0.5f)) return true;
            return false;
        }
    }
}
