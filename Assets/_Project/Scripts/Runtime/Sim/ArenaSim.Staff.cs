using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    public enum StaffMove { None, Swing, DashStrike, OmegaAttack, Special, Cast, OmegaCast, Blink, Sprint }

    /// <summary>Состояние посоха: текущий приём, комбо, блокировки, перезарядки, буфер нажатия.</summary>
    public sealed class StaffState
    {
        public StaffMove Move;
        public int Slot = -1;          // кнопка текущего приёма
        public float T;                // время в приёме
        public int Swing;              // удар комбо, который сейчас замахивается (0..2)
        public int NextSwing;          // какой удар комбо будет следующим
        public float ComboWindow;      // сколько ещё ждать следующего удара; истекло — комбо сначала
        public Vector2 Dir;
        public Enemy Target;
        public float Lock, LockCancel; // блок атак: неотменяемый и отменяемый (рывком, спецатакой чужой)
        public float TurnLock;         // DisableRotate после удара или спецатаки
        public bool LockCancelBySpecial; // CanCancelOwnDisables = false: свой блок спецатака не снимает
        public float MoveHold;         // SwingN/Special DisableMoveHold: стоит и не поворачивается, рывок снимает
        public float Root, RootCancel; // стоит на месте: неотменяемо и до рывка
        public float SlowT, SlowMul = 1f;
        public Vector2 Vel;            // отдача спецатаки, затухает
        public float SpecialCd, CastCd, DashCd, OmegaFailCd;
        public float DashStrikeWindow; // > 0 — атака станет ударом из рывка
        public float BlinkElapsed = 99f;
        public int Queued = -1;
        public float QueuedT;
        public bool OmegaArmed;        // после удара: если кнопка ещё зажата, начнётся Ω-атака
        public int OmegaSlot = -1;
        public Vector2 BlinkFrom, BlinkTo;
        public float BlinkFireT = -1f;
        public Effect Circle;
        public readonly float[] HitStopCd = new float[8];
        public float SprintIdle, RushHeld;
        public bool SprintHold;        // авто-спринт стал спринтом по удержанию (AutoSprintBuffer)

        public bool Busy => Move != StaffMove.None;
        public bool CircleAlive => Circle != null && !Circle.Done;

        public void Reset()
        {
            Move = StaffMove.None; Slot = -1; T = 0f; Swing = NextSwing = 0; ComboWindow = 0f; Target = null;
            Lock = LockCancel = TurnLock = MoveHold = Root = RootCancel = SlowT = 0f; SlowMul = 1f; LockCancelBySpecial = false;
            Vel = Vector2.zero; SpecialCd = CastCd = DashCd = OmegaFailCd = DashStrikeWindow = 0f; BlinkElapsed = 99f;
            Queued = -1; QueuedT = 0f; OmegaArmed = false; OmegaSlot = -1; BlinkFireT = -1f; Circle = null;
            System.Array.Clear(HitStopCd, 0, HitStopCd.Length); SprintIdle = RushHeld = 0f; SprintHold = false;
        }
    }

    /// <summary>Посох Мелинои из Hades II (числа — StaffTuning): четыре скилла в слотах вместо карт.</summary>
    public sealed partial class ArenaSim
    {
        public readonly StaffTuning StaffCfg = new();
        StaffState S => Player.Staff;

        public static bool IsStaff(CardId? id) => id is CardId.StaffAttack or CardId.StaffSpecial or CardId.StaffCast or CardId.StaffDash;

        bool IsStaffSlot(int i) => IsStaff(Slots[i].Main);

        // ---------- ввод ----------

        void StaffPress(int i, SimInput input)
        {
            var id = Slots[i].Main.Value;
            if (Player.Cast != null) return; // идёт карта — посох ждёт
            // RemoveControlOnCharge: пока заряжается Ω-атака, спецатака не нажимается вовсе
            if (id == CardId.StaffSpecial && S.Move == StaffMove.OmegaAttack) return;
            // спринт обрывают атака, спецатака и каст (каст — только если круга нет)
            if (S.Move == StaffMove.Sprint && id != CardId.StaffDash && !(id == CardId.StaffCast && S.CircleAlive)) EndSprint();
            if (TryStaff(i, id, input)) return;
            S.Queued = i; S.QueuedT = 0.4f; // RequestTriggerLock: нажатие в блоке исполнится, когда блок спадёт
        }

        bool TryStaff(int i, CardId id, SimInput input)
        {
            var c = StaffCfg;
            switch (id)
            {
                case CardId.StaffDash:
                    if (S.DashCd > 0f || S.Lock > 0f || S.Move is StaffMove.Blink or StaffMove.Sprint) return false;
                    StartBlink(i, input);
                    return true;
                case CardId.StaffAttack:
                    if (S.Move == StaffMove.Blink || S.BlinkElapsed < c.dashStrikeQueue) return false; // DashAttackQueue
                    if (S.Busy || S.Lock > 0f) return false;
                    if (S.DashStrikeWindow > 0f) { StartDashStrike(i, input); return true; } // CanCancelDisables — сквозь отменяемый блок
                    if (S.LockCancel > 0f) return false;
                    StartSwing(i, S.ComboWindow > 0f ? S.NextSwing : 0, input);
                    return true;
                case CardId.StaffSpecial:
                    // CanCancelDisables: снимает чужие отменяемые блоки и прерывает рывок; свои — нет
                    if (S.SpecialCd > 0f || S.Lock > 0f || (S.LockCancel > 0f && S.LockCancelBySpecial)) return false;
                    if (S.Busy && S.Move != StaffMove.Blink) return false;
                    if (S.Move == StaffMove.Blink) S.Move = StaffMove.None;
                    S.LockCancel = 0f; S.RootCancel = 0f; S.MoveHold = 0f;
                    S.Move = StaffMove.Special; S.Slot = i; S.T = 0f; S.OmegaArmed = false;
                    S.Dir = StaffAim(c.specialLockRange, c.specialLockArc, input);
                    return true;
                case CardId.StaffCast:
                    // IgnoreOwnerAttackDisabled: блоки атак касту не мешают; новый круг — только когда прежний исчез
                    if (S.CastCd > 0f || S.CircleAlive || S.Move is not (StaffMove.None or StaffMove.Sprint)) return false;
                    if (S.Move == StaffMove.Sprint) EndSprint();
                    S.Move = StaffMove.Cast; S.Slot = i; S.T = 0f; S.OmegaArmed = false;
                    S.SlowT = c.castSlowTime; S.SlowMul = c.castChargeSlow;
                    return true;
            }
            return false;
        }

        /// <summary>Наведение посоха — как у карт (FindLockTarget по дальности и дуге приёма, мышь — курсор или враг под ним).</summary>
        Vector2 StaffAim(float range, float arc, SimInput input)
        {
            var dir = AimWith(range, arc, input);
            S.Target = Player.Lock;
            return dir;
        }

        /// <summary>Ω-атака: цель автолока берётся один раз в начале заряда (AutoLockUnderCharge), дальше прицел свободный —
        /// за курсором или стиком (UseTargetAngle, CanUseKeyboardAim).</summary>
        Vector2 FreeAim(SimInput input, Vector2 keep)
        {
            var p = Player;
            if (input.UseMouse && input.HasCursor)
            {
                var d = input.Cursor - p.Pos;
                return d.sqrMagnitude > 0.01f ? d.normalized : keep;
            }
            return input.Move.sqrMagnitude > 0.15f ? input.Move.normalized : keep;
        }

        // ---------- атака ----------

        void StartSwing(int i, int index, SimInput input)
        {
            var c = StaffCfg;
            S.Move = StaffMove.Swing; S.Slot = i; S.T = 0f; S.Swing = index; S.OmegaArmed = false;
            S.Dir = StaffAim(c.swingLockRange[index], c.swingLockArc, input); // SetCompleteAngleOnCharge; до удара доворачивает
        }

        void StartDashStrike(int i, SimInput input)
        {
            var c = StaffCfg;
            S.Move = StaffMove.DashStrike; S.Slot = i; S.T = 0f; S.OmegaArmed = false; S.DashStrikeWindow = 0f;
            S.LockCancel = 0f; S.RootCancel = 0f; S.MoveHold = 0f;
            S.Dir = StaffAim(c.dashStrikeLockRange, c.dashStrikeLockArc, input);
        }

        /// <summary>Блок после приёма: атаки и поворота (DisableAttack + DisableRotate), неотменяемый и отменяемый.</summary>
        void SetLock(float hard, float cancel, bool bySpecial = false)
        {
            S.Lock = hard; S.LockCancel = cancel; S.LockCancelBySpecial = bySpecial;
            S.TurnLock = Mathf.Max(hard, cancel);
        }

        void FireSwing(SimInput input)
        {
            var c = StaffCfg;
            int k = S.Swing;
            S.Dir = StaffAim(c.swingLockRange[k], c.swingLockArc, input); // SetCompleteAngleOnFire
            Wedge(S.Slot, c.swingDamage[k], c.swingStun[k], c.wedgeSpeed, c.wedgeRange, c.swingStartDelay[k], 0f, c.swingHalfWidth[k], c.wedgeLength, k);
            if (k == 2) Shake(c.swing3FireShake);
            SetLock(c.swingLock[k], c.swingLockCancel[k]);
            S.MoveHold = c.swingMoveHold[k];
            S.Root = Mathf.Max(c.swingRoot[k], c.swingRootWhileFiring[k] ? c.wedgeRange / c.wedgeSpeed : 0f);
            S.RootCancel = c.swingRootCancel[k];
            if (k == 0) { S.SlowT = c.swing1FireSlow; S.SlowMul = 0.05f; }
            // ControlWindow следующего удара — с момента этого удара; после третьего комбо начинается сначала
            S.NextSwing = (k + 1) % 3;
            S.ComboWindow = k < 2 ? c.swingWindow[k] : 0f;
            S.OmegaArmed = true; S.OmegaSlot = S.Slot; // AddOnFire WeaponStaffSwing5
            S.Move = StaffMove.None;
        }

        void FireDashStrike(SimInput input)
        {
            var c = StaffCfg;
            S.Dir = StaffAim(c.dashStrikeLockRange, c.dashStrikeLockArc, input); // SetCompleteAngleOnFire
            Wedge(S.Slot, c.dashStrikeDamage, c.dashStrikeStun, c.dashStrikeSpeed, c.dashStrikeRange, 0f, 0f, c.swingHalfWidth[0], c.wedgeLength, 3);
            SetLock(c.dashStrikeLock, c.dashStrikeLockCancel);
            S.Root = Mathf.Max(c.dashStrikeLock, c.dashStrikeRange / c.dashStrikeSpeed); // RootOwnerWhileFiring
            S.RootCancel = c.dashStrikeLockCancel;
            S.NextSwing = 0; S.ComboWindow = 0f; // SwapOnFire WeaponStaffSwing — дальше снова первый удар
            S.OmegaArmed = true; S.OmegaSlot = S.Slot;
            S.Move = StaffMove.None;
        }

        void FireOmegaAttack()
        {
            var c = StaffCfg;
            // NumProjectiles 2, ProjectileAngleOffset 180: волна вперёд и назад; ExcludeSameWaveHits — враг получает одну
            var shared = new HashSet<Enemy>();
            for (int k = 0; k < 2; k++)
            {
                var d = k == 0 ? S.Dir : -S.Dir;
                var w = Wedge(S.Slot, c.omegaAttackDamage, c.omegaAttackStun, c.omegaAttackSpeed, c.omegaAttackRange, 0.01f * k,
                    c.omegaWaveBackHalf, c.omegaWaveFrontHalf, c.omegaWaveLength, 4, d, c.omegaWaveBarrel);
                w.SharedHit = shared;
            }
            Shake(c.omegaFireShake);
            SetLock(c.omegaAttackLock, c.omegaAttackLockCancel);
            S.Root = c.omegaAttackLock; S.RootCancel = c.omegaAttackLockCancel;
            S.NextSwing = 0; S.ComboWindow = 0f;
            S.Move = StaffMove.None;
        }

        /// <summary>Хитбокс посоха: трапеция, летит по прямой сквозь врагов и препятствия. Хвост стартует на barrel от героя
        /// (у ударов комбо BarrelLength −150 — за спиной), голова — на length дальше и проходит range; у хвоста полуширина
        /// back, у головы front.</summary>
        Effect Wedge(int slot, float damage, float stun, float speed, float range, float delay, float back, float front, float length, int phase,
            Vector2? dir = null, float? barrel = null)
        {
            var d = dir ?? S.Dir;
            float b = barrel ?? StaffCfg.wedgeBarrel;
            return Add(new Effect
            {
                Kind = EffectKind.StaffWedge, Pos = Player.Pos + d * (b + length), Dir = d, Speed = speed, Life = range / speed, T = delay,
                R = front, Back = back, Length = length, Damage = damage, Stun = stun, Phase = phase, Src = Source(Slots[slot], damage),
            });
        }

        /// <summary>Тряска экрана {Distance, Duration, ScreenPreWait}.</summary>
        void Shake(float[] s) =>
            Events.Add(new SimEvent { Kind = EventKind.Shake, Value = s[0], Radius = s[1], To = new Vector2(s[2], 0f) });

        // ---------- рывок и спринт ----------

        void StartBlink(int i, SimInput input)
        {
            var c = StaffCfg;
            // BlockDashDisable: рывок из замаха Swing2/Swing3 не запирает атаку на DashAttackQueue
            bool blockQueue = S.Move == StaffMove.Swing && S.Swing >= 1;
            if (S.Move == StaffMove.OmegaAttack) S.OmegaFailCd = c.omegaAttackFailCooldown; // сорванный Ω — как неудачный выстрел
            // CanCancelDisables + ForceReleaseAllOnFire: рывок срывает заряды и отменяемые блоки, снимает оглушение героя
            S.Move = StaffMove.Blink; S.Slot = i; S.T = 0f; S.BlinkFireT = -1f; S.BlinkElapsed = blockQueue ? c.dashStrikeQueue : 0f;
            S.LockCancel = 0f; S.RootCancel = 0f; S.MoveHold = 0f; S.TurnLock = Mathf.Min(S.TurnLock, S.Lock); S.OmegaArmed = false; S.Queued = -1;
            Player.HitStun = 0f;
            S.SlowT = c.blinkCharge; S.SlowMul = 1.2f;
            S.Dir = input.Move.sqrMagnitude > 0.0001f ? input.Move.normalized : Player.Face;
            Player.DashInvuln = Mathf.Max(Player.DashInvuln, c.blinkInvulnCharge);
        }

        void EndSprint()
        {
            S.Move = StaffMove.None;
            S.DashStrikeWindow = StaffCfg.dashStrikeWindow;
        }

        // ---------- шаг ----------

        void UpdateStaffTimers(float dt, SimInput input)
        {
            S.Lock -= dt; S.LockCancel -= dt; S.TurnLock -= dt; S.Root -= dt; S.RootCancel -= dt; S.SlowT -= dt; S.MoveHold -= dt;
            S.SpecialCd -= dt; S.CastCd -= dt; S.DashCd -= dt; S.OmegaFailCd -= dt; S.ComboWindow -= dt; S.QueuedT -= dt;
            for (int k = 0; k < S.HitStopCd.Length; k++) S.HitStopCd[k] -= dt;
            S.BlinkElapsed += dt;
            if (S.Move is not (StaffMove.Blink or StaffMove.Sprint)) S.DashStrikeWindow -= dt;
            if (S.OmegaArmed && !input.IsHeld(S.OmegaSlot)) S.OmegaArmed = false;
            S.Vel *= Mathf.Exp(-dt / 0.1f);
            // кулдауны посоха — в слоты, чтобы их показывал интерфейс; каст ждёт и исчезновения круга
            for (int i = 0; i < 4; i++)
            {
                var s = Slots[i];
                if (!IsStaffSlot(i)) continue;
                float cd = s.Main switch
                {
                    CardId.StaffSpecial => S.SpecialCd,
                    CardId.StaffCast => Mathf.Max(S.CastCd, S.CircleAlive ? S.Circle.Life : 0f),
                    CardId.StaffDash => S.DashCd,
                    _ => 0f,
                };
                s.CdTimer = Mathf.Max(0f, cd); s.OnCd = cd > 0f; s.Charges = cd > 0f ? 0 : 1;
            }
        }

        void UpdateStaff(float dt, SimInput input)
        {
            var c = StaffCfg;
            var p = Player;
            // буфер: нажатие, сделанное в блоке, исполняется, как только можно
            if (S.Queued >= 0)
            {
                if (S.QueuedT <= 0f || !IsStaffSlot(S.Queued)) S.Queued = -1;
                else if (TryStaff(S.Queued, Slots[S.Queued].Main.Value, input)) S.Queued = -1;
            }
            // Ω-атака: кнопка атаки зажата и после удара блок спал
            if (S.OmegaArmed && !S.Busy && S.Lock <= 0f && S.LockCancel <= 0f && S.OmegaFailCd <= 0f && input.IsHeld(S.OmegaSlot))
            {
                S.OmegaArmed = false;
                S.Move = StaffMove.OmegaAttack; S.Slot = S.OmegaSlot; S.T = 0f;
                S.Dir = StaffAim(c.omegaAttackLockRange, c.omegaAttackLockArc, input);
                p.Face = S.Dir; // корпус замирает на весь заряд (RotationMultiplier ≈ 0), прицел — свободный
                Shake(c.omegaChargeShake);
            }

            S.T += dt;
            switch (S.Move)
            {
                case StaffMove.Swing:
                    // во время замаха прицел и разворот следуют за мышью/стиком, направление фиксируется в момент удара
                    S.Dir = StaffAim(c.swingLockRange[S.Swing], c.swingLockArc, input);
                    p.Face = S.Dir;
                    if (S.T >= c.swingCharge[S.Swing]) FireSwing(input);
                    break;
                case StaffMove.DashStrike:
                    S.Dir = StaffAim(c.dashStrikeLockRange, c.dashStrikeLockArc, input);
                    p.Face = S.Dir;
                    if (S.T >= c.dashStrikeCharge) FireDashStrike(input);
                    break;
                case StaffMove.OmegaAttack:
                    S.Dir = FreeAim(input, S.Dir);
                    if (!input.IsHeld(S.Slot)) { S.Move = StaffMove.None; S.OmegaFailCd = c.omegaAttackFailCooldown; } // отпустили раньше — отмена
                    else if (S.T >= c.omegaAttackCharge) FireOmegaAttack();
                    break;
                case StaffMove.Special:
                {
                    S.Dir = StaffAim(c.specialLockRange, c.specialLockArc, input); // доворачивает до отпускания
                    p.Face = S.Dir;
                    if (input.IsHeld(S.Slot) || S.T < c.specialMinCharge) break;
                    bool omega = S.T >= c.specialOmegaWait;
                    Add(new Effect
                    {
                        Kind = EffectKind.StaffBall, Pos = p.Pos + S.Dir * 0.3f, Dir = S.Dir, Phase = omega ? 1 : 0, Target = S.Target,
                        Speed = omega ? c.specialOmegaSpeed : c.specialSpeed, Life = c.specialRange, T = omega ? c.specialOmegaStartDelay : c.specialStartDelay,
                        R = omega ? c.specialOmegaRadius : c.specialRadius, Damage = omega ? c.specialOmegaDamage : c.specialDamage,
                        Stun = omega ? c.specialOmegaStun : c.specialStun, Src = Source(Slots[S.Slot], 0f),
                    });
                    if (!omega) S.Vel = -S.Dir * c.specialRecoil;
                    Shake(omega ? c.omegaBallFireShake : c.ballFireShake);
                    S.SpecialCd = omega ? c.specialOmegaCooldown : c.specialCooldown;
                    SetLock(c.specialLock, c.specialLockCancel, true);
                    S.Root = c.specialLock; S.RootCancel = c.specialLockCancel;
                    // Special1DisableMoveHold с момента Ω-стадии: стоит и не поворачивается до 0.65 + 0.71 с
                    if (omega) S.MoveHold = Mathf.Max(S.MoveHold, c.specialOmegaWait + c.specialOmegaHold - S.T);
                    S.Move = StaffMove.None;
                    break;
                }
                case StaffMove.Cast:
                    if (S.T < c.castCharge) break;
                    S.Circle = Add(new Effect { Kind = EffectKind.CastCircle, Pos = p.Pos, Life = c.castLife, R = 0f, Src = Source(Slots[S.Slot], 0f) });
                    S.CastCd = c.castCooldown;
                    S.LockCancel = Mathf.Max(S.LockCancel, c.castAttackLock); S.LockCancelBySpecial = false;
                    if (input.IsHeld(S.Slot)) { S.Move = StaffMove.OmegaCast; S.T = 0f; S.SlowT = c.castArmCharge; S.SlowMul = c.castChargeSlow; }
                    else S.Move = StaffMove.None;
                    break;
                case StaffMove.OmegaCast:
                    if (!input.IsHeld(S.Slot)) { S.Move = StaffMove.None; S.SlowT = 0f; break; } // отпустили раньше — отмена
                    if (S.T < c.castArmCharge) break;
                    if (S.CircleAlive) S.Circle.Phase = 1; // ArmProjectiles: круг взорвётся, когда истечёт
                    SetLock(c.castArmLock, c.castArmLockCancel);
                    S.Root = c.castArmLock;
                    S.Move = StaffMove.None;
                    break;
                case StaffMove.Blink:
                    if (S.BlinkFireT < 0f)
                    {
                        if (S.T < c.blinkCharge) break;
                        // выстрел рывка: 0.2 с полёта, неуязвимость ещё 16 кадров; удары из прошлого рывка гаснут (ExpireDashWeaponOnDash)
                        S.BlinkFireT = 0f;
                        S.BlinkFrom = p.Pos; S.BlinkTo = TravelTarget(p.Pos, S.Dir, c.blinkRange);
                        S.DashCd = c.blinkDuration + c.blinkRegen; // перезарядка идёт с конца рывка
                        p.DashInvuln = Mathf.Max(p.DashInvuln, c.blinkInvulnFire);
                        p.Face = S.Dir;
                        foreach (var f in Effects) if (f.Kind == EffectKind.StaffWedge && f.Phase == 3) f.Done = true;
                        Events.Add(new SimEvent { Kind = EventKind.CardCast, Pos = p.Pos, To = S.Dir, Value = S.Slot, Color = 2 });
                        break;
                    }
                    S.BlinkFireT += dt;
                    float k = Mathf.Clamp01(S.BlinkFireT / c.blinkDuration);
                    p.Pos = Vector2.Lerp(S.BlinkFrom, S.BlinkTo, k); // BlinkEaseIn/Out 0 — равномерно
                    if (k >= 1f)
                    {
                        // Rush ещё держится — спринт (SwapOnFire WeaponSprint, ChargeTime 0.1 прошёл за время рывка).
                        // С мышью Hades включает Auto-Sprint (SprintAutoHold): спринт без удержания, пока есть направление.
                        bool wantSprint = input.UseMouse ? input.Move.sqrMagnitude > 0.0001f : input.IsHeld(S.Slot);
                        if (wantSprint && S.BlinkFireT >= c.sprintDelay)
                        {
                            S.Move = StaffMove.Sprint; S.T = 0f; S.SprintIdle = 0f; S.RushHeld = 0f; S.SprintHold = !input.UseMouse;
                            p.DashInvuln = Mathf.Max(p.DashInvuln, 0.05f);
                        }
                        else { S.Move = StaffMove.None; S.DashStrikeWindow = c.dashStrikeWindow; }
                    }
                    break;
                case StaffMove.Sprint:
                {
                    // Auto-Sprint: стоп, когда направления нет дольше 0.05 с; Rush держали 0.42 с — дальше спринт по удержанию
                    S.SprintIdle = input.Move.sqrMagnitude < 0.0001f ? S.SprintIdle + dt : 0f;
                    S.RushHeld = input.IsHeld(S.Slot) ? S.RushHeld + dt : 0f;
                    if (!S.SprintHold && S.RushHeld >= c.autoSprintHold) S.SprintHold = true;
                    bool keep = S.SprintHold ? input.IsHeld(S.Slot) && input.Move.sqrMagnitude > 0.0001f : S.SprintIdle <= 0.05f;
                    if (!keep) { EndSprint(); break; }
                    if (input.Move.sqrMagnitude < 0.0001f) break;
                    var dir = input.Move.normalized;
                    p.Pos += dir * (c.sprintSpeed * dt);
                    p.Face = dir; p.Moving = true;
                    break;
                }
            }
        }

        /// <summary>Посох держит Смерть на месте: замах, Ω-заряд, заряд спецатаки, рывок, блоки после ударов, DisableMoveHold.</summary>
        bool StaffBlocksMove(SimInput input)
        {
            // BlockMoveInput: замах ударов, Ω-заряд и заряд спецатаки — стоя; рывок и спринт двигают сами
            if (S.Move is StaffMove.Swing or StaffMove.DashStrike or StaffMove.OmegaAttack or StaffMove.Special or StaffMove.Sprint) return true;
            if (S.Move == StaffMove.Blink && S.BlinkFireT >= 0f) return true;
            return S.Root > 0f || S.RootCancel > 0f || S.MoveHold > 0f;
        }

        /// <summary>Посох не даёт повернуться: Ω-заряд и DisableRotate после ударов и спецатаки (у каста — только блок атаки).</summary>
        bool StaffBlocksTurn => S.Move == StaffMove.OmegaAttack || S.TurnLock > 0f || S.MoveHold > 0f;

        float StaffSpeedMul => S.SlowT > 0f ? S.SlowMul : 1f;

        bool StaffPhasing => S.Move == StaffMove.Blink;

        // ---------- эффекты посоха ----------

        void UpdateStaffWedge(Effect f, float dt)
        {
            if (f.T > 0f) { f.T -= dt; return; }
            f.Life -= dt;
            f.Pos += f.Dir * (f.Speed * dt);
            var hit = f.SharedHit ?? f.Hit;
            var side = new Vector2(-f.Dir.y, f.Dir.x);
            foreach (var e in Enemies)
            {
                if (!e.Alive || hit.Contains(e)) continue;
                var d = e.Pos - f.Pos;
                float along = Vector2.Dot(d, f.Dir), lat = Mathf.Abs(Vector2.Dot(d, side));
                if (along > e.R || along < -f.Length - e.R) continue;
                float half = Mathf.Lerp(f.Back, f.R, Mathf.Clamp01((along + f.Length) / Mathf.Max(f.Length, 0.001f)));
                if (lat > half + e.R) continue;
                hit.Add(e);
                StaffHit(e, f, f.Phase);
            }
            // удары комбо и из рывка гасят вражеские снаряды рядом (ProjectileDefenseRadius 150, ProjectileDefenseAngle 110)
            if (f.Phase <= 3)
                foreach (var b in Effects)
                {
                    if (b.Kind != EffectKind.EnemyBolt || b.Done) continue;
                    var d = b.Pos - f.Pos;
                    if (d.magnitude <= StaffCfg.defenseRadius && Vector2.Angle(f.Dir, d) <= StaffCfg.defenseHalfAngle) b.Done = true;
                }
            if (f.Life <= 0f) f.Done = true;
        }

        void UpdateStaffBall(Effect f, float dt)
        {
            if (f.T > 0f) { f.T -= dt; f.Pos = Player.Pos + f.Dir * 0.3f; return; }
            // HOMING (HomingAllegiance ENEMIES): доворачивает на цель не быстрее MaxAdjustRate; цель погибла — ближайший враг впереди
            if (f.Target == null || !f.Target.Alive) f.Target = NearestAhead(f.Pos, f.Dir, f.Life);
            if (f.Target != null)
            {
                float a = Vector2.SignedAngle(f.Dir, f.Target.Pos - f.Pos);
                float turn = Mathf.Clamp(a, -StaffCfg.specialHoming * dt, StaffCfg.specialHoming * dt) * Mathf.Deg2Rad;
                f.Dir = new Vector2(f.Dir.x * Mathf.Cos(turn) - f.Dir.y * Mathf.Sin(turn), f.Dir.x * Mathf.Sin(turn) + f.Dir.y * Mathf.Cos(turn)).normalized;
            }
            float step = f.Speed * dt;
            f.Pos += f.Dir * step;
            f.Life -= step; // оставшаяся дальность
            bool omega = f.Phase == 1;
            bool end = f.Life <= 0f || !OnFloor(f.Pos) || BlocksShot(f.Pos, 0.1f);
            foreach (var e in Enemies)
            {
                if (!e.Alive || f.Hit.Contains(e) || (e.Pos - f.Pos).magnitude > f.R + e.R) continue;
                if (omega) { end = true; break; } // Ω: взрыв о первого врага
                f.Hit.Add(e);
                StaffHit(e, f, 6);                 // обычный снаряд пробивает врагов
            }
            if (!end) return;
            f.Done = true;
            if (!omega) return;
            float r = StaffCfg.specialOmegaBlast;
            foreach (var e in Enemies) if (e.Alive && (e.Pos - f.Pos).magnitude <= r + e.R) StaffHit(e, f, 5);
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = f.Pos, Radius = r, Color = 2 });
        }

        Enemy NearestAhead(Vector2 from, Vector2 dir, float range)
        {
            Enemy best = null;
            float bd = float.MaxValue;
            foreach (var e in Enemies)
            {
                if (!e.Alive) continue;
                var d = e.Pos - from;
                float m = d.magnitude;
                if (m > range || Vector2.Dot(d, dir) <= 0f) continue;
                if (m < bd) { bd = m; best = e; }
            }
            return best;
        }

        void UpdateCastCircle(Effect f, float dt)
        {
            var c = StaffCfg;
            f.R = c.castRadius * Mathf.Clamp01(f.Age / c.castGrow); // вид: круг растёт ~0.2 с
            f.Tick -= dt;
            if (f.Tick <= 0f)
            {
                // StartCastSlow: враги в круге (полного радиуса с первого тика) «прибиты» ImpactSlow × 0.05 + ImpactGrip на 0.2 с
                f.Tick = c.castRootTick;
                foreach (var e in Enemies) if (e.Alive && (e.Pos - f.Pos).magnitude <= c.castRadius + e.R) e.Rooted = c.castRootTime;
            }
            f.Life -= dt;
            if (f.Life > 0f) return;
            f.Done = true;
            if (f.Phase != 1) return;
            // Ω-каст: ArmedExpirationDamage по всем в круге
            f.Damage = c.castArmDamage; f.Stun = c.castArmStun;
            foreach (var e in Enemies) if (e.Alive && (e.Pos - f.Pos).magnitude <= c.castRadius + e.R) StaffHit(e, f, 7);
            Events.Add(new SimEvent { Kind = EventKind.Explosion, Pos = f.Pos, Radius = c.castRadius, Color = 2 });
        }

        void StaffHit(Enemy e, Effect f, int profile)
        {
            var src = f.Src;
            src.Base = f.Damage;
            HitEnemy(e, src);
            if (e.Alive && f.Stun > 0f) ApplyCc(e, CcKind.Stun, f.Stun); // OnHitStun: враг не двигается, не поворачивается, не атакует
            // HitSimSlowParameters, HitScreenshake и HitSimSlowCooldown удара (WeaponData_Staff.lua, ProjectileData_Melinoe.lua)
            var c = StaffCfg;
            if (profile < 0 || S.HitStopCd[profile] > 0f || (e.Pos - Player.Pos).magnitude > c.hitSlowRange[profile]) return;
            S.HitStopCd[profile] = c.hitSlowCooldown[profile];
            Events.Add(new SimEvent { Kind = EventKind.HitStop, Pos = e.Pos, Value = profile, Crit = e.Dead, Radius = c.hitShake[profile], To = new Vector2(c.hitShakeTime[profile], 0f) });
        }
    }
}
