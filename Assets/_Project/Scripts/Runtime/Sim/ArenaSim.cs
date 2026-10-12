using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Вся логика арены без отрисовки: Ace, карты, враги, обозначения атак, волны.
    /// Координаты — плоскость пола (x, z мира) в юнитах DandyAce.</summary>
    public sealed partial class ArenaSim
    {
        public readonly PlayerTuning PlayerCfg;
        public readonly WastrelTuning WastrelCfg;
        public readonly CasketTuning CasketCfg;
        public readonly ArenaTuning ArenaCfg;
        /// <summary>Арена из Hades II; null — классическая прямоугольная комната 20×14 с четырьмя колоннами.</summary>
        public readonly ArenaLayout Layout;

        public readonly Player Player = new();
        public readonly Slot[] Slots = { new(), new(), new(), new() };
        public readonly CardId?[] Reserve = new CardId?[15];
        public readonly List<Enemy> Enemies = new();
        public readonly List<Effect> Effects = new();
        public readonly List<Obstacle> Obstacles = new();
        public readonly List<SimEvent> Events = new();

        public int Wave;
        public int Kills;
        public bool GameOver;
        public float Time;
        public bool WavesEnabled = true;
        float waveTimer;
        int nextId = 1;
        readonly System.Random rng;

        public ArenaSim(int seed = 1, PlayerTuning player = null, WastrelTuning wastrel = null, CasketTuning casket = null, ArenaTuning arena = null,
            ArenaLayout layout = null)
        {
            rng = new System.Random(seed);
            PlayerCfg = player ?? new PlayerTuning();
            WastrelCfg = wastrel ?? new WastrelTuning();
            CasketCfg = casket ?? new CasketTuning();
            ArenaCfg = arena ?? new ArenaTuning();
            Layout = layout;
            SetDefaultDeck();
            Restart();
        }

        /// <summary>По умолчанию в слотах — посох Мелинои (Атака, Спецатака, Каст, Рывок), все 15 карт — в запасе.
        /// staff = false — прежняя колода карт (в запасе сначала 11 карт, потом посох).</summary>
        public void SetDefaultDeck(bool staff = true)
        {
            CardId[] staffSet = { CardId.StaffAttack, CardId.StaffSpecial, CardId.StaffCast, CardId.StaffDash };
            CardId[] cardMains = { CardId.TwinDaggers, CardId.Crescendo, CardId.BlackHole, CardId.JumpScare };
            CardId[] cardRest =
            {
                CardId.FiveOfAKind, CardId.Roses, CardId.Dio, CardId.Dazzling, CardId.Starburst, CardId.Aegis,
                CardId.MasterfulJump, CardId.Dash, CardId.BlazingSprint, CardId.ShockingArrival, CardId.EscapeArtist,
            };
            var mains = staff ? staffSet : cardMains;
            for (int i = 0; i < 4; i++) { Slots[i].Main = mains[i]; Slots[i].Up = null; Slots[i].Reset(); }
            var rest = new System.Collections.Generic.List<CardId>();
            if (staff) { rest.AddRange(cardMains); rest.AddRange(cardRest); }
            else { rest.AddRange(cardRest); rest.AddRange(staffSet); }
            for (int i = 0; i < Reserve.Length; i++) Reserve[i] = i < rest.Count ? rest[i] : null;
            Player.Staff.Reset();
        }

        public void Restart()
        {
            var p = Player;
            p.Pos = Layout != null ? Layout.playerStart : new Vector2(0f, -3.5f);
            p.Face = Layout != null && p.Pos.sqrMagnitude > 0.01f ? -p.Pos.normalized : new Vector2(0f, 1f); // лицом к центру комнаты
            p.Aim = p.Face;
            p.RecentDamage.Clear();
            p.Hp = PlayerCfg.hp; p.HitInvuln = p.BuffInvuln = p.DashInvuln = p.HitStun = p.Haste = p.Slow = p.Shoes = p.Shield = p.Hidden = 0f;
            p.Cast = null; p.Buffer = -1; p.Lock = null; p.Dashing = false; p.KnockT = 0f; p.ShoeTrail = null;
            foreach (var s in Slots) s.Reset();
            p.Staff.Reset();
            Enemies.Clear(); Effects.Clear(); Events.Clear();
            Obstacles.Clear();
            if (Layout == null)
                foreach (var o in new[] { new Vector2(-5.5f, 3f), new Vector2(5.5f, 3f), new Vector2(-6f, -3.5f), new Vector2(6f, -3.5f) })
                    Obstacles.Add(new Obstacle { Pos = o, R = 0.7f });
            Wave = 0; Kills = 0; GameOver = false; Time = 0f; waveTimer = 0f; pending.Clear(); spawnTimer = 0f;
            if (FormsMode) ResetForms();
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
            Enemies.RemoveAll(e => e.Dead && e.DeathT <= 0f);
            Effects.RemoveAll(f => f.Done);
            if (WavesEnabled) UpdateWaves(dt);
        }

        // ---------- Слоты ----------

        void UpdateSlots(float dt)
        {
            foreach (var s in Slots)
            {
                if (IsStaff(s.Main)) continue; // кулдауны посоха ведёт ArenaSim.Staff
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
            Player.Cast = null; Player.Buffer = -1; Player.Staff.Reset();
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
            if (FormsMode) { UpdatePlayerForms(dt, input); return; }
            var p = Player;
            p.HitInvuln -= dt; p.BuffInvuln -= dt; p.DashInvuln -= dt; p.HitStun -= dt; p.Haste -= dt; p.Slow -= dt; p.Shoes -= dt;
            if (p.Hidden > 0f)
            {
                p.Hidden -= dt;
                if (p.Hidden <= 0f) ArriveJump();
                return;
            }
            var before = p.Pos;
            if (p.Shield > 0f)
            {
                p.Shield -= dt;
                if (p.Shield <= 0f) BreakShield();
            }

            UpdateStaffTimers(dt, input);
            for (int i = 0; i < 4; i++)
            {
                if (IsStaffSlot(i)) { if (input.IsPressed(i)) StaffPress(i, input); continue; }
                if (S.Busy || S.Lock > 0f) { if (input.IsPressed(i)) p.Buffer = i; continue; } // посох занят — карта подождёт
                if (input.IsPressed(i)) PressSlot(i, input);
                if (input.IsReleased(i)) ReleaseSlot(i, input);
            }

            // Удержание повторяет каст (IsSpammable) — только у карт без прицела: у прицельных зажатие — это заряд с прицеливанием
            if (p.Cast == null && !p.Dashing && p.Buffer < 0)
                for (int i = 0; i < 4; i++)
                    if (input.IsHeld(i) && !input.IsPressed(i) && Slots[i].Ready && Slots[i].Card.Spammable && !ChargesOnHold(Slots[i].Card) && Slots[i].Bat == null) { StartCast(i, input); break; }

            UpdateCast(dt, input);
            p.Moving = false;
            UpdateStaff(dt, input);
            p.Pos += S.Vel * dt; // отдача спецатаки
            if (p.Dashing)
            {
                p.DashT += dt;
                float k = Mathf.Clamp01(p.DashT / p.DashDur), q = 1f - (1f - k) * (1f - k); // Ease.OutQuad
                p.Pos = Vector2.Lerp(p.DashFrom, p.DashTo, q);
                if (k >= 1f)
                {
                    p.Dashing = false;
                    OnDashEnd();
                    if (p.Buffer >= 0) { int b = p.Buffer; p.Buffer = -1; if (Slots[b].Ready) StartCast(b, input); }
                }
            }
            else if (p.KnockT > 0f)
            {
                p.KnockT -= dt;
                p.Pos += p.KnockVel * dt;
            }
            else if (p.Cast == null && p.HitStun <= 0f && !StaffBlocksMove(input) && S.Move is not (StaffMove.Blink or StaffMove.Sprint) && input.Move.sqrMagnitude > 0.0001f)
            {
                // Hades: скорость сразу полная, наклон стика её не меняет
                var dir = input.Move.normalized;
                float speed = PlayerCfg.speed * (p.Haste > 0f ? 1.5f : 1f) * (p.Slow > 0f ? CasketCfg.slow : 1f) * StaffSpeedMul;
                p.Pos += dir * (speed * dt);
                p.Moving = true;
                if ((p.Lock == null || !p.Lock.Alive) && !S.Busy && !StaffBlocksTurn) p.Face = dir;
            }

            if (p.Lock != null && (!p.Lock.Alive || (p.Cast == null && !S.Busy && input.Held == 0))) p.Lock = null;
            if (p.Lock != null && !S.Busy && !StaffBlocksTurn && p.HitStun <= 0f) p.Face = (p.Lock.Pos - p.Pos).normalized;
            if (S.Move is StaffMove.Swing or StaffMove.DashStrike or StaffMove.OmegaAttack or StaffMove.Special) p.Aim = S.Dir;
            else if (p.Cast == null) p.Aim = ComputeAimPreview(input);

            CollideCircle(ref p.Pos, PlayerCfg.radius, before);
            UpdateShoes(dt);
        }

        static bool ChargesOnHold(CardDef c) => c.Aims && !c.IsMovement;

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

        /// <summary>Направление удара карты. Геймпад: цель автолока (наименьшее отклонение от стика в пределах угла и дальности),
        /// иначе стик, иначе взгляд. Мышь: враг под курсором, иначе направление на курсор. Синие карты — по движению или взгляду.</summary>
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
            return AimWith(card.LockRange, card.LockArc, input);
        }

        /// <summary>Наведение по-Hades: мышь — враг под курсором, иначе курсор; геймпад — FindLockTarget по дальности и дуге.</summary>
        Vector2 AimWith(float range, float arc, SimInput input)
        {
            var p = Player;
            if (input.UseMouse && input.HasCursor)
            {
                var under = EnemyUnderCursor(input.Cursor);
                p.Lock = under;
                if (under != null) return (under.Pos - p.Pos).normalized;
                var c = input.Cursor - p.Pos;
                return c.sqrMagnitude > 0.01f ? c.normalized : p.Face;
            }

            bool stick = input.Move.sqrMagnitude > 0.15f;
            var search = input.Move.sqrMagnitude > 0.0001f ? input.Move.normalized : p.Face;
            if (stick || p.Lock == null || !p.Lock.Alive) p.Lock = FindLockTarget(search, range, arc);
            return p.Lock != null ? (p.Lock.Pos - p.Pos).normalized : search;
        }

        /// <summary>FindLockTarget из Hades II: кандидаты ближе дальности (до края тела) и в пределах угла, выигрывает наименьший угол.</summary>
        public Enemy FindLockTarget(Vector2 dir, float range, float arcDeg)
        {
            Enemy best = null;
            float bestAngle = float.MaxValue;
            foreach (var e in Enemies)
            {
                if (!e.Alive) continue;
                var d = e.Pos - Player.Pos;
                float dist = d.magnitude - e.R;
                if (dist > range) continue;
                float angle = d.sqrMagnitude < 0.0001f ? 0f : Vector2.Angle(dir, d);
                if (arcDeg < 180f && angle > arcDeg) continue;
                if (angle < bestAngle) { bestAngle = angle; best = e; }
            }
            return best;
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

        Vector2 RoomMin => Layout != null ? Layout.boundsMin : new Vector2(-ArenaCfg.halfWidth, -ArenaCfg.halfDepth);
        Vector2 RoomMax => Layout != null ? Layout.boundsMax : new Vector2(ArenaCfg.halfWidth, ArenaCfg.halfDepth);

        public void CollideCircle(ref Vector2 pos, float r)
        {
            foreach (var o in Obstacles)
            {
                var d = pos - o.Pos;
                float m = o.R + r, l = d.magnitude;
                if (l < m) pos = o.Pos + (l > 0.0001f ? d / l : Vector2.up) * m;
            }
            if (Layout != null)
                // два прохода: в углу между двумя препятствиями первое может втолкнуть во второе
                for (int pass = 0; pass < 2; pass++)
                    foreach (var w in Layout.walls)
                    {
                        if (!w.Overlaps(pos, r)) continue;
                        var q = w.Nearest(pos, out var n);
                        if (w.Contains(pos)) pos = q + n * r;
                        else
                        {
                            var d = pos - q;
                            float l = d.magnitude;
                            pos = q + (l > 0.0001f ? d / l : n) * r;
                        }
                    }
            Vector2 mn = RoomMin, mx = RoomMax;
            pos.x = Mathf.Clamp(pos.x, mn.x + r, mx.x - r);
            pos.y = Mathf.Clamp(pos.y, mn.y + r, mx.y - r);
        }

        /// <summary>То же, но если круг остался зажат в щели уже своего диаметра (выталкивания двух препятствий спорят),
        /// возвращает его на прошлую позицию <paramref name="from"/>.</summary>
        public void CollideCircle(ref Vector2 pos, float r, Vector2 from)
        {
            CollideCircle(ref pos, r);
            if (Layout != null && LayoutBlocks(pos, r * 0.98f) && !LayoutBlocks(from, r * 0.98f)) pos = from;
        }

        /// <summary>Точка в пределах комнаты (для снарядов). Препятствия проверяет <see cref="BlocksShot"/>.</summary>
        public bool InsideRoom(Vector2 p, float r)
        {
            Vector2 mn = RoomMin, mx = RoomMax;
            return p.x >= mn.x + r && p.x <= mx.x - r && p.y >= mn.y + r && p.y <= mx.y - r;
        }

        /// <summary>Круг задевает препятствие для ходьбы (колонны, стены, вода, обрывы).</summary>
        public bool HitsObstacle(Vector2 p, float r)
        {
            foreach (var o in Obstacles)
                if ((p - o.Pos).sqrMagnitude < (o.R + r) * (o.R + r)) return true;
            if (Layout != null)
                foreach (var w in Layout.walls)
                    if (w.Overlaps(p, r)) return true;
            return false;
        }

        /// <summary>Снаряд упирается в препятствие. Над водой и обрывами (StopsProjectiles = false) пролетает.</summary>
        bool BlocksShot(Vector2 p, float r)
        {
            foreach (var o in Obstacles)
                if ((p - o.Pos).sqrMagnitude < (o.R + r) * (o.R + r)) return true;
            if (Layout != null)
                foreach (var w in Layout.walls)
                    if (w.projectiles && w.Overlaps(p, r)) return true;
            return false;
        }

        /// <summary>Точка рывка/телепорта по прямой. Через колонну можно перескочить (если за ней свободно),
        /// через стены и воду арены — нет: рывок останавливается перед ними.</summary>
        Vector2 TravelTarget(Vector2 from, Vector2 dir, float dist)
        {
            var r = PlayerCfg.radius;
            if (Layout != null)
            {
                const float step = 0.1f;
                float d = 0f;
                while (d + step <= dist && !LayoutBlocks(from + dir * (d + step), r)) d += step;
                dist = d;
            }
            var to = from + dir * dist;
            for (int i = 0; i < 20; i++)
            {
                if (!HitsObstacle(to, r)) break;
                dist -= 0.2f; to = from + dir * Mathf.Max(0f, dist);
            }
            Vector2 mn = RoomMin, mx = RoomMax;
            to.x = Mathf.Clamp(to.x, mn.x + r, mx.x - r);
            to.y = Mathf.Clamp(to.y, mn.y + r, mx.y - r);
            return to;
        }

        bool LayoutBlocks(Vector2 p, float r)
        {
            foreach (var w in Layout.walls)
                if (w.Overlaps(p, r)) return true;
            return false;
        }

        readonly List<Vector2> beforeSeparate = new();

        void Separate()
        {
            beforeSeparate.Clear();
            foreach (var e in Enemies) beforeSeparate.Add(e.Pos);
            var playerBefore = Player.Pos;
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
                if (!p.Dashing && p.Hidden <= 0f && !StaffPhasing) // в рывке посоха — сквозь врагов (NoUnitCollision)
                {
                    var d = a.Pos - p.Pos;
                    float m = a.R + PlayerCfg.radius, l = d.magnitude;
                    if (l < m && l > 0.0001f) { var n = d / l * (m - l); a.Pos += n * 0.7f; p.Pos -= n * 0.3f; }
                }
                CollideCircle(ref a.Pos, a.R, beforeSeparate[i]);
            }
            CollideCircle(ref Player.Pos, PlayerCfg.radius, playerBefore);
        }

        // ---------- Волны ----------

        readonly List<EnemyKind> pending = new();
        float spawnTimer;

        /// <summary>Волны как в бою Эреба Hades II: первая стоит в комнате заранее и просыпается через WakeUpDelay;
        /// следующая — через 0.5 с, когда живых осталось ≤ WaveRequiredRemainingCount (выжившие сразу в бой);
        /// враги выходят по одному через 0.2–0.4 с, пока «вес» живых не больше ActiveEnemyCap 4 (Casket — 0.8).</summary>
        void UpdateWaves(float dt)
        {
            var c = ArenaCfg;
            if (FormsMode && RewardPending) return; // идёт выбор карточки
            if (Wave == 0) { StartWave(true); return; }
            if (pending.Count > 0)
            {
                spawnTimer -= dt;
                if (spawnTimer > 0f || ActiveWeight() + Weight(pending[0]) > c.activeCap + 0.001f) return;
                var e = SpawnEnemy(pending[0], RandomSpawn());
                e.SpawnAggro = true; // новые враги волны выходят уже агрессивными
                pending.RemoveAt(0);
                spawnTimer = Rand(c.spawnIntervalMin, c.spawnIntervalMax);
                return;
            }
            // режим форм: каждая вторая волна добивается до конца, потом — выбор карточки
            bool reward = FormsMode && Wave % c.wavesPerReward == 0;
            if (AliveCount() > (reward ? 0 : c.waveRemaining)) { waveTimer = c.waveDelay; return; }
            if (reward && rewardWave != Wave) { rewardWave = Wave; OpenReward(); return; }
            waveTimer -= dt;
            if (waveTimer <= 0f) StartWave(false);
        }

        void StartWave(bool prespawned)
        {
            var c = ArenaCfg;
            Wave++;
            foreach (var o in Enemies) if (o.Alive && !o.Aggro) MakeAggro(o);
            var kinds = WaveKinds(Wave);
            if (prespawned)
                foreach (var k in kinds)
                {
                    // стоит в комнате с начала: без появления, просыпается через WakeUpDelay + реакция
                    var e = SpawnEnemy(k, RandomSpawn());
                    e.State = AiState.Wander; e.T = 0f;
                    e.React = c.wakeUpDelay + Rand(c.wakeReactionMin, c.wakeReactionMax);
                }
            else { pending.AddRange(kinds); spawnTimer = 0f; }
            Events.Add(new SimEvent { Kind = EventKind.WaveStart, Value = Wave });
        }

        /// <summary>Состав: первые три волны — как FIntroFight (Brawler → 3 Mage → Brawler + Mage, без Guard), дальше —
        /// случайная волна на бюджет сложности 55 (Wastrel 18, Casket 12), как GeneratedF.</summary>
        List<EnemyKind> WaveKinds(int wave)
        {
            var c = ArenaCfg;
            switch (wave)
            {
                case 1: return new List<EnemyKind> { EnemyKind.Wastrel };
                case 2: return new List<EnemyKind> { EnemyKind.Casket, EnemyKind.Casket, EnemyKind.Casket };
                case 3: return new List<EnemyKind> { EnemyKind.Wastrel, EnemyKind.Casket };
            }
            var list = new List<EnemyKind>();
            int budget = c.budget + (FormsMode ? Rewards * c.budgetPerReward : 0);
            while (budget >= Mathf.Min(c.wastrelCost, c.casketCost))
            {
                bool w = budget >= c.wastrelCost && (budget < c.casketCost || rng.NextDouble() < 0.5);
                list.Add(w ? EnemyKind.Wastrel : EnemyKind.Casket);
                budget -= w ? c.wastrelCost : c.casketCost;
            }
            return list;
        }

        float Weight(EnemyKind k) => k == EnemyKind.Casket ? ArenaCfg.casketWeight : 1f;

        float ActiveWeight()
        {
            float w = 0f;
            foreach (var e in Enemies) if (!e.Dead) w += Weight(e.Kind);
            return w;
        }

        Vector2 RandomSpawn()
        {
            if (Layout != null && Layout.spawns.Count > 0) return LayoutSpawn();
            for (int t = 0; t < 60; t++)
            {
                var p = new Vector2(Rand(-ArenaCfg.halfWidth + 1.5f, ArenaCfg.halfWidth - 1.5f), Rand(-ArenaCfg.halfDepth + 1.5f, ArenaCfg.halfDepth - 1.5f));
                if ((p - Player.Pos).magnitude < 5f || HitsObstacle(p, 0.6f)) continue;
                bool crowded = false;
                foreach (var e in Enemies) if ((e.Pos - p).sqrMagnitude < 1.5f) crowded = true;
                if (!crowded) return p;
            }
            return new Vector2(0f, ArenaCfg.halfDepth - 2f);
        }

        /// <summary>Случайная точка спавна комнаты (EnemyPoint из Hades II): не ближе 5 юнитов к Ace и не в толпе.</summary>
        Vector2 LayoutSpawn()
        {
            var list = Layout.spawns;
            for (int t = 0; t < 60; t++)
            {
                var p = list[rng.Next(list.Count)];
                if ((p - Player.Pos).magnitude < 5f) continue;
                bool crowded = false;
                foreach (var e in Enemies) if ((e.Pos - p).sqrMagnitude < 1.5f) crowded = true;
                if (!crowded) return p;
            }
            // всё рядом с Ace — самая дальняя точка
            var best = list[0];
            foreach (var p in list) if ((p - Player.Pos).sqrMagnitude > (best - Player.Pos).sqrMagnitude) best = p;
            return best;
        }

        public Enemy SpawnEnemy(EnemyKind kind, Vector2 pos, float delay = 0f)
        {
            var e = new Enemy
            {
                Id = nextId++, Kind = kind, Pos = pos, Home = pos, Dir = pos, State = AiState.Spawn, T = ArenaCfg.spawnTime + delay,
                R = kind == EnemyKind.Wastrel ? WastrelCfg.radius : CasketCfg.radius,
                Cd = Rand(0.3f, 1f),
            };
            e.MaxHp = e.Hp = kind == EnemyKind.Wastrel ? WastrelCfg.hp : CasketCfg.hp;
            Enemies.Add(e);
            Events.Add(new SimEvent { Kind = EventKind.EnemySpawned, Pos = pos, Enemy = e });
            return e;
        }

        float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        int NextId() => nextId++;
    }
}
