using System;
using System.Threading;
using TappoIdle;

internal static class HeartbeatLeaseTests
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Run("Uninitialized query allows a later first pulse", UninitializedDoesNotRevoke);
        Run("Pulse is alive at zero and 200 milliseconds", ExactBoundaryIsAlive);
        Run("201 milliseconds expires and stays expired", ExpiryCannotRevive);
        Run("Regular pulses renew the lease", PulsesRenewLease);
        Run("Late pulse cannot hide an expired interval", LatePulseRevokes);
        Run("Backward query permanently expires the lease", BackwardQueryRevokes);
        Run("Backward pulse permanently expires the lease", BackwardPulseRevokes);
        Run("Repeated timestamps remain valid", RepeatedTimestampsAreValid);
        Run("Revoke permanently disables an active lease", RevokeActiveLease);
        Run("Revoke before initialization prevents activation", RevokeUninitializedLease);
        Run("Replacement object starts an independent lease", NewObjectHasIndependentLease);
        Run("Extreme clock ranges cannot overflow into validity", ExtremeClockRange);
        Run("Parameterless methods use the injected clock", InjectedClockIsUsed);
        Run("Injected clock expiration remains permanent", InjectedExpiryCannotRevive);
        Run("Injected late pulse cannot revive the lease", InjectedLatePulseRevokes);
        Run("Injected backward query permanently revokes", InjectedBackwardQueryRevokes);
        Run("Injected backward pulse permanently revokes", InjectedBackwardPulseRevokes);
        Run("Injected heartbeat respects explicit revocation", InjectedRevokeIsPermanent);
        Run("Parameterless pulse requires a clock", ParameterlessPulseRequiresClock);
        Run("Parameterless query requires a clock", ParameterlessQueryRequiresClock);
        Run("Concurrent increasing clock readings do not falsely revoke", ConcurrentIncreasingClockRemainsAlive);
        Console.WriteLine("RESULT: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL: " + name + " -- " + error.Message); }
    }

    private static void Equal(bool expected, bool actual, string detail)
    {
        if (actual != expected)
            throw new Exception(detail + ": expected " + expected + ", got " + actual);
    }

    private static void UninitializedDoesNotRevoke()
    {
        var lease = new HeartbeatLease();
        Equal(false, lease.IsAlive(0), "not initialized");
        Equal(false, lease.IsAlive(100000), "query without pulse is still inactive");
        lease.Pulse(100001);
        Equal(true, lease.IsAlive(100001), "first pulse activates");
    }

    private static void ExactBoundaryIsAlive()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(1000);
        Equal(true, lease.IsAlive(1000), "pulse timestamp");
        Equal(true, lease.IsAlive(1199), "one ms before boundary");
        Equal(true, lease.IsAlive(1200), "exact 200 ms boundary");
    }

    private static void ExpiryCannotRevive()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(0);
        Equal(true, lease.IsAlive(200), "alive before expiration");
        Equal(false, lease.IsAlive(201), "201 ms expiration");
        lease.Pulse(201);
        Equal(false, lease.IsAlive(201), "pulse cannot revive expired lease");
        lease.Pulse(202);
        Equal(false, lease.IsAlive(202), "fast pulse cannot revive either");
        Equal(false, lease.IsAlive(0), "moving clock back cannot revive");
    }

    private static void PulsesRenewLease()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(10);
        Equal(true, lease.IsAlive(210), "first deadline");
        lease.Pulse(210);
        Equal(true, lease.IsAlive(410), "200 ms pulse interval accepted");
        lease.Pulse(410);
        Equal(true, lease.IsAlive(610), "latest pulse replaces deadline");
        Equal(false, lease.IsAlive(611), "queries do not themselves renew lease");
    }

    private static void LatePulseRevokes()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(1000);
        Equal(true, lease.IsAlive(1100), "initial lease active");
        lease.Pulse(1201);
        Equal(false, lease.IsAlive(1201), "late pulse cannot conceal missed deadline");
        lease.Pulse(1202);
        Equal(false, lease.IsAlive(1202), "later pulses remain blocked");
    }

    private static void BackwardQueryRevokes()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(1000);
        Equal(true, lease.IsAlive(1000), "active first");
        Equal(false, lease.IsAlive(999), "query before last pulse");
        Equal(false, lease.IsAlive(1000), "clock recovery cannot clear failure");
        lease.Pulse(1000);
        Equal(false, lease.IsAlive(1000), "new pulse cannot clear failure");
    }

    private static void BackwardPulseRevokes()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(1000);
        Equal(true, lease.IsAlive(1000), "active first");
        lease.Pulse(999);
        Equal(false, lease.IsAlive(1000), "backward pulse revokes");
        lease.Pulse(1100);
        Equal(false, lease.IsAlive(1100), "normal pulse does not recover");
    }

    private static void RepeatedTimestampsAreValid()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(1000);
        lease.Pulse(1000);
        Equal(true, lease.IsAlive(1000), "same pulse time is allowed");
        Equal(true, lease.IsAlive(1000), "same query time is allowed");
        Equal(true, lease.IsAlive(1200), "same-time repeats do not alter deadline");
    }

    private static void RevokeActiveLease()
    {
        var lease = new HeartbeatLease();
        lease.Pulse(1000);
        Equal(true, lease.IsAlive(1000), "initially active");
        lease.Revoke();
        Equal(false, lease.IsAlive(1000), "immediately revoked");
        lease.Revoke();
        lease.Pulse(1100);
        Equal(false, lease.IsAlive(1100), "revocation is idempotent and permanent");
    }

    private static void RevokeUninitializedLease()
    {
        var lease = new HeartbeatLease();
        lease.Revoke();
        lease.Pulse(0);
        Equal(false, lease.IsAlive(0), "revoked lease never activates");
        // Keep a live control to catch an implementation that simply never activates.
        var active = new HeartbeatLease();
        active.Pulse(0);
        Equal(true, active.IsAlive(0), "fresh control activates");
    }

    private static void NewObjectHasIndependentLease()
    {
        var expired = new HeartbeatLease();
        expired.Pulse(0);
        Equal(false, expired.IsAlive(201), "old task expired");
        var replacement = new HeartbeatLease();
        replacement.Pulse(201);
        Equal(true, replacement.IsAlive(401), "new task owns a fresh lease");
        Equal(false, expired.IsAlive(201), "old task remains disabled");
    }

    private static void ExtremeClockRange()
    {
        var hugeInterval = new HeartbeatLease();
        hugeInterval.Pulse(long.MinValue);
        Equal(false, hugeInterval.IsAlive(long.MaxValue), "elapsed interval cannot overflow");
        var finalBoundary = new HeartbeatLease();
        finalBoundary.Pulse(long.MaxValue - 200);
        Equal(true, finalBoundary.IsAlive(long.MaxValue), "valid boundary near maximum timestamp");
        var finalPulse = new HeartbeatLease();
        finalPulse.Pulse(long.MaxValue);
        Equal(true, finalPulse.IsAlive(long.MaxValue), "zero elapsed at final timestamp");
        Equal(false, finalPulse.IsAlive(long.MinValue), "extreme reversal cannot become valid");
        var latePulse = new HeartbeatLease();
        latePulse.Pulse(long.MinValue);
        latePulse.Pulse(long.MaxValue);
        Equal(false, latePulse.IsAlive(long.MaxValue), "huge late pulse cannot revive");
    }

    private static void InjectedClockIsUsed()
    {
        long now = 1000;
        var lease = new HeartbeatLease(delegate { return now; });
        Equal(false, lease.IsAlive(), "clock-backed lease still needs first pulse");
        lease.Pulse();
        Equal(true, lease.IsAlive(), "injected pulse activates");
        now = 1200;
        Equal(true, lease.IsAlive(), "query reads advanced injected clock");
        lease.Pulse();
        now = 1400;
        Equal(true, lease.IsAlive(), "injected pulse renews lease");
        now = 1401;
        Equal(false, lease.IsAlive(), "query does not renew injected deadline");
    }

    private static void InjectedExpiryCannotRevive()
    {
        long now = 0;
        var lease = new HeartbeatLease(delegate { return now; });
        lease.Pulse();
        Equal(true, lease.IsAlive(), "live control before expiration");
        now = 201;
        Equal(false, lease.IsAlive(), "injected lease expired");
        lease.Pulse();
        Equal(false, lease.IsAlive(), "expired injected lease does not revive");
    }

    private static void InjectedLatePulseRevokes()
    {
        long now = 0;
        var lease = new HeartbeatLease(delegate { return now; });
        lease.Pulse();
        Equal(true, lease.IsAlive(), "live control before late pulse");
        now = 201;
        lease.Pulse();
        Equal(false, lease.IsAlive(), "late injected pulse cannot hide outage");
        now = 202;
        lease.Pulse();
        Equal(false, lease.IsAlive(), "normal pulse after outage cannot recover");
    }

    private static void InjectedBackwardQueryRevokes()
    {
        long now = 1000;
        var lease = new HeartbeatLease(delegate { return now; });
        lease.Pulse();
        Equal(true, lease.IsAlive(), "active before reversal");
        now = 999;
        Equal(false, lease.IsAlive(), "injected backward query revokes");
        now = 1000;
        lease.Pulse();
        Equal(false, lease.IsAlive(), "restored clock does not recover");
    }

    private static void InjectedBackwardPulseRevokes()
    {
        long now = 1000;
        var lease = new HeartbeatLease(delegate { return now; });
        lease.Pulse();
        Equal(true, lease.IsAlive(), "active before pulse reversal");
        now = 999;
        lease.Pulse();
        now = 1000;
        Equal(false, lease.IsAlive(), "injected backward pulse revokes");
        lease.Pulse();
        Equal(false, lease.IsAlive(), "restored pulse cannot revive");
    }

    private static void InjectedRevokeIsPermanent()
    {
        long now = 0;
        var lease = new HeartbeatLease(delegate { return now; });
        lease.Pulse();
        Equal(true, lease.IsAlive(), "initially active");
        lease.Revoke();
        now = 1;
        lease.Pulse();
        Equal(false, lease.IsAlive(), "injected pulse cannot undo revoke");
    }

    private static void ParameterlessPulseRequiresClock()
    {
        var lease = new HeartbeatLease();
        try { lease.Pulse(); }
        catch (InvalidOperationException error)
        {
            if (string.IsNullOrWhiteSpace(error.Message)) throw new Exception("Missing clock configuration message");
            return;
        }
        throw new Exception("Missing InvalidOperationException for parameterless pulse without clock");
    }

    private static void ParameterlessQueryRequiresClock()
    {
        var lease = new HeartbeatLease();
        try { lease.IsAlive(); }
        catch (InvalidOperationException error)
        {
            if (string.IsNullOrWhiteSpace(error.Message)) throw new Exception("Missing clock configuration message");
            return;
        }
        throw new Exception("Missing InvalidOperationException for parameterless query without clock");
    }

    private static void ConcurrentIncreasingClockRemainsAlive()
    {
        int ticks = 0;
        int rejected = 0;
        var lease = new HeartbeatLease(delegate {
            long value = Interlocked.Increment(ref ticks);
            // Encourage context switches after sampling. Sampling outside the
            // lease lock can then reorder timestamps despite a healthy clock.
            Thread.Yield();
            return value;
        });
        lease.Pulse();
        var start = new ManualResetEvent(false);
        var threads = new Thread[4];
        var errors = new Exception[4];
        for (int worker = 0; worker < threads.Length; worker++)
        {
            int index = worker;
            threads[worker] = new Thread(delegate() {
                try
                {
                    start.WaitOne();
                    for (int step = 0; step < 20; step++)
                    {
                        if ((step + index) % 2 == 0) lease.Pulse();
                        else if (!lease.IsAlive()) Interlocked.Increment(ref rejected);
                    }
                }
                catch (Exception error) { errors[index] = error; }
            });
            threads[worker].IsBackground = true;
            threads[worker].Start();
        }
        start.Set();
        for (int worker = 0; worker < threads.Length; worker++)
        {
            if (!threads[worker].Join(5000)) throw new Exception("Concurrent lease worker did not finish");
            if (errors[worker] != null) throw new Exception("Concurrent lease worker failed: " + errors[worker].Message);
        }
        start.Close();
        // At most 82 clock steps across the whole test, safely below 200 ms.
        Equal(true, rejected == 0, "healthy concurrent queries never rejected");
        Equal(true, lease.IsAlive(), "healthy concurrent lease remains active");
    }
}
