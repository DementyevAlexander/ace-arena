using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Вся логика арены без отрисовки: Ace, карты, враги, ловушки, волны.
    /// Координаты — плоскость пола (x, z мира) в юнитах арены; числа Hades переводятся через <see cref="Hades.U"/>.</summary>
    public sealed partial class ArenaSim
    {
        public readonly PlayerTuning PlayerCfg;
        public readonly CastTuning CastCfg;
        public readonly GhostTuning GhostCfg;
        public readonly StomperTuning StomperCfg;
        public readonly ThugTuning ThugCfg;
        public readonly WitchTuning WitchCfg;
        public readonly ArenaTuning ArenaCfg;

        /// <summary>Комната как есть в данных и её вариант в игре (может быть отражена, как в Hades).</summary>
        public RoomDef Room { get; private set; }
        RoomDef baseRoom;
        public bool MirrorRooms = true;
        /// <summary>Направление «вправо по экрану» на полу (камера повёрнута на 45°): вдоль него Hades отражает комнаты.</summary>
        public static readonly Vector2 ScreenRight = new(0.70710678f, -0.70710678f);
        public readonly List<Breakable> Breakables = new();
        /// <summary>Комната пройдена — двери открыты, шаг в дверь ведёт в следующую комнату.</summary>
        public bool DoorsOpen { get; private set; }
        public int RoomsCleared { get; private set; }
        public readonly Player Player = new();
        public readonly Slot[] Slots = { new(), new(), new(), new() };
        public readonly CardId?[] Reserve = new CardId?[11];
        public readonly List<Enemy> Enemies = new();
        public readonly List<Effect> Effects = new();
        public readonly List<Obstacle> Obstacles = new();
        public readonly List<Bolt> Bolts = new();
        public readonly List<Trap> Traps = new();
        public readonly List<HazardZone> Hazards = new();
        public readonly List<SimEvent> Events = new();

        public int Wave;
        public int Kills;
        public bool GameOver;
        public float Time;
        public bool WavesEnabled = true;
        int nextId = 1;
        readonly System.Random rng;

        public ArenaSim(int seed = 1, RoomDef room = null, PlayerTuning player = null, GhostTuning ghost = null, StomperTuning stomper = null, ArenaTuning arena = null)
        {
            rng = new System.Random(seed);
            PlayerCfg = player ?? new PlayerTuning();
            CastCfg = new CastTuning();
            GhostCfg = ghost ?? new GhostTuning();
            StomperCfg = stomper ?? new StomperTuning();
            ThugCfg = new ThugTuning();
            WitchCfg = new WitchTuning();
            ArenaCfg = arena ?? new ArenaTuning();
            baseRoom = room ?? RoomLibrary.Default();
            Room = baseRoom;
            SetDefaultDeck();
            Restart();
        }

        public void SetDefaultDeck()
        {
            CardId?[] mains = { CardId.TwinDaggers, CardId.Crescendo, CardId.BlackHole, CardId.JumpScare };
            for (int i = 0; i < 4; i++) { Slots[i].Main = mains[i]; Slots[i].Up = null; }
            CardId[] rest =
            {
                CardId.FiveOfAKind, CardId.Roses, CardId.Dio, CardId.Dazzling, CardId.Starburst, CardId.Aegis,
                CardId.MasterfulJump, CardId.Dash, CardId.BlazingSprint, CardId.ShockingArrival, CardId.EscapeArtist,
            };
            for (int i = 0; i < Reserve.Length; i++) Reserve[i] = i < rest.Length ? rest[i] : null;
        }

        /// <summary>Сменить комнату и начать заново.</summary>
        public void LoadRoom(RoomDef room)
        {
            baseRoom = room ?? RoomDef.Box(10f, 7f);
            Restart();
        }

        /// <summary>Hades отражает комнату по горизонтали при каждом входе — половину раз.</summary>
        void PickVariant() => Room = MirrorRooms && rng.Next(2) == 1 ? baseRoom.Mirrored(ScreenRight) : baseRoom;

        public void Restart()
        {
            PickVariant();
            RoomsCleared = 0;
            var p = Player;
            p.Pos = Room.heroStart; p.Face = new Vector2(0f, 1f); p.Aim = p.Face;
            p.Vel = p.ExtVel = Vector2.zero; p.GripMul = 1f; p.GripT = 0f; p.Stun = 0f; p.RecentDamage.Clear();
            p.Hp = PlayerCfg.hp; p.HitInvuln = p.BuffInvuln = p.Haste = p.Shoes = p.Shield = p.Hidden = 0f;
            p.Cast = null; p.Buffer = -1; p.Lock = null; p.Dashing = false; p.KnockT = 0f; p.ShoeTrail = null;
            foreach (var s in Slots) s.Reset();
            Enemies.Clear(); Effects.Clear(); Events.Clear(); Bolts.Clear();
            BuildRoom();
            Wave = 0; Kills = 0; GameOver = false; Time = 0f;
            DoorsOpen = false;
            ResetEncounter();
        }

        /// <summary>Шаг в открытую дверь: следующая комната из подключённых (сейчас та же, в новом варианте),
        /// глубина растёт, здоровье и колода сохраняются, как между комнатами Hades.</summary>
        public void EnterNextRoom()
        {
            baseRoom = RoomLibrary.Next(rng, baseRoom.name) ?? baseRoom;
            PickVariant();
            RoomsCleared++;
            var p = Player;
            p.Pos = Room.heroStart; p.Vel = p.ExtVel = Vector2.zero; p.Stun = 0f;
            p.Cast = null; p.Buffer = -1; p.Lock = null; p.Dashing = false; p.Hidden = 0f; p.ShoeTrail = null;
            Enemies.Clear(); Effects.Clear(); Bolts.Clear();
            foreach (var s in Slots) s.Bat = null;
            BuildRoom();
            DoorsOpen = false;
            StartGeneratedEncounter(Depth + 1);
            Events.Add(new SimEvent { Kind = EventKind.RoomEnter, Value = RoomsCleared });
        }

        void BuildRoom()
        {
            Obstacles.Clear(); Traps.Clear(); Hazards.Clear(); Breakables.Clear();
            foreach (var w in Room.walls)
            {
                if (w.p == null || w.p.Length < 3) continue;
                var c = Geo.Centroid(w.p);
                float r = 0f;
                foreach (var v in w.p) r = Mathf.Max(r, (v - c).magnitude);
                Obstacles.Add(new Obstacle { Pos = c, R = r, Poly = w.p, Height = w.h, Type = w.type });
            }
            BuildTraps();
            foreach (var b in Room.breakables)
            {
                var body = new Obstacle { Pos = b.pos, R = Mathf.Clamp(b.r, 0.15f, 0.6f), Height = 0f, Type = "Breakable" };
                Obstacles.Add(body);
                Breakables.Add(new Breakable { Pos = b.pos, R = body.R, Body = body });
            }
            foreach (var h in Room.hazards)
                if (h.p != null && h.p.Length >= 3) Hazards.Add(new HazardZone { Type = h.type, Poly = h.p, Damage = h.damage });
        }

        public void Step(float dt, SimInput input)
        {
            Events.Clear();
            if (GameOver) return;
            Time += dt;
            UpdateSlots(dt);
            UpdatePlayer(dt, input);
            for (int i = 0; i < Enemies.Count; i++) UpdateEnemy(Enemies[i], dt);
            Separate();
            UpdateEffects(dt);
            UpdateBolts(dt);
            UpdateTraps(dt);
            UpdateHazards(dt);
            UpdateDoors();
            Enemies.RemoveAll(e => e.Dead && e.DeathT <= 0f);
            Effects.RemoveAll(f => f.Done);
            Bolts.RemoveAll(b => b.Done);
            if (WavesEnabled) UpdateEncounter(dt);
        }

        // ---------- Слоты ----------

        void UpdateSlots(float dt)
        {
            foreach (var s in Slots)
            {
                if (s.OnCd)
                {
                    s.CdTimer -= dt;
                    if (s.CdTimer <= 0f)
                    {
                        var c = s.Card;
                        s.Charges++;
                        s.CdTimer = c.Cooldown;
                        if (s.Charges >= c.Charges) { s.Charges = c.Charges; s.OnCd = false; }
                    }
                }
                s.UpgCd = Mathf.Max(0f, s.UpgCd - dt);
                s.SpawnCd = Mathf.Max(0f, s.SpawnCd - dt);
            }
        }

        /// <summary>Меняет карты местами (колода). Слоты 0–3: главные, 4–7: улучшения, 8+: запас.</summary>
        public void SwapPlaces(int a, int b)
        {
            var va = GetPlace(a);
            SetPlace(a, GetPlace(b));
            SetPlace(b, va);
            foreach (var s in Slots) s.Reset();
            Player.Cast = null; Player.Buffer = -1;
            Effects.RemoveAll(f => f.Kind == EffectKind.Bat);
        }

        public CardId? GetPlace(int i) => i < 4 ? Slots[i].Main : i < 8 ? Slots[i - 4].Up : Reserve[i - 8];

        void SetPlace(int i, CardId? v)
        {
            if (i < 4) Slots[i].Main = v;
            else if (i < 8) Slots[i - 4].Up = v;
            else Reserve[i - 8] = v;
        }

        // ---------- Ace ----------

        void UpdatePlayer(float dt, SimInput input)
        {
            var p = Player;
            p.HitInvuln -= dt; p.BuffInvuln -= dt; p.Haste -= dt; p.Shoes -= dt; p.Stun -= dt;
            for (int i = p.RecentDamage.Count - 1; i >= 0; i--)
                if (Time - p.RecentDamage[i].x > PlayerCfg.iFrameWindow) p.RecentDamage.RemoveAt(i);
            if (p.Hidden > 0f)
            {
                p.Hidden -= dt;
                if (p.Hidden <= 0f) ArriveJump();
                return;
            }
            if (p.Shield > 0f)
            {
                p.Shield -= dt;
                if (p.Shield <= 0f) BreakShield();
            }

            for (int i = 0; i < 4; i++)
            {
                if (input.IsPressed(i)) PressSlot(i, input);
                if (input.IsReleased(i)) ReleaseSlot(i, input);
            }

            // Удержание повторяет каст (IsSpammable / FullyAutomatic в Hades)
            if (p.Cast == null && !p.Dashing && p.Buffer < 0)
                for (int i = 0; i < 4; i++)
                    if (input.IsHeld(i) && !input.IsPressed(i) && Slots[i].Ready && Slots[i].Card.Spammable && Slots[i].Bat == null && Style(Slots[i].Card) != CastStyle.Long) { StartCast(i, input); break; }

            UpdateCast(dt, input);

            p.Moving = false;
            if (p.Dashing)
            {
                p.DashT += dt;
                float k = Mathf.Clamp01(p.DashT / p.DashDur), q = 1f - (1f - k) * (1f - k); // Ease.OutQuad
                p.Pos = Vector2.Lerp(p.DashFrom, p.DashTo, q);
                p.Vel = Vector2.zero;
                if (k >= 1f)
                {
                    p.Dashing = false;
                    OnDashEnd();
                    if (p.Buffer >= 0) { int b = p.Buffer; p.Buffer = -1; if (Slots[b].Ready) StartCast(b, input); }
                }
            }
            else
            {
                MovePlayer(dt, input);
            }

            if (p.Cast == null) { p.Lock = null; p.Aim = ComputeAimPreview(input); }

            CollideCircle(ref p.Pos, PlayerCfg.radius);
            UpdateShoes(dt);
        }

        /// <summary>Движение Загрея: разгон к целевой скорости (Acceleration 2000), взгляд поворачивается мгновенно.
        /// Внешняя скорость (отбрасывание, выпады) тормозит сцеплением Grip; пока она выше 400 — ввод не управляет.</summary>
        void MovePlayer(float dt, SimInput input)
        {
            var p = Player;
            float mul = MoveMultiplier();
            if (p.Stun > 0f) mul = 0f;
            var target = Vector2.zero;
            if (input.Move.sqrMagnitude > 0.0001f && mul > 0f)
                target = input.Move.normalized * (PlayerCfg.speed * mul * (p.Haste > 0f ? 1.5f : 1f));
            bool control = p.ExtVel.magnitude <= PlayerCfg.fullControlSpeed;
            if (!control) target = Vector2.zero;
            // замедление атакой действует сразу (эффект SPEED ограничивает скорость, а не разгон)
            float cap = PlayerCfg.speed * mul * (p.Haste > 0f ? 1.5f : 1f);
            if (mul < 1f && p.Vel.magnitude > cap) p.Vel = p.Vel.normalized * cap;
            p.Vel = mul <= 0f ? Vector2.zero : Vector2.MoveTowards(p.Vel, target, PlayerCfg.acceleration * dt);

            // внешняя скорость
            p.GripT -= dt;
            if (p.GripT <= 0f && p.GripMul < 1f) { p.GripMul = 1f; p.ExtVel = Vector2.zero; } // HaltOnEnd
            p.ExtVel = Vector2.MoveTowards(p.ExtVel, Vector2.zero, PlayerCfg.grip * p.GripMul * dt);

            p.Pos += (p.Vel + p.ExtVel) * dt;
            p.Moving = p.Vel.sqrMagnitude > 0.01f;
            if (p.Moving && p.Cast == null && p.Stun <= 0f && input.Move.sqrMagnitude > 0.0001f) p.Face = input.Move.normalized;
        }

        /// <summary>Скорость во время каста: розовые — ×0.35 (SwordWeapon), жёлтые — на месте (Cast, GunGrenadeToss).</summary>
        float MoveMultiplier()
        {
            var c = Player.Cast;
            if (c == null) return 1f;
            var card = Slots[c.Slot].Card;
            if (card == null) return 1f;
            return Style(card) switch
            {
                CastStyle.Quick => card.HoldTime > 0f && c.Phase == CastPhase.Hold ? 0f : CastCfg.quickMoveMultiplier,
                CastStyle.Long => 0f,
                _ => c.Phase == CastPhase.Soft ? 1f : 0f,
            };
        }

        /// <summary>Направление стрелки под Ace, пока нет каста.</summary>
        Vector2 ComputeAimPreview(SimInput input)
        {
            var p = Player;
            if (input.UseMouse && input.HasCursor)
            {
                var d = input.Cursor - p.Pos;
                if (d.sqrMagnitude > 0.01f) return d.normalized;
            }
            return p.Face;
        }

        // ---------- Наведение по-Hades ----------

        /// <summary>Направление удара карты.
        /// Мышь: снаряды летят ровно в курсор (AutoLockWithMouse = false), враг под курсором — во врага.
        /// Геймпад: автоприцел Hades — из 4 ближайших врагов в радиусе берётся ближайший в пределах ±угла от стика или взгляда,
        /// иначе ближайший вообще. Синие карты — по движению или взгляду.</summary>
        public Vector2 AimFor(CardDef card, SimInput input)
        {
            var p = Player;
            if (card.IsMovement)
            {
                if (input.UseMouse && input.HasCursor && PlayerCfg.dashAtCursor > 0f)
                {
                    var c = input.Cursor - p.Pos;
                    if (c.sqrMagnitude > 0.01f) return c.normalized;
                }
                return input.Move.sqrMagnitude > 0.0001f ? input.Move.normalized : p.Face;
            }
            if (!card.Aims) return p.Face;

            if (input.UseMouse && input.HasCursor)
            {
                var under = EnemyUnderCursor(input.Cursor);
                p.Lock = under;
                if (under != null) return (under.Pos - p.Pos).normalized;
                var c = input.Cursor - p.Pos;
                return c.sqrMagnitude > 0.01f ? c.normalized : p.Face;
            }

            var search = input.Move.sqrMagnitude > 0.0001f ? input.Move.normalized : p.Face;
            p.Lock = FindLockTarget(search, card.LockRange, card.LockArc);
            return p.Lock != null ? (p.Lock.Pos - p.Pos).normalized : search;
        }

        /// <summary>GetNearestEnemyArgs из Hades (Powers.lua:1442): 4 ближайших врага в радиусе (неуязвимые не в счёт),
        /// из них ближайший в пределах ±arc от направления; если таких нет — ближайший вообще. Расстояние решает, угол — только фильтр.</summary>
        public Enemy FindLockTarget(Vector2 dir, float range, float arcDeg)
        {
            var near = new List<(float d, Enemy e)>();
            foreach (var e in Enemies)
            {
                if (!e.Alive || e.SpawnInvuln > 0f) continue;
                float d = (e.Pos - Player.Pos).magnitude - e.R;
                if (d <= range) near.Add((d, e));
            }
            near.Sort((a, b) => a.d.CompareTo(b.d));
            if (near.Count > CastCfg.lockCandidates) near.RemoveRange(CastCfg.lockCandidates, near.Count - CastCfg.lockCandidates);
            foreach (var (_, e) in near)
            {
                var d = e.Pos - Player.Pos;
                float angle = d.sqrMagnitude < 0.0001f ? 0f : Vector2.Angle(dir, d);
                if (angle <= arcDeg) return e;
            }
            return near.Count > 0 ? near[0].e : null;
        }

        public Enemy EnemyUnderCursor(Vector2 cursor)
        {
            Enemy best = null;
            float bestD = float.MaxValue;
            foreach (var e in Enemies)
            {
                if (!e.Alive) continue;
                float d = (e.Pos - cursor).magnitude;
                if (d <= e.R + 0.45f && d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        // ---------- Столкновения ----------

        public void CollideCircle(ref Vector2 pos, float r)
        {
            foreach (var o in Obstacles)
            {
                if ((pos - o.Pos).sqrMagnitude > (o.R + r) * (o.R + r)) continue;
                if (o.Round)
                {
                    var d = pos - o.Pos;
                    float m = o.R + r, l = d.magnitude;
                    if (l < m) pos = o.Pos + (l > 0.0001f ? d / l : Vector2.up) * m;
                }
                else Geo.PushOut(o.Poly, ref pos, r);
            }
            KeepOnFloor(ref pos, r);
        }

        /// <summary>Держит круг на полу комнаты: внутри любого контура пола, иначе — к ближайшему краю.</summary>
        void KeepOnFloor(ref Vector2 pos, float r)
        {
            var floor = Room.floor;
            if (floor.Length == 0) return;
            RoomDef.Poly home = null;
            foreach (var f in floor)
                if (Geo.InPoly(f.p, pos)) { home = f; break; }
            if (home != null)
            {
                // контуры пола могут перекрываться: край одного внутри другого — не стена
                bool ok = true;
                for (int i = 0; i < 8 && ok; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    if (!OnFloor(pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r)) ok = false;
                }
                if (ok) return;
                Geo.KeepInside(home.p, ref pos, r);
                return;
            }
            RoomDef.Poly best = null;
            float bestD = float.MaxValue;
            foreach (var f in floor)
            {
                Geo.ClosestOnEdge(f.p, pos, out float d);
                if (d < bestD) { bestD = d; best = f; }
            }
            if (best != null) Geo.KeepInside(best.p, ref pos, r);
        }

        bool OnFloor(Vector2 p)
        {
            if (Room.floor.Length == 0) return true;
            foreach (var f in Room.floor)
                if (Geo.InPoly(f.p, p)) return true;
            return false;
        }

        public bool InsideRoom(Vector2 p, float r) => OnFloor(p) && !HitsObstacle(p, r);

        bool HitsObstacle(Vector2 p, float r)
        {
            foreach (var o in Obstacles)
            {
                if ((p - o.Pos).sqrMagnitude >= (o.R + r) * (o.R + r)) continue;
                if (o.Round || Geo.Overlaps(o.Poly, p, r)) return true;
            }
            return false;
        }

        /// <summary>Видит ли a точку b: отрезок не пересекает препятствия (AIRequireProjectileLineOfSight).</summary>
        public bool LineOfSight(Vector2 a, Vector2 b, float buffer = 0f)
        {
            foreach (var o in Obstacles)
            {
                float d = (Geo.ClosestOnSegment(a, b, o.Pos) - o.Pos).magnitude;
                if (d > o.R + buffer) continue;
                if (o.Round) return false;
                if (Geo.SegmentHit(o.Poly, a, b, out _)) return false;
            }
            return true;
        }

        /// <summary>Точка рывка/телепорта: по прямой, но не в стену и не внутрь препятствия.</summary>
        Vector2 TravelTarget(Vector2 from, Vector2 dir, float dist)
        {
            var to = from + dir * dist;
            var r = PlayerCfg.radius;
            for (int i = 0; i < 40; i++)
            {
                if (InsideRoom(to, r)) break;
                dist -= 0.2f; to = from + dir * Mathf.Max(0f, dist);
            }
            CollideCircle(ref to, r);
            return to;
        }

        void Separate()
        {
            for (int i = 0; i < Enemies.Count; i++)
            {
                var a = Enemies[i];
                if (a.Dead || a.State == AiState.Spawn) continue;
                for (int j = i + 1; j < Enemies.Count; j++)
                {
                    var b = Enemies[j];
                    if (b.Dead || b.State == AiState.Spawn) continue;
                    var d = b.Pos - a.Pos;
                    float m = a.R + b.R, l = d.magnitude;
                    if (l < m && l > 0.0001f) { var n = d / l * ((m - l) * 0.5f); a.Pos -= n; b.Pos += n; }
                }
                var p = Player;
                if (!p.Dashing && p.Hidden <= 0f)
                {
                    var d = a.Pos - p.Pos;
                    float m = a.R + PlayerCfg.radius, l = d.magnitude;
                    if (l < m && l > 0.0001f) { var n = d / l * (m - l); a.Pos += n * 0.7f; p.Pos -= n * 0.3f; }
                }
                CollideCircle(ref a.Pos, a.R);
            }
            CollideCircle(ref Player.Pos, PlayerCfg.radius);
        }

        // ---------- Урны и двери ----------

        /// <summary>Удар картой по месту: разбивает урны и поджигает фитили взрывных кубов.</summary>
        public void HitRoomObjects(Vector2 at, float radius)
        {
            ArmBlastCubes(at, radius);
            foreach (var b in Breakables)
            {
                if (b.Broken || (b.Pos - at).magnitude > radius + b.R + 0.1f) continue;
                b.Broken = true;
                Obstacles.Remove(b.Body);
                Events.Add(new SimEvent { Kind = EventKind.PropBroken, Pos = b.Pos, Radius = b.R });
            }
        }

        /// <summary>Расстояние, с которого дверь засчитывается: двери Hades стоят в стене, до них не дойти вплотную.</summary>
        public float DoorReach(Vector2 door)
        {
            float best = float.MaxValue;
            foreach (var f in Room.floor)
            {
                Geo.ClosestOnEdge(f.p, door, out float d);
                if (Geo.InPoly(f.p, door)) d = 0f;
                best = Mathf.Min(best, d);
            }
            return best + PlayerCfg.radius + 0.6f;
        }

        void UpdateDoors()
        {
            if (!DoorsOpen || Player.Hidden > 0f) return;
            foreach (var x in Room.exits)
                if ((Player.Pos - x.pos).magnitude <= DoorReach(x.pos)) { EnterNextRoom(); return; }
        }

        public Enemy SpawnEnemy(EnemyKind kind, Vector2 pos, float delay = 0f, bool preSpawned = false)
        {
            var e = new Enemy
            {
                Id = nextId++, Kind = kind, Pos = pos, Home = pos, State = AiState.Spawn,
                T = preSpawned ? 0f : ArenaCfg.spawnTime + delay, PreSpawned = preSpawned,
                R = kind switch { EnemyKind.Ghost => GhostCfg.radius, EnemyKind.Stomper => StomperCfg.radius, EnemyKind.Thug => ThugCfg.radius, _ => WitchCfg.radius },
                Cd = Rand(0.3f, 1f),
            };
            e.MaxHp = e.Hp = kind switch { EnemyKind.Ghost => GhostCfg.hp, EnemyKind.Stomper => StomperCfg.hp, EnemyKind.Thug => ThugCfg.hp, _ => WitchCfg.hp };
            Enemies.Add(e);
            Events.Add(new SimEvent { Kind = EventKind.EnemySpawned, Pos = pos, Enemy = e });
            return e;
        }

        float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        int NextId() => nextId++;
    }
}
