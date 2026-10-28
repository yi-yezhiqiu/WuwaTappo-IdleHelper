using System;

namespace TappoIdle
{
    public enum RunState { Stopped, Waiting, Running, Completed }

    // The caller serializes access and supplies milliseconds from one monotonic clock.
    // This class never reads the system clock or sends native input itself.
    public sealed class IdleEngine
    {
        private int intervalMs;
        private long idleDurationMs;
        private long idleAnchorMs;
        private long lastSentMs;
        private bool sending;

        public RunState State { get; private set; }
        public int Sent { get; private set; }
        public int Target { get; private set; }
        public string Reason { get; private set; }
        public long IdleRemainingMs { get; private set; }

        public IdleEngine()
        {
            State = RunState.Stopped;
            Reason = "尚未启动。";
        }

        public void Start(long nowMs, long lastPhysicalMs, int intervalMs, int target, int idleSeconds)
        {
            // Validate before changing the active session.
            if (intervalMs < 50 || intervalMs > 10000)
                throw new ArgumentOutOfRangeException("intervalMs", "发送间隔必须为 50–10000 毫秒。");
            if (target < 1 || target > 100000)
                throw new ArgumentOutOfRangeException("target", "目标次数必须为 1–100000。");
            if (idleSeconds < 5 || idleSeconds > 600)
                throw new ArgumentOutOfRangeException("idleSeconds", "闲置等待必须为 5–600 秒。");

            this.intervalMs = intervalMs;
            idleDurationMs = idleSeconds * 1000L;
            idleAnchorMs = Math.Max(nowMs, lastPhysicalMs);
            Target = target;
            Sent = 0;
            IdleRemainingMs = idleDurationMs;
            State = RunState.Waiting;
            Reason = "等待连续闲置。";
        }

        public void Tick(long nowMs, bool foreground, bool petRunning, Func<bool> sendPair)
        {
            if (State != RunState.Waiting && State != RunState.Running) return;
            if (!foreground)
            {
                Stop("助手已失去前台，已停止。");
                return;
            }
            if (!petRunning)
            {
                Stop("未检测到鸣潮桌宠运行，已停止。");
                return;
            }
            if (sending) return;

            if (State == RunState.Waiting)
            {
                long elapsed = Math.Max(0L, nowMs - idleAnchorMs);
                IdleRemainingMs = Math.Max(0L, idleDurationMs - elapsed);
                if (IdleRemainingMs > 0) return;
                State = RunState.Running;
                Reason = "正在发送模拟按键。";
            }
            else if (nowMs < lastSentMs || nowMs - lastSentMs < intervalMs)
            {
                return;
            }

            // A late tick emits only one pair; the next interval starts here.
            lastSentMs = nowMs;
            bool success = false;
            sending = true;
            try { success = sendPair != null && sendPair(); }
            catch (Exception) { success = false; }
            finally { sending = false; }

            if (!success)
            {
                Stop("发送按键失败，已停止。");
                return;
            }

            Sent++;
            // A physical-input/stop callback during output must remain stopped.
            if (State != RunState.Running) return;
            if (Sent >= Target)
            {
                State = RunState.Completed;
                IdleRemainingMs = 0;
                Reason = "已达到本次目标，已停止。";
            }
        }

        public void PhysicalInput(long nowMs)
        {
            if (State == RunState.Waiting)
            {
                idleAnchorMs = nowMs;
                IdleRemainingMs = idleDurationMs;
                Reason = "检测到真实输入，重新等待连续闲置。";
            }
            else if (State == RunState.Running)
            {
                Stop("检测到真实键盘或鼠标输入，已停止。");
            }
        }

        public void Stop(string reason)
        {
            // Keep the completed result visible until the user starts a new session.
            if (State == RunState.Completed) return;
            State = RunState.Stopped;
            IdleRemainingMs = 0;
            Reason = string.IsNullOrWhiteSpace(reason) ? "已停止。" : reason;
        }
    }
}
