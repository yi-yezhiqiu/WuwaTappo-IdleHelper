using System;
using TappoIdle;

internal static class LoopGuardTests
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Run("First check establishes baseline", FirstCheckEstablishesBaseline);
        Run("Exactly 200 milliseconds is allowed", ExactBoundaryIsAllowed);
        Run("201 milliseconds fails and stays failed", LateCheckIsSticky);
        Run("Backward time fails and stays failed", BackwardTimeIsSticky);
        Run("Each successful check updates baseline", EachCheckUpdatesBaseline);
        Run("Reset recovers and replaces baseline", ResetRecovers);
        Run("Very large elapsed time cannot overflow into success", OverflowCannotPass);
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
        if (actual != expected) throw new Exception(detail + ": expected " + expected + ", got " + actual);
    }

    private static void FirstCheckEstablishesBaseline()
    {
        var guard = new LoopGuard();
        Equal(true, guard.Check(100000), "first check accepts any baseline");
        Equal(true, guard.Check(100200), "next check uses first baseline");
        Equal(false, guard.Check(100401), "late check after implicit initialization");
    }

    private static void ExactBoundaryIsAllowed()
    {
        var guard = new LoopGuard();
        guard.Reset(10);
        Equal(true, guard.Check(10), "same timestamp");
        Equal(true, guard.Check(210), "exact 200 ms");
        Equal(true, guard.Check(410), "exact 200 ms again");
    }

    private static void LateCheckIsSticky()
    {
        var guard = new LoopGuard();
        guard.Reset(0);
        Equal(true, guard.Check(0), "healthy before lateness");
        Equal(false, guard.Check(201), "201 ms exceeds limit");
        Equal(false, guard.Check(202), "failure does not clear with fast checks");
        Equal(false, guard.Check(202), "same timestamp cannot clear failure");
    }

    private static void BackwardTimeIsSticky()
    {
        var guard = new LoopGuard();
        guard.Reset(1000);
        Equal(true, guard.Check(1000), "healthy before reversal");
        Equal(false, guard.Check(999), "time reversal");
        Equal(false, guard.Check(1000), "forward clock does not clear failure");
    }

    private static void EachCheckUpdatesBaseline()
    {
        var guard = new LoopGuard();
        guard.Reset(0);
        Equal(true, guard.Check(150), "first interval");
        Equal(true, guard.Check(300), "second interval uses last check");
        Equal(true, guard.Check(500), "third interval uses last check");
        Equal(false, guard.Check(701), "late interval after repeated checks");
    }

    private static void ResetRecovers()
    {
        var guard = new LoopGuard();
        guard.Reset(0);
        Equal(false, guard.Check(201), "fail before reset");
        guard.Reset(5000);
        Equal(true, guard.Check(5200), "reset clears prior failure");
        guard.Reset(20);
        Equal(true, guard.Check(220), "reset replaces even a newer prior baseline");
        Equal(false, guard.Check(421), "reset retains normal limit");
    }

    private static void OverflowCannotPass()
    {
        var guard = new LoopGuard();
        guard.Reset(long.MinValue);
        Equal(false, guard.Check(long.MaxValue), "huge ascending interval");
        guard.Reset(long.MaxValue - 200);
        Equal(true, guard.Check(long.MaxValue), "valid boundary near maximum timestamp");
    }
}
