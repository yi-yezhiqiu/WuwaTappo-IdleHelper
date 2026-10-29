using System;

namespace TappoIdle
{
    public enum ChestSlotState { Unknown, Cooling, Ready }

    public sealed class ChestSlot
    {
        public ChestSlotState State;
        public int Seconds;
    }

    public sealed class ChestSnapshot
    {
        public ChestSlot Lower = new ChestSlot();
        public ChestSlot Upper = new ChestSlot();
    }

    public sealed class ChestEngine
    {
        private const long StableMs = 2000;
        private const long RetryMs = 10000;
        private readonly bool[] readyObserved = new bool[2];
        private readonly long[] readySince = new long[2];
        private readonly int[] failures = new int[2];
        private bool hasTime;
        private long lastTime;
        private bool hasAttempt;
        private long lastAttempt;
        private bool lastAttemptConfirmed;
        private long confirmedAt;
        private int decisionSlot = -1;
        private long pendingSince;
        private int coolingObservations;
        private long lastCoolingTime;

        public int Confirmed { get; private set; }
        public int Attempts { get; private set; }
        public string Message { get; private set; }
        public bool Faulted { get; private set; }
        public int PendingSlot { get; private set; }

        public ChestEngine()
        {
            Reset();
        }

        // Pure policy only: a returned slot reserves one decision. The caller
        // must report its result through RecordClick before asking for another.
        public int Observe(long nowMs, ChestSnapshot snapshot)
        {
            if (Faulted) return -1;
            if (hasTime && nowMs < lastTime)
            {
                Message = "宝箱观察时间倒退，本次不操作";
                return -1;
            }
            hasTime = true;
            lastTime = nowMs;

            ChestSlot lower = snapshot == null ? null : snapshot.Lower;
            ChestSlot upper = snapshot == null ? null : snapshot.Upper;
            ChestSlotState lowerState = StateOf(lower);
            ChestSlotState upperState = StateOf(upper);
            UpdateReady(0, lowerState, nowMs);
            UpdateReady(1, upperState, nowMs);
            Message = "下箱 " + Describe(lower) + "；上箱 " + Describe(upper);

            if (PendingSlot != -1)
            {
                int slot = PendingSlot;
                // At the ten-second boundary the old attempt expires. A late
                // cooling transition cannot be credited to an expired click.
                if (Elapsed(nowMs, pendingSince, RetryMs))
                {
                    PendingSlot = -1;
                    coolingObservations = 0;
                    if (failures[slot] >= 3)
                    {
                        Fault(slot);
                        return -1;
                    }
                    Message += "；领取未确认，等待重试";
                }
                else
                {
                    ChestSlotState state = slot == 0 ? lowerState : upperState;
                    if (state != ChestSlotState.Cooling)
                    {
                        coolingObservations = 0;
                    }
                    else if (nowMs > pendingSince &&
                        (coolingObservations == 0 || nowMs > lastCoolingTime))
                    {
                        if (coolingObservations < 2) coolingObservations++;
                        lastCoolingTime = nowMs;
                    }

                    if (coolingObservations >= 2 && Elapsed(nowMs, pendingSince, StableMs))
                    {
                        Confirmed++;
                        failures[slot] = 0;
                        PendingSlot = -1;
                        coolingObservations = 0;
                        lastAttemptConfirmed = true;
                        confirmedAt = nowMs;
                        Message += "；已确认" + SlotName(slot) + "恢复冷却";
                    }
                    else
                    {
                        Message += "；等待" + SlotName(slot) + "领取确认";
                    }
                    return -1;
                }
            }

            if (decisionSlot != -1) return -1;
            if (hasAttempt)
            {
                if (lastAttemptConfirmed)
                {
                    if (!Elapsed(nowMs, confirmedAt, StableMs)) return -1;
                }
                else if (!Elapsed(nowMs, lastAttempt, RetryMs))
                {
                    Message += "；领取未确认，等待10秒重试间隔";
                    return -1;
                }
            }

            for (int slot = 0; slot < 2; slot++)
            {
                if (readyObserved[slot] && Elapsed(nowMs, readySince[slot], StableMs))
                {
                    decisionSlot = slot;
                    Message += "；准备领取" + SlotName(slot);
                    return slot;
                }
            }
            return -1;
        }

        public void RecordClick(long nowMs, int slot, bool dispatched)
        {
            // Do not let stale, duplicate, wrong-slot, or reset decisions
            // inflate attempts or create a confirmation opportunity.
            if (Faulted || slot != decisionSlot || decisionSlot == -1 ||
                (hasTime && nowMs < lastTime)) return;
            hasTime = true;
            lastTime = nowMs;
            decisionSlot = -1;
            Attempts++;
            failures[slot]++;
            hasAttempt = true;
            lastAttempt = nowMs;
            lastAttemptConfirmed = false;
            coolingObservations = 0;

            if (dispatched)
            {
                PendingSlot = slot;
                pendingSince = nowMs;
                Message = "已尝试领取" + SlotName(slot) + "，等待两次正数冷却确认";
            }
            else if (failures[slot] >= 3)
            {
                Fault(slot);
            }
            else
            {
                Message = SlotName(slot) + "点击未送达，等待10秒后重试";
            }
        }

        public void Reset()
        {
            Array.Clear(readyObserved, 0, readyObserved.Length);
            Array.Clear(readySince, 0, readySince.Length);
            Array.Clear(failures, 0, failures.Length);
            Confirmed = 0;
            Attempts = 0;
            Faulted = false;
            PendingSlot = -1;
            decisionSlot = -1;
            hasTime = false;
            lastTime = 0;
            hasAttempt = false;
            lastAttempt = 0;
            lastAttemptConfirmed = false;
            confirmedAt = 0;
            pendingSince = 0;
            coolingObservations = 0;
            lastCoolingTime = 0;
            Message = "等待观察宝箱";
        }

        private void UpdateReady(int slot, ChestSlotState state, long nowMs)
        {
            if (state != ChestSlotState.Ready)
            {
                readyObserved[slot] = false;
            }
            else if (!readyObserved[slot])
            {
                readyObserved[slot] = true;
                readySince[slot] = nowMs;
            }
        }

        private void Fault(int slot)
        {
            Faulted = true;
            PendingSlot = -1;
            decisionSlot = -1;
            Message = SlotName(slot) + "连续3次领取未确认，本次自动开箱已停止";
        }

        private static ChestSlotState StateOf(ChestSlot value)
        {
            if (value == null) return ChestSlotState.Unknown;
            if (value.State == ChestSlotState.Ready) return ChestSlotState.Ready;
            if (value.State == ChestSlotState.Cooling && value.Seconds > 0)
                return ChestSlotState.Cooling;
            return ChestSlotState.Unknown;
        }

        private static string Describe(ChestSlot value)
        {
            ChestSlotState state = StateOf(value);
            if (state == ChestSlotState.Ready) return "可领取";
            if (state == ChestSlotState.Cooling)
                return string.Format("冷却 {0:00}:{1:00}", value.Seconds / 60, value.Seconds % 60);
            return "状态未知";
        }

        private static string SlotName(int slot)
        {
            return slot == 0 ? "下箱" : "上箱";
        }

        private static bool Elapsed(long nowMs, long since, long duration)
        {
            // Adding only after this bound check avoids signed overflow even
            // at long.MinValue / long.MaxValue fake-clock boundaries.
            return since <= long.MaxValue - duration && nowMs >= since + duration;
        }
    }
}
