using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using TappoIdle;

// Entry point: /main:UiIntegrationTests
// Sources: src/Program.cs src/IdleEngine.cs src/LoopGuard.cs
//          tests/UiIntegrationStubs.cs tests/UiIntegrationTests.cs
// References: System.Windows.Forms.dll System.Drawing.dll
// The real NativeInput and ChestService must not be included in this executable.
internal static class UiIntegrationTests
{
    private static int checks;
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(object owner, string name)
    {
        return (T)owner.GetType().GetField(name, Flags).GetValue(owner);
    }

    private static void Set(object owner, string name, object value)
    {
        owner.GetType().GetField(name, Flags).SetValue(owner, value);
    }

    private static void Call(object owner, string name, params object[] args)
    {
        owner.GetType().GetMethod(name, Flags).Invoke(owner, args);
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        checks++;
    }

    private static long Now(MainForm form)
    {
        return Field<Stopwatch>(form, "clock").ElapsedMilliseconds;
    }

    private static MainForm New(bool auto, bool only)
    {
        NativeInput.Foreground = NativeInput.Pet = NativeInput.SendSuccess = true;
        NativeInput.DuringSend = null;
        // Create a handle without showing the form or invoking OnShown.
        MainForm form = new MainForm(false);
        IntPtr handle = form.Handle;
        Set(form, "ready", true);
        Field<CheckBox>(form, "autoChest").Checked = auto;
        Field<CheckBox>(form, "chestOnly").Checked = only;
        Field<NumericUpDown>(form, "target").Value = 1;
        return form;
    }

    private static void Start(MainForm form) { Call(form, "StartRun"); }

    private static void Expire(MainForm form)
    {
        long now = Now(form);
        long duration = Field<long>(form, "idleDurationMs");
        Set(form, "idleSince", now - duration);
        Field<IdleEngine>(form, "engine").Start(now - duration, now - duration, 2000, 1, (int)(duration / 1000));
        Field<LoopGuard>(form, "loopGuard").Reset(now);
        Call(form, "TickRun");
    }

