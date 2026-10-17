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
        /// <summary>Открыто меню персонажа хаба (Наставник, Торговец, Хранитель, Летописец): бой стоит, ввод — у экрана меню.</summary>
        public HubRole Menu { get; private set; }
        public bool MenuOpen => Menu != HubRole.None;

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
            Menu = HubRole.None;
            WavesEnabled = false;
            NewMentorSets(); // после каждого забега — новые наборы
            Restart();
        }

        /// <summary>Врата: новый забег по этажам из арен, билд с нуля.</summary>
        public void StartRun()
        {
            HubMode = false;
            RunRequested = false;
            Menu = HubRole.None;
            runRecorded = false;
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
                if (!HubMode || GameOver || RewardPending || MenuOpen) return null;
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

        /// <summary>«Взаимодействие» (E / RB): в хабе — врата или меню персонажа, в забеге — испить из источника или подобрать
        /// награду с пятачка.</summary>
        public bool Interact()
        {
            if (!HubMode) return DrinkFountain() || PickUpLoot();
            var s = HubNear;
            if (s == null) return false;
            if (s.Kind == HubSpotKind.Gate)
            {
                RunRequested = true;
                Events.Add(new SimEvent { Kind = EventKind.HubGate, Pos = s.Pos });
                return true;
            }
            if (s.Role != HubRole.None)
            {
                Menu = s.Role;
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

        /// <summary>Закрыть меню персонажа хаба.</summary>
        public void CloseMenu() => Menu = HubRole.None;

        /// <summary>Выход с пятого этажа забега из арен: забег пройден.</summary>
        void WinRun()
        {
            RunWon = true;
            GameOver = true;
            RecordRun(true);
            Events.Add(new SimEvent { Kind = EventKind.RunEnd, Value = 1f });
        }
    }
}
