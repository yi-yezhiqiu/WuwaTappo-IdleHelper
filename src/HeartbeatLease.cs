using System;

namespace TappoIdle
{
    public sealed class HeartbeatLease
    {
        private readonly object gate = new object();
        private readonly Func<long> monotonicClock;
        private bool hasPulse;
        private bool revoked;
        private long lastPulse;

        public HeartbeatLease() { }

        public HeartbeatLease(Func<long> monotonicClock)
        {
            if (monotonicClock == null) throw new ArgumentNullException("monotonicClock");
            this.monotonicClock = monotonicClock;
        }

        public void Pulse()
        {
            lock (gate)
            {
                PulseLocked(ReadClock());
            }
        }

        public bool IsAlive()
        {
            lock (gate)
            {
                return IsAliveLocked(ReadClock());
            }
        }

        public void Pulse(long nowMs)
        {
            lock (gate)
            {
                PulseLocked(nowMs);
            }
        }

        public bool IsAlive(long nowMs)
        {
            lock (gate)
            {
                return IsAliveLocked(nowMs);
            }
        }

        public void Revoke()
        {
            lock (gate)
            {
                revoked = true;
            }
        }

        private long ReadClock()
        {
            if (monotonicClock == null)
                throw new InvalidOperationException("未配置心跳时钟，请使用带时钟的构造函数或显式时间参数。");
            return monotonicClock();
        }

        private void PulseLocked(long nowMs)
        {
            if (revoked) return;
            if (hasPulse && !WithinLease(nowMs))
            {
                revoked = true;
                return;
            }
            lastPulse = nowMs;
            hasPulse = true;
        }

        private bool IsAliveLocked(long nowMs)
        {
            if (revoked || !hasPulse) return false;
            if (!WithinLease(nowMs))
            {
                revoked = true;
                return false;
            }
            return true;
        }

        // Called only while gate is held. Guard the addition so fake clocks
        // at either long boundary cannot turn an expired lease into a live one.
        private bool WithinLease(long nowMs)
        {
            return nowMs >= lastPulse &&
                (lastPulse > long.MaxValue - 200 || nowMs <= lastPulse + 200);
        }
    }
}
