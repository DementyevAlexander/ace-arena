using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Зажатие по Hades II (WeaponStaffBall): прицельная карта заряжается, пока кнопка зажата, прицел следует за
    /// курсором, удар — по отпусканию в направлении на момент отпускания. Синие карты — по нажатию, как рывок.</summary>
    public class ControlTests
    {
        static int Casts(ArenaSim s, SimInput input, float seconds, ref Vector2 dir)
        {
            int n = 0, steps = Mathf.RoundToInt(seconds / T.Dt);
            for (int i = 0; i < steps; i++)
            {
                s.Step(T.Dt, input);
                input.Pressed = 0; input.Released = 0;
                foreach (var ev in s.Events)
                    if (ev.Kind == EventKind.CardCast) { n++; dir = ev.To; }
            }
            return n;
        }

        static SimInput Mouse(Vector2 cursor, int held) => new() { UseMouse = true, HasCursor = true, Cursor = cursor, Held = held };

        [Test]
        public void AimedCardFiresOnReleaseTowardCursorAtRelease()
        {
            var s = T.NewSim();
            var dir = Vector2.zero;
            var hold = Mouse(new Vector2(5f, 0f), 1);
            hold.Pressed = 1;
            Assert.AreEqual(0, Casts(s, hold, 1f, ref dir), "пока кнопка зажата — заряд, удара нет");
            Assert.AreEqual(CastPhase.Charge, s.Player.Cast.Phase);
            var release = Mouse(new Vector2(0f, 5f), 0);
            release.Released = 1;
            Assert.AreEqual(1, Casts(s, release, 0.3f, ref dir));
            Assert.Greater(dir.y, 0.99f, "направление — куда смотрел курсор в момент отпускания");
        }

        [Test]
        public void TapFiresAfterMinimumCharge()
        {
            var s = T.NewSim();
            var dir = Vector2.zero;
            var tap = Mouse(new Vector2(0f, 5f), 0);
            tap.Pressed = 1; tap.Released = 1;
            Assert.AreEqual(0, Casts(s, tap, 0.08f, ref dir), "минимальный заряд 0.1 с (MinChargeToFire)");
            Assert.AreEqual(1, Casts(s, Mouse(new Vector2(0f, 5f), 0), 0.2f, ref dir));
        }

        [Test]
        public void HoldingDoesNotRepeatAimedCard()
        {
            var s = T.NewSim();
            var dir = Vector2.zero;
            var hold = Mouse(new Vector2(0f, 5f), 1);
            hold.Pressed = 1;
            Assert.AreEqual(0, Casts(s, hold, 3f, ref dir));
            var release = Mouse(new Vector2(0f, 5f), 0);
            release.Released = 1;
            Assert.AreEqual(1, Casts(s, release, 1f, ref dir));
        }

        [Test]
        public void CannotMoveWhileCharging()
        {
            var s = T.NewSim();
            var dir = Vector2.zero;
            var hold = Mouse(new Vector2(0f, 5f), 1);
            hold.Pressed = 1; hold.Move = Vector2.right;
            Casts(s, hold, 0.5f, ref dir);
            Assert.AreEqual(0f, s.Player.Pos.x, 1e-4f, "BlockMoveInput: во время заряда Смерть стоит, стик только целится");
        }

        [Test]
        public void BlueCardFiresOnPressEvenWhileHeld()
        {
            var s = T.NewSim();
            var dir = Vector2.zero;
            var hold = new SimInput { Pressed = 1 << 3, Held = 1 << 3, Move = Vector2.up };
            Assert.AreEqual(1, Casts(s, hold, 0.1f, ref dir), "Jump Scare — синяя карта, срабатывает по нажатию, как рывок");
        }
    }
}
