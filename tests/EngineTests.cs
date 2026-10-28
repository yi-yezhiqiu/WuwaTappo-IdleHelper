using System;
using TappoIdle;

internal static class EngineTests
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Run("Start waits even after prior idle", StartWaitsAfterPriorIdle);
        Run("Physical input resets waiting countdown", PhysicalInputResetsWaiting);
        Run("Physical input stops running", PhysicalInputStopsRunning);
        Run("Focus loss stops waiting", FocusLossStopsWaiting);
        Run("Focus loss stops running", FocusLossStopsRunning);
        Run("Pet exit stops waiting", PetExitStopsWaiting);
        Run("Pet exit stops running", PetExitStopsRunning);
        Run("First send and interval boundaries", FirstSendAndIntervalBoundaries);
        Run("Delayed tick never catches up", DelayedTickNeverCatchesUp);
        Run("Target completion prevents further sends", CompletionPreventsFurtherSends);
        Run("Failed send does not count", FailedSendDoesNotCount);
        Run("Throwing send stops without counting", ThrowingSendStopsWithoutCounting);
        Run("Missing send callback stops", MissingCallbackStops);
        Run("Manual stop prevents sending", ManualStopPreventsSending);
        Run("Restart resets session and waits", RestartResetsAndWaits);
        Run("Invalid configuration is rejected atomically", InvalidConfigurationIsRejected);
        Run("Minimum configuration boundaries work", MinimumConfigurationBoundariesWork);
        Run("Maximum configuration boundaries work", MaximumConfigurationBoundariesWork);
        Run("Stop during output is not overwritten", StopDuringOutputIsNotOverwritten);
        Run("Input during output is not overwritten", InputDuringOutputIsNotOverwritten);
        Console.WriteLine("RESULT: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL: " + name + " -- " + error.Message); }
    }

    private static void Equal<T>(T expected, T actual, string detail)
    {
        if (!object.Equals(expected, actual))
            throw new Exception(detail + ": expected " + expected + ", got " + actual);
    }

    private static void HasReason(IdleEngine engine)
    {
        if (string.IsNullOrWhiteSpace(engine.Reason)) throw new Exception("Missing UI reason");
    }

    private static IdleEngine Waiting()
    {
        var engine = new IdleEngine();
        engine.Start(10000, 0, 100, 5, 5);
        return engine;
    }

    private static IdleEngine Running()
    {
        var engine = Waiting();
        engine.Tick(15000, true, true, delegate { return true; });
        Equal(RunState.Running, engine.State, "running setup");
        Equal(1, engine.Sent, "first successful output");
        return engine;
    }

    private static void StartWaitsAfterPriorIdle()
    {
        var engine = Waiting();
        int outputs = 0;
        Equal(RunState.Waiting, engine.State, "initial state");
        Equal(5000L, engine.IdleRemainingMs, "full initial countdown");
        engine.Tick(14999, true, true, delegate { outputs++; return true; });
        Equal(0, outputs, "no early output");
        Equal(1L, engine.IdleRemainingMs, "last millisecond");
        Equal(RunState.Waiting, engine.State, "still waiting");
        HasReason(engine);
    }

    private static void PhysicalInputResetsWaiting()
    {
        var engine = Waiting();
        int outputs = 0;
        engine.Tick(14000, true, true, delegate { outputs++; return true; });
        Equal(1000L, engine.IdleRemainingMs, "countdown before input");
        engine.PhysicalInput(14500);
        Equal(5000L, engine.IdleRemainingMs, "reset countdown");
        engine.Tick(19499, true, true, delegate { outputs++; return true; });
        Equal(0, outputs, "no output before reset deadline");
        engine.Tick(19500, true, true, delegate { outputs++; return true; });
        Equal(1, outputs, "output at reset deadline");
        Equal(0L, engine.IdleRemainingMs, "running has no countdown");
    }

    private static void PhysicalInputStopsRunning()
    {
        var engine = Running();
        int outputs = 0;
        engine.PhysicalInput(15050);
        engine.Tick(15100, true, true, delegate { outputs++; return true; });
        Equal(RunState.Stopped, engine.State, "physical-input stop");
        Equal(1, engine.Sent, "successful count retained");
        Equal(0, outputs, "no output after physical input");
        HasReason(engine);
    }

    private static void AssertGuardStops(IdleEngine engine, bool foreground, bool petRunning)
    {
        int countBefore = engine.Sent;
        int outputs = 0;
        engine.Tick(15000, foreground, petRunning, delegate { outputs++; return true; });
        engine.Tick(20000, true, true, delegate { outputs++; return true; });
        Equal(RunState.Stopped, engine.State, "guard must stop");
        Equal(countBefore, engine.Sent, "guard preserves count");
        Equal(0, outputs, "guard blocks output and automatic resume");
        Equal(0L, engine.IdleRemainingMs, "stopped has no countdown");
        HasReason(engine);
    }

    private static void FocusLossStopsWaiting() { AssertGuardStops(Waiting(), false, true); }
    private static void FocusLossStopsRunning() { AssertGuardStops(Running(), false, true); }
    private static void PetExitStopsWaiting() { AssertGuardStops(Waiting(), true, false); }
    private static void PetExitStopsRunning() { AssertGuardStops(Running(), true, false); }

    private static void FirstSendAndIntervalBoundaries()
    {
        var engine = Waiting();
        int outputs = 0;
        Func<bool> send = delegate { outputs++; return true; };
        engine.Tick(15000, true, true, send);
        Equal(1, outputs, "send immediately at idle threshold");
        engine.Tick(15099, true, true, send);
        Equal(1, outputs, "interval not reached");
        engine.Tick(15100, true, true, send);
        Equal(2, outputs, "interval reached");
        Equal(2, engine.Sent, "success counter");
    }

    private static void DelayedTickNeverCatchesUp()
    {
        var engine = Waiting();
        int outputs = 0;
        Func<bool> send = delegate { outputs++; return true; };
        engine.Tick(15000, true, true, send);
        engine.Tick(20000, true, true, send);
        Equal(2, outputs, "late tick emits only once");
        engine.Tick(20000, true, true, send);
        engine.Tick(20099, true, true, send);
        Equal(2, outputs, "next interval begins at actual late output");
        engine.Tick(20100, true, true, send);
        Equal(3, outputs, "next output after a full interval");
    }

    private static void CompletionPreventsFurtherSends()
    {
        var engine = new IdleEngine();
        int outputs = 0;
        engine.Start(0, 0, 100, 2, 5);
        Func<bool> send = delegate { outputs++; return true; };
        engine.Tick(5000, true, true, send);
        engine.Tick(5100, true, true, send);
        Equal(RunState.Completed, engine.State, "completed state");
        Equal(2, engine.Sent, "exact target count");
        Equal(2, engine.Target, "session target");
        engine.Tick(100000, true, true, send);
        engine.PhysicalInput(100001);
        engine.Stop("手动停止");
        engine.Tick(100100, false, false, send);
        Equal(2, outputs, "completed session never outputs again");
        Equal(RunState.Completed, engine.State, "completed result retained");
        Equal(0L, engine.IdleRemainingMs, "completion has no countdown");
        HasReason(engine);
    }

    private static void FailedSendDoesNotCount()
    {
        var engine = Running();
        int attempts = 0;
        Func<bool> send = delegate { attempts++; return false; };
        engine.Tick(15100, true, true, send);
        engine.Tick(15200, true, true, send);
        Equal(RunState.Stopped, engine.State, "false result stops");
        Equal(1, engine.Sent, "failed output not counted");
        Equal(1, attempts, "no retry after failure");
        HasReason(engine);
    }

    private static void ThrowingSendStopsWithoutCounting()
    {
        var engine = Waiting();
        int attempts = 0;
        Func<bool> send = delegate { attempts++; throw new InvalidOperationException("fake send failure"); };
        engine.Tick(15000, true, true, send);
        engine.Tick(16000, true, true, send);
        Equal(RunState.Stopped, engine.State, "exception stops");
        Equal(0, engine.Sent, "throwing output not counted");
        Equal(1, attempts, "no retry after exception");
        HasReason(engine);
    }

    private static void MissingCallbackStops()
    {
        var engine = Waiting();
        engine.Tick(15000, true, true, null);
        Equal(RunState.Stopped, engine.State, "missing output callback fails closed");
        Equal(0, engine.Sent, "missing callback not counted");
        HasReason(engine);
    }

    private static void ManualStopPreventsSending()
    {
        var engine = Waiting();
        int outputs = 0;
        engine.Stop("用户停止");
        engine.Tick(15000, true, true, delegate { outputs++; return true; });
        Equal(RunState.Stopped, engine.State, "manual stop state");
        Equal("用户停止", engine.Reason, "caller reason shown");
        Equal(0, outputs, "manual stop blocks output");
    }

    private static void RestartResetsAndWaits()
    {
        var engine = new IdleEngine();
        engine.Start(0, 0, 100, 1, 5);
        engine.Tick(5000, true, true, delegate { return true; });
        Equal(RunState.Completed, engine.State, "first session complete");
        engine.Start(6000, 0, 200, 3, 5);
        Equal(0, engine.Sent, "restart clears sent count");
        Equal(3, engine.Target, "restart applies target");
        Equal(RunState.Waiting, engine.State, "restart waits");
        int outputs = 0;
        engine.Tick(10999, true, true, delegate { outputs++; return true; });
        Equal(0, outputs, "restart never inherits previous idle");
        engine.Tick(11000, true, true, delegate { outputs++; return true; });
        Equal(1, outputs, "restart sends at new threshold");
    }

    private static void Rejects(Action start, string parameter)
    {
        try { start(); }
        catch (ArgumentOutOfRangeException error)
        {
            Equal(parameter, error.ParamName, "invalid parameter name");
            return;
        }
        throw new Exception("Expected ArgumentOutOfRangeException for " + parameter);
    }

    private static void InvalidConfigurationIsRejected()
    {
        var engine = Running();
        Rejects(delegate { engine.Start(0, 0, 49, 1, 5); }, "intervalMs");
        Rejects(delegate { engine.Start(0, 0, 10001, 1, 5); }, "intervalMs");
        Rejects(delegate { engine.Start(0, 0, 100, 0, 5); }, "target");
        Rejects(delegate { engine.Start(0, 0, 100, 100001, 5); }, "target");
        Rejects(delegate { engine.Start(0, 0, 100, 1, 4); }, "idleSeconds");
        Rejects(delegate { engine.Start(0, 0, 100, 1, 601); }, "idleSeconds");
        Equal(RunState.Running, engine.State, "invalid restart preserves active state");
        Equal(1, engine.Sent, "invalid restart preserves count");
        Equal(5, engine.Target, "invalid restart preserves target");
    }

    private static void MinimumConfigurationBoundariesWork()
    {
        var engine = new IdleEngine();
        engine.Start(0, 0, 50, 2, 5);
        engine.Tick(5000, true, true, delegate { return true; });
        engine.Tick(5049, true, true, delegate { throw new Exception("early send"); });
        Equal(RunState.Running, engine.State, "minimum interval still waiting");
        engine.Tick(5050, true, true, delegate { return true; });
        Equal(RunState.Completed, engine.State, "minimum interval accepted");
        engine.Start(0, 0, 50, 1, 5);
        engine.Tick(5000, true, true, delegate { return true; });
        Equal(RunState.Completed, engine.State, "minimum target accepted");
    }

    private static void MaximumConfigurationBoundariesWork()
    {
        var engine = new IdleEngine();
        engine.Start(0, 0, 10000, 100000, 600);
        int outputs = 0;
        Func<bool> send = delegate { outputs++; return true; };
        engine.Tick(599999, true, true, send);
        Equal(1L, engine.IdleRemainingMs, "maximum idle boundary");
        Equal(0, outputs, "maximum idle not reached");
        engine.Tick(600000, true, true, send);
        engine.Tick(609999, true, true, send);
        Equal(1, outputs, "maximum interval not reached");
        engine.Tick(610000, true, true, send);
        Equal(2, outputs, "maximum interval accepted");
        Equal(100000, engine.Target, "maximum target accepted");
    }

    private static void StopDuringOutputIsNotOverwritten()
    {
        var engine = Waiting();
        int outputs = 0;
        Func<bool> send = delegate { outputs++; engine.Stop("急停"); return true; };
        engine.Tick(15000, true, true, send);
        engine.Tick(16000, true, true, send);
        Equal(RunState.Stopped, engine.State, "callback stop retained");
        Equal("急停", engine.Reason, "callback stop reason retained");
        Equal(1, engine.Sent, "successful in-flight output counted");
        Equal(1, outputs, "no more output after callback stop");
    }

    private static void InputDuringOutputIsNotOverwritten()
    {
        var engine = Waiting();
        int outputs = 0;
        Func<bool> send = delegate { outputs++; engine.PhysicalInput(15000); return true; };
        engine.Tick(15000, true, true, send);
        engine.Tick(16000, true, true, send);
        Equal(RunState.Stopped, engine.State, "callback input stop retained");
        Equal(1, engine.Sent, "successful in-flight output counted");
        Equal(1, outputs, "no further output after callback input");
    }
}
