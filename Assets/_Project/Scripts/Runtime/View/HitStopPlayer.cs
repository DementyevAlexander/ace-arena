using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Стоп-кадр Hades (CombatPresentation.lua:2072): подождать, замедлить игру до доли, подержать, вернуть.
    /// Идёт в реальном времени; одновременно — только одно замедление (кто первый, тот и действует).
    /// Крит или добивание держат паузу на 0.04 с дольше.</summary>
    public sealed class HitStopPlayer
    {
        struct Pattern
        {
            public float Wait, Fraction, LerpIn, Hold, LerpOut;
        }

        Pattern cur;
        float t = -1f;

        public float Scale { get; private set; } = 1f;
        public bool Active => t >= 0f;

        static Pattern For(HitStop s) => s switch
        {
            HitStop.Quick => new Pattern { Wait = 0.04f, Fraction = 0.1f, Hold = 0.01f, LerpOut = 0.03f },       // SwordWeapon
            HitStop.Long => new Pattern { Wait = 0.02f, Fraction = 0.3f, Hold = 0.02f, LerpOut = 0.07f },        // RangedWeapon (Cast)
            HitStop.Instant => new Pattern { Wait = 0.04f, Fraction = 0.1f, Hold = 0.03f, LerpOut = 0.07f },     // DashWeapon
            HitStop.HurtHeavy => new Pattern { Wait = 0.02f, Fraction = 0.01f, Hold = 0.16f },                   // удар Thug по герою
            HitStop.HurtLight => new Pattern { Wait = 0.02f, Fraction = 0.25f, Hold = 0.16f, LerpOut = 0.1f },  // сгусток ведьмы
            HitStop.HurtSmall => new Pattern { Wait = 0.04f, Fraction = 0.01f, Hold = 0.06f, LerpOut = 0.1f },  // SmallEnemyMelee
            HitStop.LastKill => new Pattern { Wait = 0.15f, Fraction = 0.35f, LerpIn = 0.08f, Hold = 0.6f, LerpOut = 0.4f },
            // Hades II (ProjectileData_EnemyF.lua): удары Wastrel и снаряд Casket
            HitStop.WastrelHit1 => new Pattern { Wait = 0.02f, Fraction = 0.1f, Hold = 0.1f, LerpOut = 0.07f },
            HitStop.WastrelHit2 => new Pattern { Wait = 0.02f, Fraction = 0.1f, Hold = 0.22f, LerpOut = 0.07f },
            HitStop.CasketBolt => new Pattern { Fraction = 0.25f, Hold = 0.11f, LerpOut = 0.1f },
            _ => default,
        };

        public void Play(HitStop s, bool longer = false)
        {
            if (s == HitStop.None) return;
            // последнее убийство важнее любого удара
            if (Active && s != HitStop.LastKill) return;
            cur = For(s);
            if (longer && s != HitStop.LastKill) cur.Hold += 0.04f;
            t = 0f;
        }

        public void Tick(float realDt)
        {
            if (t < 0f) { Scale = 1f; return; }
            t += realDt;
            var p = cur;
            float a = p.Wait, b = a + p.LerpIn, c = b + p.Hold, d = c + p.LerpOut;
            if (t < a) Scale = 1f;
            else if (t < b) Scale = Mathf.Lerp(1f, p.Fraction, (t - a) / Mathf.Max(0.0001f, p.LerpIn));
            else if (t < c) Scale = p.Fraction;
            else if (t < d) Scale = Mathf.Lerp(p.Fraction, 1f, (t - c) / Mathf.Max(0.0001f, p.LerpOut));
            else { Scale = 1f; t = -1f; }
        }

        public void Reset() { t = -1f; Scale = 1f; }
    }
}
