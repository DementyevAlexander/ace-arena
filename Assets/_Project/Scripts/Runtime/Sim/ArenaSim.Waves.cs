using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Волны по-Hades. Сначала заданные волны энкаунтера комнаты (для первой — EnemyIntroFight01 из EncounterData.lua:3058),
    /// потом генерация как GeneratedTartarus: бюджет сложности max(10, 30 + 11 × глубина), 1–3 волны (100% / 50+50 / 30+15+55),
    /// число врагов типа — доля бюджета / рейтинг (вверх), одновременно не больше ⌊2.3 + 0.35 × глубина⌋ (≤ 8),
    /// враги появляются раз в 0.175–0.225 с, следующая волна — через 1 с после гибели предыдущей.</summary>
    public sealed partial class ArenaSim
    {
        struct PendingSpawn
        {
            public EnemyKind Kind;
            public bool PreSpawned;
        }

        static readonly (EnemyKind kind, float rating)[] GeneratedPool = { (EnemyKind.Thug, 10f), (EnemyKind.Witch, 5f) };

        readonly Queue<PendingSpawn> pending = new();
        readonly List<RoomDef.Wave> plan = new();
        int planIndex;
        float waveTimer, spawnTimer, intervalMin = 0.175f, intervalMax = 0.225f;
        int activeCap = 10;
        public int Depth { get; private set; }

        void ResetEncounter()
        {
            pending.Clear();
            plan.Clear();
            planIndex = 0;
            Depth = 1;
            var enc = Room.encounter;
            if (enc != null && enc.waves != null) plan.AddRange(enc.waves);
            activeCap = enc != null && enc.activeCap > 0 ? enc.activeCap : 10;
            waveTimer = plan.Count > 0 && plan[0].preSpawned ? 0f : 1.2f;
            spawnTimer = 0f;
            if (WavesEnabled && plan.Count > 0 && plan[0].preSpawned) StartNextWave();
        }

        void UpdateEncounter(float dt)
        {
            if (pending.Count > 0)
            {
                spawnTimer -= dt;
                if (spawnTimer > 0f || ActiveWeight() >= activeCap) return;
                var s = pending.Dequeue();
                SpawnEnemy(s.Kind, PickSpawnPoint(s.PreSpawned, s.Kind), 0f, s.PreSpawned);
                spawnTimer = Rand(intervalMin, intervalMax);
                return;
            }
            foreach (var e in Enemies) if (!e.Dead) return;
            // набор волн пройден — комната зачищена, открываются двери (дальше — шаг в дверь)
            if (Wave > 0 && plan.Count > 0 && planIndex >= plan.Count)
            {
                if (!DoorsOpen) { DoorsOpen = true; Events.Add(new SimEvent { Kind = EventKind.DoorsOpen }); }
                return;
            }
            waveTimer -= dt;
            if (waveTimer > 0f) return;
            StartNextWave();
        }

        /// <summary>Новая комната за дверью: волны по бюджету GeneratedTartarus на заданной глубине.</summary>
        void StartGeneratedEncounter(int depth)
        {
            pending.Clear();
            plan.Clear();
            planIndex = 0;
            Depth = depth;
            waveTimer = 1.2f;
            spawnTimer = 0f;
        }

        float ActiveWeight()
        {
            int n = 0;
            foreach (var e in Enemies) if (!e.Dead) n++;
            return n;
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
                while (pending.Count > 0) { var s = pending.Dequeue(); SpawnEnemy(s.Kind, PickSpawnPoint(true, s.Kind), 0f, true); }
            bool last = planIndex >= plan.Count;
            waveTimer = ArenaCfg.waveDelay;
            if (last) waveTimer = ArenaCfg.waveDelay + 1.2f; // после последней волны набора — пауза «комната пройдена»
            Events.Add(new SimEvent { Kind = EventKind.WaveStart, Value = Wave });
        }

        /// <summary>Вызывается при гибели врага: если он последний в наборе волн — замедление LastKill, как в конце комнаты Hades.</summary>
        void CheckRoomClear(Enemy dead)
        {
            if (pending.Count > 0 || planIndex < plan.Count) return;
            foreach (var e in Enemies) if (!e.Dead && e != dead) return;
            Events.Add(new SimEvent { Kind = EventKind.RoomClear, Pos = dead.Pos, Stop = HitStop.LastKill });
        }

        /// <summary>Генерация набора волн как GeneratedTartarus (EncounterData.lua): бюджет по глубине, деление на волны.</summary>
        void GeneratePlan()
        {
            plan.Clear();
            planIndex = 0;
            float budget = Mathf.Max(10f, 30f + 11f * Depth);
            float[][] splits = { new[] { 1f }, new[] { 0.5f, 0.5f }, new[] { 0.3f, 0.15f, 0.55f } };
            var split = splits[rng.Next(splits.Length)];
            activeCap = Mathf.Min(8, Mathf.FloorToInt(2.3f + 0.35f * Depth));
            foreach (float part in split)
            {
                float slice = budget * part;
                int types = 1 + rng.Next(GeneratedPool.Length);
                var names = new List<string>();
                var used = new List<int>();
                for (int t = 0; t < types; t++)
                {
                    int k = rng.Next(GeneratedPool.Length);
                    if (used.Contains(k)) continue;
                    used.Add(k);
                }
                foreach (int k in used)
                {
                    int count = Mathf.CeilToInt(slice / used.Count / GeneratedPool[k].rating);
                    for (int i = 0; i < count; i++) names.Add(GeneratedPool[k].kind.ToString());
                }
                plan.Add(new RoomDef.Wave { enemies = names.ToArray(), startDelay = 1f });
            }
        }

        static bool TryKind(string name, out EnemyKind kind)
        {
            switch (name)
            {
                case "Thug": case "HeavyMelee": case "SimpleMelee": kind = EnemyKind.Thug; return true;
                case "Witch": case "LightRanged": kind = EnemyKind.Witch; return true;
                case "Ghost": kind = EnemyKind.Ghost; return true;
                case "Stomper": kind = EnemyKind.Stomper; return true;
            }
            kind = default;
            return false;
        }

        /// <summary>Точка появления из карты комнаты: сначала с ролью врага (Thug — melee, ведьма — ranged),
        /// не ближе 3 юнитов к Ace (предрасставленные — не ближе 750 ед. Hades), не занятая.</summary>
        Vector2 PickSpawnPoint(bool preSpawned, EnemyKind kind = EnemyKind.Thug)
        {
            float minDist = preSpawned ? ArenaCfg.preSpawnDistance : 3f;
            string role = kind == EnemyKind.Witch ? "ranged" : "melee";
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
