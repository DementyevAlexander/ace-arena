using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Волны по энкаунтеру комнаты, как в Тартаре Hades: сначала заданные волны (первая комната — EnemyIntroFight01,
    /// EncounterData.lua:3058), в следующих комнатах — генерация GeneratedTartarus: бюджет max(10, 30 + 11 × глубина), 1–3 волны
    /// (100% / 50+50 / 30+15+55), враги появляются раз в 0.175–0.225 с, одновременно не больше ⌊2.3 + 0.35 × глубина⌋ (≤ 8),
    /// следующая волна — через 1 с после гибели предыдущей. Кого генерировать — набор комнаты (Encounter.pool): у каждой комнаты
    /// своя комбинация Thug, Witch, Wastrel и Casket. Wastrel и Casket (Hades II) выходят волной сразу в бой.
    /// Набор волн пройден — открываются двери; в режиме форм на землю падает карточка, каждая взятая делает волны дороже на 8.</summary>
    public sealed partial class ArenaSim
    {
        struct PendingSpawn
        {
            public EnemyKind Kind;
            public bool PreSpawned;
        }

        static readonly (EnemyKind kind, float rating)[] TartarusPool = { (EnemyKind.Thug, 10f), (EnemyKind.Witch, 5f) };

        readonly Queue<PendingSpawn> pending = new();
        readonly List<RoomDef.Wave> plan = new();
        int planIndex;
        float waveTimer, spawnTimer, intervalMin = 0.175f, intervalMax = 0.225f;
        float activeCap = 10f;
        public int Depth { get; private set; }

        RoomDef.Encounter Enc => Room.encounter;

        static bool IsHades2(EnemyKind k) => k is EnemyKind.Wastrel or EnemyKind.Casket;

        void ResetEncounter()
        {
            pending.Clear();
            plan.Clear();
            planIndex = 0;
            Depth = 1;
            var enc = Enc;
            if (enc != null && enc.waves != null) plan.AddRange(enc.waves);
            activeCap = enc != null && enc.activeCap > 0 ? enc.activeCap : 10;
            waveTimer = plan.Count > 0 && plan[0].preSpawned ? 0f : 1.2f;
            spawnTimer = 0f;
            if (WavesEnabled && !RewardPending && plan.Count > 0 && plan[0].preSpawned) StartNextWave();
        }

        /// <summary>Новая комната за дверью: волны генерируются на заданной глубине.</summary>
        void StartGeneratedEncounter(int depth)
        {
            pending.Clear();
            plan.Clear();
            planIndex = 0;
            Depth = depth;
            var enc = Enc;
            activeCap = enc != null && enc.activeCap > 0 ? enc.activeCap : 10;
            waveTimer = 1.2f;
            spawnTimer = 0f;
        }

        void UpdateEncounter(float dt)
        {
            if (FormsMode && RewardPending) return; // идёт выбор карточки
            if (Wave == 0 && plan.Count > 0 && plan[0].preSpawned) { StartNextWave(); return; }
            if (pending.Count > 0)
            {
                spawnTimer -= dt;
                var next = pending.Peek();
                if (spawnTimer > 0f || ActiveWeight() + Weight(next.Kind) > activeCap + 0.001f) return;
                pending.Dequeue();
                var e = SpawnEnemy(next.Kind, PickSpawnPoint(next.PreSpawned, next.Kind), 0f, next.PreSpawned);
                if (IsHades2(next.Kind)) e.SpawnAggro = true; // враги Hades II выходят волной уже агрессивными, как в Эребе
                spawnTimer = Rand(intervalMin, intervalMax);
                return;
            }
            bool setDone = Wave > 0 && plan.Count > 0 && planIndex >= plan.Count;
            if (AliveCount() > 0) return;
            if (setDone)
            {
                // «Арена пройдена» держится doorsDelay, потом загорается дверь
                if (!DoorsOpen)
                {
                    if (doorTimer <= 0f)
                    {
                        Events.Add(new SimEvent { Kind = EventKind.ArenaCleared, Value = ArenaCfg.doorsDelay });
                        if (FormsMode) DropLoot(); // награда комнаты — карточка на земле
                    }
                    doorTimer += dt;
                    if (doorTimer >= ArenaCfg.doorsDelay) { DoorsOpen = true; Events.Add(new SimEvent { Kind = EventKind.DoorsOpen }); }
                }
                return;
            }
            waveTimer -= dt;
            if (waveTimer > 0f) return;
            StartNextWave();
        }

        float Weight(EnemyKind k) => k == EnemyKind.Casket ? ArenaCfg.casketWeight : 1f;

        float ActiveWeight()
        {
            float w = 0f;
            foreach (var e in Enemies) if (!e.Dead) w += Weight(e.Kind);
            return w;
        }

        void StartNextWave()
        {
            if (planIndex >= plan.Count) GeneratePlan();
            if (plan.Count == 0) return;
            var w = plan[planIndex++];
            Wave++;
            intervalMin = w.intervalMin; intervalMax = w.intervalMax;
            foreach (var name in w.enemies)
                if (TryKind(name, out var kind)) pending.Enqueue(new PendingSpawn { Kind = kind, PreSpawned = w.preSpawned });
            spawnTimer = 0f;
            // заранее расставленные враги стоят в комнате сразу
            if (w.preSpawned)
                while (pending.Count > 0)
                {
                    var s = pending.Dequeue();
                    var e = SpawnEnemy(s.Kind, PickSpawnPoint(true, s.Kind), 0f, true);
                    // Hades II: просыпается через WakeUpDelay и реакцию
                    if (e.Kind is EnemyKind.Wastrel or EnemyKind.Casket)
                    {
                        e.State = AiState.Wander; e.T = 0f;
                        e.React = ArenaCfg.wakeUpDelay + Rand(ArenaCfg.wakeReactionMin, ArenaCfg.wakeReactionMax);
                    }
                }
            waveTimer = Enc?.waveDelay ?? ArenaCfg.waveDelay;
            if (planIndex >= plan.Count) waveTimer += 1.2f; // после последней волны набора — пауза «комната пройдена»
            Events.Add(new SimEvent { Kind = EventKind.WaveStart, Value = Wave });
        }

        /// <summary>Вызывается при гибели врага: если он последний в наборе волн — замедление LastKill, как в конце комнаты Hades.</summary>
        void CheckRoomClear(Enemy dead)
        {
            if (pending.Count > 0 || planIndex < plan.Count) return;
            foreach (var e in Enemies) if (!e.Dead && e != dead) return;
            Events.Add(new SimEvent { Kind = EventKind.RoomClear, Pos = dead.Pos, Stop = HitStop.LastKill });
        }

        /// <summary>Генерация набора волн по бюджету комнаты (GeneratedTartarus) из её набора врагов.</summary>
        void GeneratePlan()
        {
            plan.Clear();
            planIndex = 0;
            var enc = Enc;
            var pool = PoolOf(enc);
            float budget = enc != null ? enc.budget + enc.budgetPerDepth * Depth : 30f + 11f * Depth;
            budget = Mathf.Max(10f, budget) + (FormsMode ? Rewards * ArenaCfg.budgetPerReward : 0);
            float[][] splits = { new[] { 1f }, new[] { 0.5f, 0.5f }, new[] { 0.3f, 0.15f, 0.55f } };
            var split = splits[rng.Next(splits.Length)];
            activeCap = Mathf.Min(8, Mathf.FloorToInt(2.3f + 0.35f * Depth));
            foreach (float part in split)
            {
                float slice = budget * part;
                int types = 1 + rng.Next(pool.Count);
                var names = new List<string>();
                var used = new List<int>();
                for (int t = 0; t < types; t++)
                {
                    int k = rng.Next(pool.Count);
                    if (!used.Contains(k)) used.Add(k);
                }
                foreach (int k in used)
                {
                    int count = Mathf.CeilToInt(slice / used.Count / pool[k].rating);
                    for (int i = 0; i < count; i++) names.Add(pool[k].kind.ToString());
                }
                plan.Add(new RoomDef.Wave { enemies = names.ToArray(), startDelay = 1f });
            }
        }

        static List<(EnemyKind kind, float rating)> PoolOf(RoomDef.Encounter enc)
        {
            var list = new List<(EnemyKind, float)>();
            if (enc != null && enc.pool != null)
                for (int i = 0; i < enc.pool.Length; i++)
                    if (TryKind(enc.pool[i], out var k)) list.Add((k, enc.ratings != null && i < enc.ratings.Length ? enc.ratings[i] : 10f));
            if (list.Count == 0) list.AddRange(TartarusPool);
            return list;
        }

        static bool TryKind(string name, out EnemyKind kind)
        {
            switch (name)
            {
                case "Thug": case "HeavyMelee": case "SimpleMelee": kind = EnemyKind.Thug; return true;
                case "Witch": case "LightRanged": kind = EnemyKind.Witch; return true;
                case "Wastrel": case "Brawler": kind = EnemyKind.Wastrel; return true;
                case "Casket": case "Mage": kind = EnemyKind.Casket; return true;
                case "Ghost": kind = EnemyKind.Ghost; return true;
                case "Stomper": kind = EnemyKind.Stomper; return true;
            }
            kind = default;
            return false;
        }

        /// <summary>Точка появления из карты комнаты: сначала с ролью врага (Thug и Wastrel — melee, ведьма и Casket — ranged),
        /// не ближе 3 юнитов к герою (враги Hades II — 5; предрасставленные — не ближе 750 ед. Hades), не занятая.</summary>
        Vector2 PickSpawnPoint(bool preSpawned, EnemyKind kind = EnemyKind.Thug)
        {
            float minDist = preSpawned ? ArenaCfg.preSpawnDistance : IsHades2(kind) ? 5f : 3f;
            string role = kind is EnemyKind.Witch or EnemyKind.Casket ? "ranged" : "melee";
            var spawns = Room.spawns;
            for (int t = 0; t < 60 && spawns.Length > 0; t++)
            {
                var sp = spawns[rng.Next(spawns.Length)];
                if (t < 30 && !string.IsNullOrEmpty(sp.role) && sp.role != role) continue;
                var p = sp.pos;
                if ((p - Player.Pos).magnitude < minDist && t < 50) continue;
                if (Crowded(p) && t < 55) continue;
                if (!InsideRoom(p, 0.3f) && t < 58) continue;
                return p;
            }
            if (spawns.Length > 0)
            {
                // всё рядом с героем — самая дальняя точка
                var best = spawns[0].pos;
                foreach (var sp in spawns) if ((sp.pos - Player.Pos).sqrMagnitude > (best - Player.Pos).sqrMagnitude) best = sp.pos;
                return best;
            }
            return RandomFloorPoint(minDist);
        }

        bool Crowded(Vector2 p)
        {
            foreach (var e in Enemies) if (!e.Dead && (e.Pos - p).sqrMagnitude < 1f) return true;
            return false;
        }

        Vector2 RandomFloorPoint(float minDist)
        {
            var r = Room;
            for (int t = 0; t < 200; t++)
            {
                var p = new Vector2(Rand(r.min.x + 1f, r.max.x - 1f), Rand(r.min.y + 1f, r.max.y - 1f));
                if ((p - Player.Pos).magnitude < minDist || !InsideRoom(p, 0.6f) || Crowded(p)) continue;
                return p;
            }
            return Player.Pos + new Vector2(0f, minDist);
        }
    }
}
