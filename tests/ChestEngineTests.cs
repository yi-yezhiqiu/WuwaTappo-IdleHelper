using System;
using TappoIdle;

internal static class ChestEngineTests
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Run("Ready must remain visible for 2000 ms", ReadyRequiresTwoSeconds);
        Run("Upper ready window is independent", IndependentReadyWindows);
        Run("Unknown breaks ready stability", UnknownBreaksReadyWindow);
        Run("Nonpositive cooling is unknown", InvalidCoolingBreaksReadyWindow);
        Run("Missing snapshot breaks ready stability", MissingSnapshotBreaksReadyWindow);
        Run("Ready decision alone never counts a click", DecisionIsNotAttempt);
        Run("Dispatched click enters pending without success", ClickEntersPending);
        Run("Pending locks out the other ready box", PendingBlocksAnotherBox);
        Run("Two positive cooling observations confirm the clicked box", TwoCoolingObservationsConfirm);
        Run("Cooling confirmation waits two seconds after click", ConfirmationWaitsTwoSeconds);
        Run("Duplicate timestamps cannot double confirm", DuplicateObservationsDoNotConfirm);
        Run("Cooling from the other box does not confirm", OtherSlotCannotConfirm);
        Run("Unknown and missing snapshots never count success", UnknownDoesNotConfirm);
        Run("Ready interrupts consecutive cooling confirmation", ReadyBreaksConfirmationStreak);
        Run("Unknown interrupts consecutive cooling confirmation", UnknownBreaksConfirmationStreak);
        Run("Invalid cooling interrupts consecutive confirmation", InvalidCoolingBreaksConfirmationStreak);
        Run("Pending timeout allows a retry at ten seconds", TimeoutAllowsThrottledRetry);
        Run("Timed out cooling cannot count a late success", LateCoolingDoesNotConfirm);
        Run("Failed dispatch counts attempts and respects throttle", FailedDispatchIsThrottled);
        Run("Cooling after a failed dispatch is never credited", FailedDispatchCannotConfirm);
        Run("Three failed dispatches stop both boxes", ThreeDispatchFailuresFault);
        Run("Third actual click still gets a confirmation chance", ThirdClickCanConfirm);
        Run("Third unconfirmed click faults on timeout", ThirdTimeoutFaults);
        Run("Successful cooling resets consecutive slot failures", ConfirmationResetsFailures);
        Run("Other box success does not clear slot failures", OtherSlotSuccessPreservesFailures);
        Run("Other box waits two seconds after confirmation", SuccessAnimationPause);
        Run("Consumed decisions cannot be recorded twice", DuplicateRecordDoesNotCount);
        Run("Wrong-slot records cannot authorize a click", WrongRecordDoesNotCount);
        Run("Backward observations never click or confirm", BackwardTimeCannotAct);
        Run("Backward click records do not count", BackwardClickRecordDoesNotCount);
        Run("Click timestamp is not a subsequent observation", ClickTimestampDoesNotConfirm);
        Run("Reset clears faults counts pending and time", ResetClearsSession);
        Run("Reset cancels an outstanding decision", ResetCancelsDecision);
        Run("Elapsed checks handle extreme timestamps", ExtremeTimestampBoundaries);
        Run("Known insufficient clicks wait without attempts or faults", InsufficientClickBalanceWaits);
        Run("Reaching 1000 clicks preserves the stable ready window", ThresholdPreservesStableReady);
        Run("Low balance cannot block pending cooling confirmation", LowBalanceAllowsPendingConfirmation);
        Run("Low balance preserves cooling and readiness transitions", LowBalancePreservesCoolingState);
        Run("Waiting for clicks does not consume the retry budget", LowBalancePreservesRetryBudget);
        Run("Unknown available clicks retain the legacy policy", UnknownBalanceKeepsLegacyPolicy);
        Run("Fresh low balance cancels an undispatched decision without failure", FreshLowBalanceCancelsDecision);
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

    private static ChestSnapshot Snapshot(ChestSlotState lower, int lowerSeconds,
        ChestSlotState upper, int upperSeconds)
    {
        return new ChestSnapshot {
            Lower = new ChestSlot { State = lower, Seconds = lowerSeconds },
            Upper = new ChestSlot { State = upper, Seconds = upperSeconds }
        };
    }

    private static ChestSnapshot ReadyBoth()
    {
        return Snapshot(ChestSlotState.Ready, 0, ChestSlotState.Ready, 0);
    }

    private static ChestSnapshot ReadyLower()
    {
        return Snapshot(ChestSlotState.Ready, 0, ChestSlotState.Unknown, 0);
    }

    private static ChestSnapshot CoolingLower()
    {
        return Snapshot(ChestSlotState.Cooling, 1799, ChestSlotState.Ready, 0);
    }

    private static ChestSnapshot WithClicks(ChestSnapshot snapshot, long? availableClicks)
    {
        snapshot.AvailableClicks = availableClicks;
        return snapshot;
    }

    private static ChestEngine ClickedLower()
    {
        var engine = new ChestEngine();
        Equal(-1, engine.Observe(0, ReadyBoth()), "first observation");
        Equal(0, engine.Observe(2000, ReadyBoth()), "lower first");
        engine.RecordClick(2000, 0, true);
        return engine;
    }

    private static void ReadyRequiresTwoSeconds()
    {
        var engine = new ChestEngine();
        Equal(-1, engine.Observe(100, ReadyLower()), "initial ready");
        Equal(-1, engine.Observe(2099, ReadyLower()), "one millisecond early");
        Equal(0, engine.Observe(2100, ReadyLower()), "exact stable boundary");
        Equal(0, engine.Confirmed, "ready is not confirmation");
    }

    private static void IndependentReadyWindows()
    {
        var engine = new ChestEngine();
        engine.Observe(0, Snapshot(ChestSlotState.Ready, 0, ChestSlotState.Unknown, 0));
        engine.Observe(1500, Snapshot(ChestSlotState.Cooling, 60, ChestSlotState.Ready, 0));
        Equal(-1, engine.Observe(3499, Snapshot(ChestSlotState.Cooling, 58, ChestSlotState.Ready, 0)), "upper not stable yet");
        Equal(1, engine.Observe(3500, Snapshot(ChestSlotState.Cooling, 58, ChestSlotState.Ready, 0)), "upper own stable window");
    }

    private static void UnknownBreaksReadyWindow()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyLower());
        engine.Observe(1900, new ChestSnapshot());
        Equal(-1, engine.Observe(2000, ReadyLower()), "unknown reset");
        Equal(-1, engine.Observe(3999, ReadyLower()), "new window incomplete");
        Equal(0, engine.Observe(4000, ReadyLower()), "new full window");
    }

    private static void InvalidCoolingBreaksReadyWindow()
    {
        foreach (int seconds in new int[] { 0, -1 })
        {
            var engine = new ChestEngine();
            engine.Observe(0, ReadyLower());
            engine.Observe(1900, Snapshot(ChestSlotState.Cooling, seconds, ChestSlotState.Unknown, 0));
            Equal(-1, engine.Observe(2000, ReadyLower()), "invalid cooling reset");
            Equal(0, engine.Observe(4000, ReadyLower()), "new ready window");
        }
    }

    private static void MissingSnapshotBreaksReadyWindow()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyLower());
        engine.Observe(1900, null);
        Equal(-1, engine.Observe(2000, ReadyLower()), "snapshot error resets");
        engine.Observe(3900, new ChestSnapshot { Lower = null, Upper = null });
        Equal(-1, engine.Observe(4000, ReadyLower()), "missing slot resets");
        Equal(0, engine.Observe(6000, ReadyLower()), "full valid window");
    }

    private static void DecisionIsNotAttempt()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyBoth());
        Equal(0, engine.Observe(2000, ReadyBoth()), "one decision");
        Equal(0, engine.Attempts, "decision not dispatched");
        Equal(-1, engine.PendingSlot, "decision not pending click");
        Equal(-1, engine.Observe(2100, ReadyBoth()), "no additional decision until recorded");
        Equal(0, engine.Confirmed, "no confirmation");
    }

    private static void ClickEntersPending()
    {
        var engine = ClickedLower();
        Equal(1, engine.Attempts, "one actual attempt");
        Equal(0, engine.PendingSlot, "lower pending");
        Equal(0, engine.Confirmed, "dispatch is not confirmation");
        if (string.IsNullOrWhiteSpace(engine.Message)) throw new Exception("Missing Chinese UI status");
    }

    private static void PendingBlocksAnotherBox()
    {
        var engine = ClickedLower();
        Equal(-1, engine.Observe(3000, ReadyBoth()), "lower still pending");
        Equal(-1, engine.Observe(11999, ReadyBoth()), "one ms before timeout");
        Equal(0, engine.PendingSlot, "pending retained");
        Equal(1, engine.Attempts, "no implicit retry");
    }

    private static void TwoCoolingObservationsConfirm()
    {
        var engine = ClickedLower();
        engine.Observe(4000, CoolingLower());
        Equal(0, engine.Confirmed, "one cooling not enough");
        Equal(0, engine.PendingSlot, "still pending after one sample");
        Equal(-1, engine.Observe(4100, CoolingLower()), "confirmation emits no click");
        Equal(1, engine.Confirmed, "two positive observations");
        Equal(-1, engine.PendingSlot, "pending cleared");
        engine.Observe(4200, CoolingLower());
        Equal(1, engine.Confirmed, "same round only counted once");
    }

    private static void ConfirmationWaitsTwoSeconds()
    {
        var engine = ClickedLower();
        engine.Observe(2100, CoolingLower());
        engine.Observe(2200, CoolingLower());
        Equal(0, engine.Confirmed, "animation interval incomplete");
        engine.Observe(3999, CoolingLower());
        Equal(0, engine.Confirmed, "one ms early");
        engine.Observe(4000, CoolingLower());
        Equal(1, engine.Confirmed, "two seconds since click");
    }

    private static void DuplicateObservationsDoNotConfirm()
    {
        var engine = ClickedLower();
        engine.Observe(4000, CoolingLower());
        engine.Observe(4000, CoolingLower());
        Equal(0, engine.Confirmed, "duplicate timestamp is one observation");
        engine.Observe(4001, CoolingLower());
        Equal(1, engine.Confirmed, "new timestamp is independent");
    }

    private static void OtherSlotCannotConfirm()
    {
        var engine = ClickedLower();
        var otherCooling = Snapshot(ChestSlotState.Ready, 0, ChestSlotState.Cooling, 1799);
        engine.Observe(4000, otherCooling);
        engine.Observe(5000, otherCooling);
        Equal(0, engine.Confirmed, "upper cooling does not confirm lower click");
        Equal(0, engine.PendingSlot, "lower remains pending");
    }

    private static void UnknownDoesNotConfirm()
    {
        var engine = ClickedLower();
        engine.Observe(4000, new ChestSnapshot());
        engine.Observe(5000, null);
        engine.Observe(6000, new ChestSnapshot { Lower = null });
        Equal(0, engine.Confirmed, "missing button or UIA error is not success");
    }

    private static void ReadyBreaksConfirmationStreak()
    {
        AssertConfirmationInterrupted(ReadyBoth());
    }

    private static void UnknownBreaksConfirmationStreak()
    {
        AssertConfirmationInterrupted(null);
    }

    private static void InvalidCoolingBreaksConfirmationStreak()
    {
        AssertConfirmationInterrupted(Snapshot(ChestSlotState.Cooling, 0, ChestSlotState.Ready, 0));
        AssertConfirmationInterrupted(Snapshot(ChestSlotState.Cooling, -20, ChestSlotState.Ready, 0));
    }

    private static void AssertConfirmationInterrupted(ChestSnapshot interruption)
    {
        var engine = ClickedLower();
        engine.Observe(4000, CoolingLower());
        engine.Observe(4100, interruption);
        engine.Observe(4200, CoolingLower());
        Equal(0, engine.Confirmed, "interruption cleared first cooling sample");
        engine.Observe(4300, CoolingLower());
        Equal(1, engine.Confirmed, "new consecutive pair confirms");
    }

    private static void TimeoutAllowsThrottledRetry()
    {
        var engine = ClickedLower();
        Equal(-1, engine.Observe(11999, ReadyBoth()), "no early retry");
        Equal(0, engine.Observe(12000, ReadyBoth()), "retry at ten seconds");
        Equal(-1, engine.PendingSlot, "expired pending released before decision");
        engine.RecordClick(12000, 0, true);
        Equal(2, engine.Attempts, "second attempt");
        Equal(0, engine.Confirmed, "timeout not success");
    }

    private static void LateCoolingDoesNotConfirm()
    {
        var engine = ClickedLower();
        engine.Observe(11999, CoolingLower());
        engine.Observe(12000, CoolingLower());
        Equal(0, engine.Confirmed, "confirmation expired at deadline");
        Equal(-1, engine.PendingSlot, "pending expired");
        engine.Observe(13000, CoolingLower());
        Equal(0, engine.Confirmed, "late cooling not attributed to expired click");
    }

    private static void FailedDispatchIsThrottled()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyBoth());
        Equal(0, engine.Observe(2000, ReadyBoth()), "ready decision");
        engine.RecordClick(2000, 0, false);
        Equal(1, engine.Attempts, "false dispatch still an attempt");
        Equal(-1, engine.PendingSlot, "false dispatch no pending");
        Equal(-1, engine.Observe(11999, ReadyBoth()), "all slots throttled after failure");
        Equal(0, engine.Observe(12000, ReadyBoth()), "retry boundary");
        Equal(0, engine.Confirmed, "false dispatch never success");
    }

    private static void FailedDispatchCannotConfirm()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyLower());
        Equal(0, engine.Observe(2000, ReadyLower()), "decision");
        engine.RecordClick(2000, 0, false);
        engine.Observe(4000, CoolingLower());
        engine.Observe(4100, CoolingLower());
        Equal(0, engine.Confirmed, "cooling cannot confirm an undispatched click");
        Equal(-1, engine.PendingSlot, "undispatched attempt never became pending");
    }

    private static void ThreeDispatchFailuresFault()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyBoth());
        for (int i = 0; i < 3; i++)
        {
            long now = 2000 + i * 10000;
            Equal(0, engine.Observe(now, ReadyBoth()), "failed attempt decision");
            engine.RecordClick(now, 0, false);
        }
        Equal(3, engine.Attempts, "three attempts recorded");
        Equal(true, engine.Faulted, "third failed dispatch faults");
        Equal(-1, engine.Observe(100000, Snapshot(ChestSlotState.Cooling, 1, ChestSlotState.Ready, 0)), "upper also locked");
        Equal(-1, engine.Observe(110000, ReadyBoth()), "fault is sticky");
        Equal(0, engine.Confirmed, "failed attempts not counted");
    }

    private static ChestEngine ThirdActualClick()
    {
        var engine = ClickedLower();
        Equal(0, engine.Observe(12000, ReadyBoth()), "retry second");
        engine.RecordClick(12000, 0, true);
        Equal(0, engine.Observe(22000, ReadyBoth()), "retry third");
        engine.RecordClick(22000, 0, true);
        return engine;
    }

    private static void ThirdClickCanConfirm()
    {
        var engine = ThirdActualClick();
        Equal(false, engine.Faulted, "third dispatched attempt has confirmation chance");
        engine.Observe(24000, CoolingLower());
        engine.Observe(24100, CoolingLower());
        Equal(1, engine.Confirmed, "third actual attempt can succeed");
        Equal(false, engine.Faulted, "success clears failure streak");
    }

    private static void ThirdTimeoutFaults()
    {
        var engine = ThirdActualClick();
        Equal(-1, engine.Observe(32000, ReadyBoth()), "no fourth click");
        Equal(true, engine.Faulted, "three unconfirmed attempts fault");
        Equal(-1, engine.PendingSlot, "expired pending cleared");
        Equal(-1, engine.Observe(100000, ReadyBoth()), "fault remains");
        Equal(3, engine.Attempts, "exactly three actual attempts");
    }

    private static void ConfirmationResetsFailures()
    {
        var engine = ThirdActualClick();
        engine.Observe(24000, CoolingLower());
        engine.Observe(24100, CoolingLower());
        engine.Observe(25000, ReadyLower());
        Equal(0, engine.Observe(27000, ReadyLower()), "new round ready");
        engine.RecordClick(27000, 0, false);
        Equal(false, engine.Faulted, "one new failure after success is not fourth consecutive failure");
        Equal(4, engine.Attempts, "session attempts retained across success");
        Equal(1, engine.Confirmed, "confirmed total retained");
    }

    private static void OtherSlotSuccessPreservesFailures()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyBoth());
        Equal(0, engine.Observe(2000, ReadyBoth()), "first lower attempt");
        engine.RecordClick(2000, 0, false);
        Equal(0, engine.Observe(12000, ReadyBoth()), "second lower attempt");
        engine.RecordClick(12000, 0, false);
        var upperReady = Snapshot(ChestSlotState.Unknown, 0, ChestSlotState.Ready, 0);
        Equal(1, engine.Observe(22000, upperReady), "upper can be independently ready");
        engine.RecordClick(22000, 1, true);
        var upperCooling = Snapshot(ChestSlotState.Unknown, 0, ChestSlotState.Cooling, 1799);
        engine.Observe(24000, upperCooling);
        engine.Observe(24100, upperCooling);
        Equal(1, engine.Confirmed, "upper success");
        engine.Observe(25000, ReadyLower());
        Equal(0, engine.Observe(27000, ReadyLower()), "third lower attempt");
        engine.RecordClick(27000, 0, false);
        Equal(true, engine.Faulted, "upper success did not reset lower failure streak");
        Equal(4, engine.Attempts, "three lower attempts plus one upper");
    }

    private static void SuccessAnimationPause()
    {
        var engine = ClickedLower();
        engine.Observe(4000, CoolingLower());
        engine.Observe(4100, CoolingLower());
        Equal(-1, engine.Observe(6099, CoolingLower()), "pause before next box");
        Equal(1, engine.Observe(6100, CoolingLower()), "upper allowed after two seconds");
        engine.RecordClick(6100, 1, true);
        Equal(1, engine.PendingSlot, "upper independently pending");
        var bothCooling = Snapshot(ChestSlotState.Cooling, 1790, ChestSlotState.Cooling, 1799);
        engine.Observe(8100, bothCooling);
        engine.Observe(8200, bothCooling);
        Equal(2, engine.Confirmed, "each box confirms independently");
    }

    private static void DuplicateRecordDoesNotCount()
    {
        var engine = ClickedLower();
        engine.RecordClick(2000, 0, true);
        Equal(1, engine.Attempts, "duplicate record ignored");
        Equal(0, engine.PendingSlot, "pending not replaced");
    }

    private static void WrongRecordDoesNotCount()
    {
        var engine = new ChestEngine();
        engine.RecordClick(0, 0, true);
        Equal(0, engine.Attempts, "no decision no attempt");
        engine.Observe(0, ReadyBoth());
        Equal(0, engine.Observe(2000, ReadyBoth()), "lower decision");
        engine.RecordClick(2000, 1, true);
        Equal(0, engine.Attempts, "wrong slot no attempt");
        Equal(-1, engine.PendingSlot, "wrong slot not pending");
        engine.RecordClick(2000, 0, true);
        Equal(1, engine.Attempts, "matching decision remains consumable");
    }

    private static void BackwardTimeCannotAct()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyLower());
        engine.Observe(1000, ReadyLower());
        Equal(-1, engine.Observe(-1, ReadyLower()), "backward time cannot click");
        engine = ClickedLower();
        engine.Observe(4000, CoolingLower());
        engine.Observe(3999, CoolingLower());
        Equal(0, engine.Confirmed, "backward observation not independent confirmation");
        Equal(0, engine.PendingSlot, "backward observation keeps pending");
    }

    private static void BackwardClickRecordDoesNotCount()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyLower());
        Equal(0, engine.Observe(2000, ReadyLower()), "decision");
        engine.RecordClick(1999, 0, true);
        Equal(0, engine.Attempts, "backdated record ignored");
        Equal(-1, engine.PendingSlot, "backdated record no pending");
        engine.RecordClick(2000, 0, true);
        Equal(1, engine.Attempts, "valid record can consume decision");
    }

    private static void ClickTimestampDoesNotConfirm()
    {
        var engine = ClickedLower();
        engine.Observe(2000, CoolingLower());
        engine.Observe(4000, CoolingLower());
        Equal(0, engine.Confirmed, "click timestamp cannot count as post-click sample");
        engine.Observe(4100, CoolingLower());
        Equal(1, engine.Confirmed, "two later samples confirm");
    }

    private static void ResetClearsSession()
    {
        var engine = ThirdActualClick();
        engine.Observe(32000, ReadyBoth());
        engine.Reset();
        Equal(false, engine.Faulted, "fault cleared");
        Equal(0, engine.Attempts, "attempts cleared");
        Equal(0, engine.Confirmed, "confirmed cleared");
        Equal(-1, engine.PendingSlot, "pending cleared");
        Equal(-1, engine.Observe(0, ReadyLower()), "new clock baseline allowed and readiness reset");
        Equal(0, engine.Observe(2000, ReadyLower()), "fresh stable window");
        engine.RecordClick(2000, 0, true);
        engine.Observe(4000, CoolingLower());
        engine.Reset();
        engine.Observe(5000, CoolingLower());
        Equal(0, engine.Confirmed, "reset canceled old confirmation");
    }

    private static void ResetCancelsDecision()
    {
        var engine = new ChestEngine();
        engine.Observe(0, ReadyLower());
        Equal(0, engine.Observe(2000, ReadyLower()), "old decision");
        engine.Reset();
        engine.RecordClick(2000, 0, true);
        Equal(0, engine.Attempts, "old decision revoked");
        Equal(-1, engine.PendingSlot, "old click cannot become pending");
    }

    private static void ExtremeTimestampBoundaries()
    {
        var engine = new ChestEngine();
        engine.Observe(long.MaxValue - 1999, ReadyLower());
        Equal(-1, engine.Observe(long.MaxValue, ReadyLower()), "no overflow past final millisecond");
        engine.Reset();
        engine.Observe(long.MinValue, ReadyLower());
        Equal(0, engine.Observe(long.MaxValue, ReadyLower()), "huge ascending interval remains elapsed");
        engine.RecordClick(long.MaxValue, 0, true);
        Equal(-1, engine.Observe(long.MinValue, ReadyLower()), "overflow cannot turn backwards time into retry");
        Equal(1, engine.Attempts, "backward time no extra attempts");
    }

    private static void InsufficientClickBalanceWaits()
    {
        foreach (long clicks in new long[] { -1, 0, 999 })
        {
            var engine = new ChestEngine();
            engine.Observe(0, WithClicks(ReadyBoth(), clicks));
            Equal(-1, engine.Observe(2000, WithClicks(ReadyBoth(), clicks)), "stable ready still requires 1000 clicks");
            Equal(-1, engine.Observe(12000, WithClicks(ReadyBoth(), clicks)), "first retry interval remains a wait");
            Equal(-1, engine.Observe(22000, WithClicks(ReadyBoth(), clicks)), "second retry interval remains a wait");
            Equal(0, engine.Attempts, "waiting never counts an attempt");
            Equal(0, engine.Confirmed, "waiting never counts success");
            Equal(false, engine.Faulted, "waiting never consumes failure budget");
            Equal(-1, engine.PendingSlot, "waiting never creates pending click");
            if (engine.Message.IndexOf("次数", StringComparison.Ordinal) < 0 ||
                engine.Message.IndexOf("1000", StringComparison.Ordinal) < 0)
                throw new Exception("Missing status explaining the 1000-click requirement");
        }
    }

    private static void ThresholdPreservesStableReady()
    {
        var engine = new ChestEngine();
        engine.Observe(0, WithClicks(ReadyBoth(), 999));
        Equal(-1, engine.Observe(2000, WithClicks(ReadyBoth(), 999)), "stable but one click short");
        Equal(0, engine.Observe(2001, WithClicks(ReadyBoth(), 1000)), "already stable button allowed when threshold reached");
        engine.RecordClick(2001, 0, true);
        Equal(1, engine.Attempts, "only the actual threshold-qualified attempt counts");
        Equal(-1, engine.Observe(2100, WithClicks(ReadyBoth(), 1000)), "one box at a time even if both ready");
        Equal(0, engine.PendingSlot, "first box remains pending");
    }

    private static void LowBalanceAllowsPendingConfirmation()
    {
        var engine = new ChestEngine();
        engine.Observe(0, WithClicks(ReadyBoth(), 1000));
        Equal(0, engine.Observe(2000, WithClicks(ReadyBoth(), 1000)), "sufficient balance permits lower click");
        engine.RecordClick(2000, 0, true);
        Equal(-1, engine.Observe(4000, WithClicks(CoolingLower(), 0)), "lower pending cannot click upper");
        Equal(0, engine.PendingSlot, "first cooling observation retained with depleted balance");
        engine.Observe(4100, WithClicks(CoolingLower(), 0));
        Equal(1, engine.Confirmed, "second cooling confirms despite spent balance");
        Equal(-1, engine.PendingSlot, "successful pending released");
        Equal(-1, engine.Observe(6100, WithClicks(CoolingLower(), 0)), "upper waits for its own 1000-click funding");
        Equal(1, engine.Attempts, "depleted balance causes no second attempt");
        Equal(1, engine.Observe(6101, WithClicks(CoolingLower(), 1000)), "upper allowed after replenishment");
    }

    private static void LowBalancePreservesCoolingState()
    {
        var engine = new ChestEngine();
        var cooling = Snapshot(ChestSlotState.Cooling, 60, ChestSlotState.Unknown, 0);
        engine.Observe(0, WithClicks(cooling, 999));
        Equal(-1, engine.Observe(60000, WithClicks(cooling, 999)), "cooling never becomes a blind click");
        Equal(-1, engine.Observe(62000, WithClicks(ReadyLower(), 999)), "new ready window starts normally");
        Equal(-1, engine.Observe(63999, WithClicks(ReadyLower(), 1000)), "funding does not skip two-second readiness");
        Equal(-1, engine.Observe(64000, WithClicks(ReadyLower(), 999)), "stable readiness still respects available clicks");
        Equal(0, engine.Observe(64001, WithClicks(ReadyLower(), 1000)), "cooling and ready transitions preserved while waiting");
    }

    private static void LowBalancePreservesRetryBudget()
    {
        var engine = new ChestEngine();
        engine.Observe(0, WithClicks(ReadyBoth(), 1000));
        Equal(0, engine.Observe(2000, WithClicks(ReadyBoth(), 1000)), "first funded attempt");
        engine.RecordClick(2000, 0, false);
        Equal(0, engine.Observe(12000, WithClicks(ReadyBoth(), 1000)), "second funded attempt");
        engine.RecordClick(12000, 0, false);
        Equal(-1, engine.Observe(22000, WithClicks(ReadyBoth(), 999)), "low balance prevents third attempt");
        Equal(-1, engine.Observe(100000, WithClicks(ReadyBoth(), 999)), "waiting does not retry");
        Equal(2, engine.Attempts, "only prior attempts retained");
        Equal(false, engine.Faulted, "two failures plus long waiting never fault");
        Equal(0, engine.Observe(100001, WithClicks(ReadyBoth(), 1000)), "funding permits actual third attempt");
        engine.RecordClick(100001, 0, false);
        Equal(3, engine.Attempts, "third actual attempt counts");
        Equal(true, engine.Faulted, "waiting did not erase actual prior failures either");
    }

    private static void UnknownBalanceKeepsLegacyPolicy()
    {
        var engine = new ChestEngine();
        engine.Observe(0, WithClicks(ReadyLower(), 999));
        Equal(-1, engine.Observe(2000, WithClicks(ReadyLower(), 999)), "known balance shortage blocks");
        Equal(0, engine.Observe(2001, ReadyLower()), "default unknown balance retains existing ready policy");
        engine.RecordClick(2001, 0, true);
        engine.Observe(4001, CoolingLower());
        engine.Observe(4101, CoolingLower());
        Equal(1, engine.Confirmed, "unknown balance preserves confirmation behavior");
    }

    private static void FreshLowBalanceCancelsDecision()
    {
        var engine = new ChestEngine();
        engine.Observe(0, WithClicks(ReadyLower(), 1000));
        Equal(0, engine.Observe(2000, WithClicks(ReadyLower(), 1000)), "initial funded decision");
        Equal(-1, engine.Observe(2001, WithClicks(ReadyLower(), 999)), "fresh low balance cancels pending dispatch decision");
        engine.RecordClick(2001, 0, false);
        Equal(0, engine.Attempts, "canceled unfunded decision is not an attempted click");
        Equal(false, engine.Faulted, "canceled unfunded decision is not a failure");
        Equal(-1, engine.PendingSlot, "unfunded decision never dispatched");
        Equal(0, engine.Observe(2002, WithClicks(ReadyLower(), 1000)), "replenishment allows a fresh decision");
    }
}
