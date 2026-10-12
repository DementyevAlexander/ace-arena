using Arena.Sim;
using Arena.View;
using UnityEditor;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>Arena → Spawn: в режиме игры ставит врага рядом с Ace. Прежние враги (Ghost, Топотун) в волны Hades
    /// не входят — их вызывают отсюда.</summary>
    public static class SpawnMenu
    {
        [MenuItem("Arena/Spawn/Enraged Ghost")] static void Ghost() => Spawn(EnemyKind.Ghost);
        [MenuItem("Arena/Spawn/Топотун")] static void Stomper() => Spawn(EnemyKind.Stomper);
        [MenuItem("Arena/Spawn/Wretched Thug")] static void Thug() => Spawn(EnemyKind.Thug);
        [MenuItem("Arena/Spawn/Wretched Witch")] static void Witch() => Spawn(EnemyKind.Witch);

        [MenuItem("Arena/Spawn/Enraged Ghost", true)]
        [MenuItem("Arena/Spawn/Топотун", true)]
        [MenuItem("Arena/Spawn/Wretched Thug", true)]
        [MenuItem("Arena/Spawn/Wretched Witch", true)]
        static bool CanSpawn() => Application.isPlaying && Object.FindAnyObjectByType<GameController>() != null;

        static void Spawn(EnemyKind kind)
        {
            var gc = Object.FindAnyObjectByType<GameController>();
            if (gc == null || gc.Sim == null) return;
            var sim = gc.Sim;
            for (int i = 0; i < 24; i++)
            {
                float a = i * Mathf.PI * 2f / 24f;
                var p = sim.Player.Pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 4f;
                if (!sim.InsideRoom(p, 0.6f)) continue;
                var e = sim.SpawnEnemy(kind, p);
                e.Aggro = true;
                return;
            }
            sim.SpawnEnemy(kind, sim.Player.Pos + Vector2.up * 2f).Aggro = true;
        }
    }
}