    private static ChestService Current(MainForm form) { return Field<ChestService>(form, "chestService"); }
    private static void PureRun(MainForm form) { Start(form); Expire(form); }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using (MainForm smoke = new MainForm(true))
            {
                Check(!Field<CheckBox>(smoke, "autoChest").Checked && !Field<CheckBox>(smoke, "chestOnly").Checked, "default opt-outs");
                Check(!Field<CheckBox>(smoke, "chestOnly").Enabled, "only mode gated");
                Call(smoke, "StartRun");
                Check(NativeInput.Installs == 0 && NativeInput.Sends == 0 && ChestService.Created.Count == 0, "smoke never activates integrations");
            }
            using (MainForm form = New(true, true))
            {
                int sends = NativeInput.Sends;
                int created = ChestService.Created.Count;
                Start(form);
                Call(form, "TickRun");
                Check(NativeInput.Sends == sends && ChestService.Created.Count == created, "only waits before service");
                Call(form, "PhysicalInput");
                Check(Field<bool>(form, "sessionActive") && Field<long>(form, "idleRemainingMs") == 10000, "physical input resets idle");
                Expire(form);
                ChestService service = Current(form);
                Check(service != null && service.Starts == 1 && NativeInput.Sends == sends && Field<IdleEngine>(form, "engine").Sent == 0, "only starts once and never increments sends");
                Check(Field<bool>(form, "sessionMonitoring") && !Field<CheckBox>(form, "autoChest").Enabled && !Field<CheckBox>(form, "chestOnly").Enabled, "monitoring locks settings");
                string old = Field<Label>(form, "chestState").Text;
                Thread producer = new Thread(delegate() { service.Emit("左 12:34 / 右 23:45", false, 2, 3); });
                producer.Start();
                producer.Join();
                Check(Field<Label>(form, "chestState").Text == old, "MTA event uses queued UI update");
                Application.DoEvents();
                Check(Field<Label>(form, "chestState").Text.Contains("12:34") && Field<Label>(form, "chestState").Text.Contains("2 次"), "countdowns and confirmation count displayed");
                Call(form, "PhysicalInput");
                Check(!Field<bool>(form, "sessionActive") && service.Stops == 1 && service.Disposes == 1, "only real input cancels immediately");
                Check(Field<Label>(form, "count").Text.Contains("0 次"), "only count remains zero");
            }
            using (MainForm form = New(true, false))
            {
                int sends = NativeInput.Sends;
                Start(form);
                Expire(form);
                ChestService first = Current(form);
                Check(NativeInput.Sends == sends + 1 && Field<IdleEngine>(form, "engine").State == RunState.Completed && Field<bool>(form, "sessionActive"), "target completion retains monitoring session");
                Call(form, "TickRun");
                Check(NativeInput.Sends == sends + 1 && first.Starts == 1, "no sends or repeated starts in monitor stage");
                first.Emit("old fault", true, 0, 1);
                Call(form, "PhysicalInput");
                Check(Field<Label>(form, "detail").Text.Contains("真实"), "completed engine still accepts stop reason");
                Start(form);
                Expire(form);
                ChestService second = Current(form);
                Application.DoEvents();
                Check(Field<bool>(form, "sessionActive") && Current(form) == second && first != second, "queued old Faulted cannot cancel new run");
                second.Emit("current fault", true, 0, 3);
                Application.DoEvents();
                Check(!Field<bool>(form, "sessionActive") && second.Stops == 1 && Field<Label>(form, "detail").Text.Contains("current fault"), "current Faulted cancels with reason");
            }
            using (MainForm form = New(false, false))
            {
                int created = ChestService.Created.Count;
                Start(form);
                Expire(form);
                Check(!Field<bool>(form, "sessionActive") && Field<IdleEngine>(form, "engine").Sent == 1 && ChestService.Created.Count == created, "ordinary completion stops without service");
            }
            using (MainForm form = New(true, true))
            {
                PureRun(form);
                ChestService service = Current(form);
                NativeInput.Foreground = false;
                Call(form, "TickRun");
                Check(!Field<bool>(form, "sessionActive") && service.Stops == 1, "foreground guard covers monitoring");
            }
            using (MainForm form = New(true, true))
            {
                PureRun(form);
                ChestService service = Current(form);
                NativeInput.Pet = false;
                Call(form, "TickRun");
                Check(!Field<bool>(form, "sessionActive") && service.Stops == 1, "pet exit guard covers monitoring");
            }
            using (MainForm form = New(true, true))
            {
                PureRun(form);
                ChestService service = Current(form);
                Field<LoopGuard>(form, "loopGuard").Reset(Now(form) - 1000);
                Call(form, "TickRun");
                Check(!Field<bool>(form, "sessionActive") && service.Stops == 1, "stall guard covers monitoring");
            }
            using (MainForm form = New(true, true))
            {
                PureRun(form);
                ChestService service = Current(form);
                Call(form, "QueueStop", "session lock");
                Application.DoEvents();
                Check(!Field<bool>(form, "sessionActive") && service.Stops == 1, "queued session/power cancellation covers monitoring");
            }
            using (MainForm form = New(true, false))
            {
                Start(form);
                int created = ChestService.Created.Count;
                NativeInput.SendSuccess = false;
                Expire(form);
                Check(!Field<bool>(form, "sessionActive") && Current(form) == null && ChestService.Created.Count == created, "failed first send never starts chests");
            }
            using (MainForm form = New(true, false))
            {
                Start(form);
                int created = ChestService.Created.Count;
                NativeInput.DuringSend = delegate { Call(form, "PhysicalInput"); };
                Expire(form);
                Check(!Field<bool>(form, "sessionActive") && ChestService.Created.Count == created, "real input during first send prevents service start");
            }
            using (MainForm form = New(true, false))
            {
                Field<NumericUpDown>(form, "target").Value = 10;
                Start(form);
                Expire(form);
                ChestService service = Current(form);
                int sends = NativeInput.Sends;
                service.IsExpectedFocusTransition = true;
                NativeInput.Foreground = false;
                Call(form, "TickRun");
                Check(Field<bool>(form, "sessionActive") && NativeInput.Sends == sends, "expected pet activation pauses keyboard without ending session");
                Check(service.Heartbeats > 0, "expected activation keeps healthy UI heartbeats");
                NativeInput.Foreground = true;
                Call(form, "OnDeactivate", EventArgs.Empty);
                Check(Field<bool>(form, "sessionActive"), "stale deactivation after restored foreground is ignored");
                Call(form, "PhysicalInput");
                Check(!Field<bool>(form, "sessionActive") && service.Stops == 1, "physical input always stops during an expected focus transition");
            }
            MainForm disposed = New(true, true);
            PureRun(disposed);
            ChestService last = Current(disposed);
            disposed.Dispose();
            Check(last.Stops == 1 && last.Disposes == 1, "disposing cancels service");
            Console.WriteLine("PASS: " + checks + " UI integration assertions; all hooks, input and chest I/O stubbed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return 1;
        }
    }
}
