namespace TappoIdle
{
    public sealed class LoopGuard
    {
        private bool initialized;
        private bool failed;
        private long lastCheckMs;

        public void Reset(long nowMs)
        {
            lastCheckMs = nowMs;
            initialized = true;
            failed = false;
        }

        public bool Check(long nowMs)
        {
            if (!initialized)
            {
                Reset(nowMs);
                return true;
            }

            // A signed overflow also represents an interval far beyond 200 ms.
            long elapsed = unchecked(nowMs - lastCheckMs);
            if (nowMs < lastCheckMs || elapsed < 0 || elapsed > 200) failed = true;
            lastCheckMs = nowMs;
            return !failed;
        }
    }
}
