using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Хаб между забегами (HubBuilder), как Breach в Enter the Gungeon: волн нет, мишени не ходят, не бьют и не
    /// умирают, персонажи говорят по реплике, врата начинают забег (этажи из арен). Забег кончается смертью или выходом
    /// с пятого этажа (RunWon) — дальше итоги и снова хаб.</summary>
    public sealed partial class ArenaSim
    {
        public bool HubMode { get; private set; }
        public readonly List<HubSpot> HubSpots = new();
        readonly List<Vector2> hubDummies = new();
        readonly Dictionary<HubSpot, int> hubTalks = new();
        /// <summary>Дальше этого от места (врата, персонаж) взаимодействовать нельзя.</summary>
        public float HubReach = 1.9f;
        /// <summary>Последняя реплика персонажа: кто, что и когда (по Time) — HUD показывает её над ним.</summary>
        public HubSpot SpeechSpot { get; private set; }
        public string SpeechText { get; private set; }
        public float SpeechAt { get; private set; }
        /// <summary>Забег пройден: выход с пятого этажа. Вместе с ним GameOver — бой стоит, дальше итоги.</summary>
        public bool RunWon { get; private set; }
        /// <summary>Во врата шагнули: забег начнёт GameController (перестроив мир). Событие HubGate может не дожить до чтения —
        /// «взаимодействие» нажимается между шагами, а шаг очищает события.</summary>
        public bool RunRequested { get; private set; }
        /// <summary>Боевых комнат пройдено за забег — для итогов.</summary>
        public int RunRoomsCleared { get; private set; }
        /// <summary>Открыт выбор стартового набора (Наставник): бой стоит, ввод — у экрана выбора.</summary>
        public bool KitOpen { get; private set; }

        /// <summary>В хаб: площадь, места, мишени; билд и здоровье — как в начале забега.</summary>
        public void EnterHub()
        {
            HubMode = true;
            FloorRun = false; Floor = null; floorArenas = null; RunWon = false;
            RoomStates = System.Array.Empty<FloorRoomState>(); ActiveRoom = -1; CurrentRoom = -1;
            baseRoom = HubBuilder.Build(out var spots, out var dummies);
            HubSpots.Clear(); HubSpots.AddRange(spots);
            hubDummies.Clear(); hubDummies.AddRange(dummies);
            hubTalks.Clear();
            SpeechSpot = null; SpeechText = null;
            RunRequested = false;
            KitOpen = false;
            WavesEnabled = false;
            Restart();
        }

        /// <summary>Врата: новый забег по этажам из арен, билд с нуля.</summary>
        public void StartRun()
        {
            HubMode = false;
            RunRequested = false;
            KitOpen = false;
            HubSpots.Clear(); hubDummies.Clear();
            SpeechSpot = null; SpeechText = null;
            RunWon = false; RunRoomsCleared = 0;
            WavesEnabled = true;
            FloorRun = true; FloorLap = 0;
            floorArenas = arenaCache ??= LoadArenas();
            Restart(); // билд и здоровье с нуля, первый этаж
        }

        /// <summary>Мишени хаба — Thug, которые стоят на месте и не умирают.</summary>
        void SpawnHubDummies()
        {
            foreach (var p in hubDummies)
            {
                var e = SpawnEnemy(EnemyKind.Thug, p, 0f, true);
                e.Dummy = true;
            }
        }

        /// <summary>Ближайшее место хаба в досягаемости (null — нет, или выбор открыт).</summary>
        public HubSpot HubNear
        {
            get
            {
                if (!HubMode || GameOver || RewardPending || KitOpen) return null;
                HubSpot best = null;
                float bd = HubReach * HubReach;
                foreach (var s in HubSpots)
                {
                    float d = (s.Pos - Player.Pos).sqrMagnitude;
                    if (d <= bd) { bd = d; best = s; }
                }
                return best;
            }
        }

        /// <summary>«Взаимодействие» (E / RB): в хабе — врата или разговор, в забеге — подобрать награду с пятачка.</summary>
        public bool Interact()
        {
            if (!HubMode) return PickUpLoot();
            var s = HubNear;
            if (s == null) return false;
            if (s.Kind == HubSpotKind.Gate)
            {
                RunRequested = true;
                Events.Add(new SimEvent { Kind = EventKind.HubGate, Pos = s.Pos });
                return true;
            }
            if (s.Kit)
            {
                KitOpen = true;
                Events.Add(new SimEvent { Kind = EventKind.HubTalk, Pos = s.Pos });
                return true;
            }
            hubTalks.TryGetValue(s, out int i);
            SpeechSpot = s;
            SpeechText = s.Lines.Length > 0 ? s.Lines[i % s.Lines.Length] : "…";
            SpeechAt = Time;
            hubTalks[s] = i + 1;
            Events.Add(new SimEvent { Kind = EventKind.HubTalk, Pos = s.Pos });
            return true;
        }

        /// <summary>Закрыть выбор стартового набора.</summary>
        public void CloseKit() => KitOpen = false;

        /// <summary>Стартовый набор: карта в ЛКМ — форма (снаряд, зона, призыв) и своё свойство (null — без свойства).
        /// В хабе карта сразу встаёт в ЛКМ — её можно попробовать на мишенях; в забег идёт с ней же.</summary>
        public void SetKit(FormKind main, Trait? soul)
        {
            if (System.Array.IndexOf(StartKit.Mains, main) < 0) return;
            Kit.Main = main;
            Kit.Soul = soul;
            if (!HubMode) return;
            foreach (var f in Forms[0].Active) f.Done = true;
            Forms[0].Card = Kit.Card();
            Forms[0].Reset();
        }

        /// <summary>Выход с пятого этажа забега из арен: забег пройден.</summary>
        void WinRun()
        {
            RunWon = true;
            GameOver = true;
            Events.Add(new SimEvent { Kind = EventKind.RunEnd, Value = 1f });
        }
    }
}
