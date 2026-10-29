using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TappoIdle
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool smoke = args.Length > 0 && args[0] == "--smoke-test";
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                NativeInput.VerifyInputLayout();
                if (smoke)
                {
                    // Construct and render the real UI with hooks, timers and input disabled.
                    using (MainForm form = new MainForm(true))
                    {
                        // Invisible parents do not create/render their child handles.
                        // Show off-screen without activation; smoke mode never installs hooks.
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-32000, -32000);
                        form.ShowInTaskbar = false;
                        form.Show();
                        Application.DoEvents();
                        form.PerformLayout();
                        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                            if (args.Length > 1) bitmap.Save(Path.GetFullPath(args[1]));
                        }
                    }
                    return 0;
                }
                bool ownsMutex;
                using (Mutex mutex = new Mutex(true, "Local\\TappoIdleHelper.v1", out ownsMutex))
                {
                    if (!ownsMutex)
                    {
                        MessageBox.Show("助手已经打开，请使用已有窗口。", "闲置按键助手");
                        return 0;
                    }
                    try { Application.Run(new MainForm(false)); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (!smoke) MessageBox.Show(ex.Message, "助手已停止", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else if (args.Length > 1) File.WriteAllText(args[1] + ".error.txt", ex.ToString());
                return 1;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly IdleEngine engine = new IdleEngine();
        private readonly LoopGuard loopGuard = new LoopGuard();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly NativeInput native = new NativeInput();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NumericUpDown interval = Number(50, 10000, 2000, 50);
        private readonly NumericUpDown target = Number(1, 100000, 1000, 100);
        private readonly NumericUpDown idle = Number(5, 600, 10, 5);
        private readonly ComboBox key = new ComboBox();
        private readonly ComboBox presets = new ComboBox();
        private readonly CheckBox autoChest = new CheckBox();
        private readonly CheckBox chestOnly = new CheckBox();
        private readonly Label estimate = TextLabel("", 10, FontStyle.Regular);
        private readonly Label state = TextLabel("尚未启动", 17, FontStyle.Bold);
        private readonly Label count = TextLabel("本次已发送 0 次", 11, FontStyle.Regular);
        private readonly Label detail = TextLabel("请先打开官方桌宠，再点开始。", 10, FontStyle.Regular);
        private readonly Label chestState = TextLabel("自动开箱：未启用", 10, FontStyle.Regular);
        private readonly Label targetHint = TextLabel("次，达标停止", 10, FontStyle.Regular);
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly bool smoke;
        private bool ready;
        private bool closing;
        private bool sessionActive;
        private bool sessionStarted;
        private bool sessionMonitoring;
        private bool hasRun;
        private bool runAutoChest;
        private bool runChestOnly;
        private bool chestStarted;
        private long idleSince;
        private long idleDurationMs;
        private long idleRemainingMs;
        private long runGeneration;
        private string stopReason;
        private string chestMessage;
        private int chestConfirmed;
        private int chestAttempts;
        private ChestService chestService;
        private Action<ChestStatus> chestStatusHandler;
        private ushort chosenKey;
        private readonly int[] presetValues = { 2000, 1000, 500, 200, 100, 50 };

        public MainForm(bool smokeMode)
        {
            smoke = smokeMode;
            Text = "鸣潮桌宠 · 闲置按键助手";
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(660, 820);
            MinimumSize = new Size(660, 580);
            BackColor = Color.FromArgb(246, 248, 250);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            Panel viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Add(viewport);
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Padding = new Padding(26, 22, 26, 20);
            root.ColumnCount = 1;
            root.RowCount = 9;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            viewport.Controls.Add(root);

            Label heading = TextLabel("闲置时补足次数", 23, FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(heading, 0, 0);
            Label intro = TextLabel("速度由你选择 · 回来操作即停止", 11, FontStyle.Regular);
            intro.ForeColor = Color.FromArgb(80, 94, 110);
            intro.Margin = new Padding(0, 0, 0, 18);
            root.Controls.Add(intro, 0, 1);

            Label risk = TextLabel("自动操作仍可能违反官方规则，不能保证不受处罚。\n已发送次数不等于桌宠实际入账；开箱仍受原有冷却限制。", 10, FontStyle.Regular);
            risk.BackColor = Color.FromArgb(255, 244, 218);
            risk.ForeColor = Color.FromArgb(114, 77, 16);
            risk.Padding = new Padding(12, 10, 12, 10);
            risk.Dock = DockStyle.Fill;
            risk.Margin = new Padding(0, 0, 0, 18);
            root.Controls.Add(risk, 0, 2);

            TableLayoutPanel settings = new TableLayoutPanel();
            settings.AutoSize = true;
            settings.Dock = DockStyle.Top;
            settings.ColumnCount = 3;
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settings.Margin = new Padding(0);
            presets.DropDownStyle = ComboBoxStyle.DropDownList;
            presets.Items.AddRange(new object[] { "2 秒 / 次", "1 秒 / 次", "0.5 秒 / 次", "每秒 5 次", "每秒 10 次", "每秒 20 次" });
            presets.Width = 204;
            presets.SelectedIndex = 0;
            AddSetting(settings, 0, "按键间隔（毫秒）", interval, presets);
            AddSetting(settings, 1, "本次发送目标", target, targetHint);
            AddSetting(settings, 2, "启动前连续闲置", idle, TextLabel("秒，操作会重新计时", 10, FontStyle.Regular));
            key.DropDownStyle = ComboBoxStyle.DropDownList;
            key.Items.AddRange(new object[] { "F24", "F23", "F22", "F21" });
            key.SelectedIndex = 0;
            key.Width = 134;
            AddSetting(settings, 3, "模拟按键", key, TextLabel("特殊功能键，无文字输入", 10, FontStyle.Regular));
            FlowLayoutPanel chestOptions = new FlowLayoutPanel();
            chestOptions.AutoSize = true;
            chestOptions.Dock = DockStyle.Top;
            chestOptions.Margin = new Padding(0, 6, 0, 0);
            autoChest.Text = "自动开箱";
            autoChest.AutoSize = true;
            autoChest.Margin = new Padding(0, 4, 20, 4);
            chestOnly.Text = "只自动开箱，不发送按键";
            chestOnly.AutoSize = true;
            chestOnly.Enabled = false;
            chestOnly.Margin = new Padding(0, 4, 0, 4);
            chestOptions.Controls.Add(autoChest);
            chestOptions.Controls.Add(chestOnly);
            settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settings.Controls.Add(chestOptions, 0, 4);
            settings.SetColumnSpan(chestOptions, 3);
            root.Controls.Add(settings, 0, 3);

            estimate.ForeColor = Color.FromArgb(30, 102, 107);
            estimate.MaximumSize = new Size(580, 0);
            estimate.Margin = new Padding(0, 12, 0, 16);
            root.Controls.Add(estimate, 0, 4);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Top;
            buttons.Margin = new Padding(0, 0, 0, 20);
            start.Text = "开始等待闲置";
            start.Size = new Size(218, 46);
            start.BackColor = Color.FromArgb(27, 110, 103);
            start.ForeColor = Color.White;
            start.FlatStyle = FlatStyle.Flat;
            start.FlatAppearance.BorderSize = 0;
            start.Enabled = false;
            stop.Text = "停止  /  Esc";
            stop.Size = new Size(168, 46);
            stop.Margin = new Padding(12, 3, 3, 3);
            stop.Enabled = false;
            buttons.Controls.Add(start);
            buttons.Controls.Add(stop);
            root.Controls.Add(buttons, 0, 5);

            TableLayoutPanel statusPanel = new TableLayoutPanel();
            statusPanel.AutoSize = true;
            statusPanel.Dock = DockStyle.Top;
            statusPanel.Padding = new Padding(16);
            statusPanel.BackColor = Color.White;
            statusPanel.ColumnCount = 1;
            statusPanel.RowCount = 5;
            for (int row = 0; row < 5; row++) statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            state.Margin = new Padding(0, 0, 0, 10);
            count.Margin = new Padding(0, 0, 0, 12);
            progress.Dock = DockStyle.Top;
            progress.Height = 12;
            progress.Margin = new Padding(0, 0, 0, 12);
            detail.MaximumSize = new Size(540, 0);
            chestState.MaximumSize = new Size(540, 0);
            chestState.ForeColor = Color.FromArgb(30, 102, 107);
            chestState.Margin = new Padding(0, 10, 0, 0);
            statusPanel.Controls.Add(state, 0, 0);
            statusPanel.Controls.Add(count, 0, 1);
            statusPanel.Controls.Add(progress, 0, 2);
            statusPanel.Controls.Add(detail, 0, 3);
            statusPanel.Controls.Add(chestState, 0, 4);
            root.Controls.Add(statusPanel, 0, 6);

            Label footer = TextLabel("运行期间保持本窗口在前台。移动鼠标或敲键盘即停止。\n全局急停：Ctrl + Alt + F10；锁屏、休眠或切换窗口也会停止。\n确认领取仅表示界面恢复冷却，请在桌宠中核对次数和奖励。", 9, FontStyle.Regular);
            footer.ForeColor = Color.FromArgb(95, 105, 117);
            footer.Margin = new Padding(0, 18, 0, 0);
            root.Controls.Add(footer, 0, 8);

            presets.SelectedIndexChanged += delegate { if (presets.SelectedIndex >= 0) interval.Value = presetValues[presets.SelectedIndex]; };
            interval.ValueChanged += delegate { UpdateEstimate(); };
            target.ValueChanged += delegate { UpdateEstimate(); };
            idle.ValueChanged += delegate { UpdateEstimate(); };
            autoChest.CheckedChanged += delegate
            {
                if (!autoChest.Checked) chestOnly.Checked = false;
                UpdateEstimate();
                RenderState();
            };
            chestOnly.CheckedChanged += delegate { UpdateEstimate(); RenderState(); };
            start.Click += delegate { StartRun(); };
            stop.Click += delegate { StopRun("已手动停止"); };
            native.PhysicalInput += PhysicalInput;
            timer.Interval = 20;
            timer.Tick += delegate { TickRun(); };
            Deactivate += delegate
            {
                bool expected = chestService != null && chestService.IsFocusTransitionInProgress &&
                    (chestService.IsExpectedFocusTransition || NativeInput.ForegroundIsTransitioning);
                if (!NativeInput.IsForeground(Handle) && !expected) StopRun("窗口已离开前台");
            };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { StopRun("已按 Esc 停止"); e.Handled = e.SuppressKeyPress = true; }
            };
            FormClosing += delegate { closing = true; StopRun("已关闭"); timer.Stop(); native.Dispose(); };
            UpdateEstimate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (smoke) return;
            try
            {
                native.Install(Handle);
                SystemEvents.SessionSwitch += SessionChanged;
                SystemEvents.PowerModeChanged += PowerChanged;
                ready = true;
                start.Enabled = true;
                timer.Start();
            }
            catch (Exception ex)
            {
                native.Dispose();
                state.Text = "保护功能未就绪";
                detail.Text = ex.Message;
            }
        }

        protected override bool ShowWithoutActivation { get { return smoke; } }

        private void StartRun()
        {
            if (!ready || closing || smoke || sessionActive) return;
            if (!NativeInput.IsPetRunning())
            {
                MessageBox.Show(this, "未检测到 WuwaTappo.exe。请先打开官方桌宠，再点开始。", "请先打开桌宠", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // Windows may silently remove a low-level hook after a stalled UI thread.
            // Every explicit run establishes fresh hooks; a delayed loop fails closed.
            try
            {
                native.Dispose();
                native.Install(Handle);
            }
            catch (Exception ex)
            {
                ready = false;
                start.Enabled = false;
                timer.Stop();
                state.Text = "保护功能未就绪";
                detail.Text = ex.Message + " 请关闭助手后重试。";
                return;
            }
            chosenKey = (ushort)(0x87 - key.SelectedIndex);
            long now = clock.ElapsedMilliseconds;
            loopGuard.Reset(now);
            runGeneration++;
            hasRun = sessionActive = true;
            sessionStarted = sessionMonitoring = chestStarted = false;
            runAutoChest = autoChest.Checked;
            runChestOnly = runAutoChest && chestOnly.Checked;
            idleSince = now;
            idleDurationMs = (int)idle.Value * 1000L;
            idleRemainingMs = idleDurationMs;
            stopReason = null;
            chestMessage = "尚未开始监测";
            chestConfirmed = chestAttempts = 0;
            engine.Start(now, now, (int)interval.Value, (int)target.Value, (int)idle.Value);
            RenderState();
        }

        private void TickRun()
        {
            try
            {
                if (sessionActive)
                {
                    if (!loopGuard.Check(clock.ElapsedMilliseconds))
                    {
                        StopRun("检测到程序卡顿，输入监听可能失效，已停止");
                        return;
                    }
                    bool foreground = NativeInput.IsForeground(Handle);
                    bool petRunning = NativeInput.IsPetRunning();
                    long now = clock.ElapsedMilliseconds;
                    if (!loopGuard.Check(now))
                    {
                        StopRun("检测到程序卡顿，输入监听可能失效，已停止");
                        return;
                    }
                    if (chestService != null) chestService.Heartbeat();
                    if (chestService != null && chestService.IsActionTimedOut)
                    {
                        StopRun("标准按钮调用超过时限，自动开箱保护已停止");
                        return;
                    }
                    if (chestService != null && chestService.IsFocusTransitionInProgress &&
                        (foreground || chestService.IsExpectedFocusTransition || NativeInput.ForegroundIsTransitioning))
                    {
                        RenderState();
                        return;
                    }
                    // These protections also apply after the key target is complete.
                    if (!foreground)
                    {
                        if (chestService != null && chestService.IsExpectedFocusTransition)
                        {
                            // The client may activate itself during a standard button Invoke.
                            // No key input is emitted while it temporarily owns the foreground.
                            RenderState();
                            return;
                        }
                        StopRun("助手已失去前台，已停止");
                        return;
                    }
                    if (!petRunning)
                    {
                        StopRun("未检测到鸣潮桌宠运行，已停止");
                        return;
                    }
                    if (!sessionStarted)
                    {
                        idleRemainingMs = Math.Max(0L, idleDurationMs - Math.Max(0L, now - idleSince));
                        if (idleRemainingMs > 0)
                        {
                            RenderState();
                            return;
                        }
                        if (runChestOnly)
                        {
                            sessionStarted = sessionMonitoring = true;
                            StartChestMonitoring();
                        }
                    }
                    if (!runChestOnly && !sessionMonitoring)
                    {
                        int sentBefore = engine.Sent;
                        engine.Tick(now, foreground, petRunning, delegate { return NativeInput.SendKeyPair(chosenKey, Handle); });
                        // A real-input callback can stop the session during SendInput.
                        if (!sessionActive) return;
                        if (engine.State == RunState.Stopped)
                        {
                            StopRun(engine.Reason);
                            return;
                        }
                        if (engine.Sent > sentBefore && !sessionStarted)
                        {
                            sessionStarted = true;
                            if (runAutoChest) StartChestMonitoring();
                        }
                        if (engine.State == RunState.Completed)
                        {
                            if (runAutoChest) sessionMonitoring = true;
                            else sessionActive = false;
                        }
                    }
                }
                RenderState();
            }
            catch (Exception ex)
            {
                StopRun("运行异常，已停止：" + ex.Message);
            }
        }

        private void PhysicalInput()
        {
            if (!sessionActive) return;
            long now = clock.ElapsedMilliseconds;
            bool waiting = runChestOnly ? !sessionStarted : engine.State == RunState.Waiting;
            if (waiting)
            {
                idleSince = now;
                idleRemainingMs = idleDurationMs;
                engine.PhysicalInput(now);
            }
            else
            {
                StopRun("检测到真实键盘或鼠标输入，已停止");
            }
        }

        private void StartChestMonitoring()
        {
            if (!sessionActive || !runAutoChest || chestStarted || smoke) return;
            ChestService source = new ChestService();
            long generation = runGeneration;
            chestService = source;
            chestStarted = true;
            chestMessage = "正在读取两只宝箱状态…";
            chestStatusHandler = delegate(ChestStatus status)
            {
                if (status == null || closing || !IsHandleCreated) return;
                // Capture the status before returning to the MTA producer.
                string message = status.Message;
                int confirmed = status.Confirmed;
                int attempts = status.Attempts;
                bool faulted = status.Faulted;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        // Queued events from a canceled worker cannot affect a new run.
                        if (closing || !sessionActive || generation != runGeneration || !Object.ReferenceEquals(chestService, source)) return;
                        chestMessage = String.IsNullOrEmpty(message) ? "正在等待宝箱界面" : message;
                        chestConfirmed = confirmed;
                        chestAttempts = attempts;
                        if (faulted) StopRun("自动开箱停止：" + chestMessage);
                        else RenderChestState();
                    });
                }
                catch (InvalidOperationException) { }
            };
            source.StatusChanged += chestStatusHandler;
            source.Start(Handle);
        }

        private void CancelChestMonitoring()
        {
            ChestService source = chestService;
            chestService = null;
            if (source == null) return;
            source.StatusChanged -= chestStatusHandler;
            chestStatusHandler = null;
            // Stop revokes permission immediately; disposing must not wait for UIA.
            try { source.Stop(); }
            finally { source.Dispose(); }
        }

        private void StopRun(string reason)
        {
            bool wasActive = sessionActive;
            sessionActive = sessionMonitoring = sessionStarted = false;
            runGeneration++;
            if (wasActive)
            {
                stopReason = reason;
                engine.Stop(reason);
            }
            CancelChestMonitoring();
            if (wasActive && !closing) RenderState();
        }

        private void RenderState()
        {
            bool active = sessionActive;
            bool only = active ? runChestOnly : chestOnly.Checked;
            bool resultOnly = hasRun ? runChestOnly : only;
            start.Enabled = ready && !active && !closing && !smoke;
            stop.Enabled = active;
            idle.Enabled = autoChest.Enabled = !active;
            chestOnly.Enabled = !active && autoChest.Checked;
            interval.Enabled = target.Enabled = key.Enabled = presets.Enabled = !active && !only;
            targetHint.Text = autoChest.Checked ? "次，达标后继续监测宝箱" : "次，达标停止";
            count.Text = resultOnly ? "本次不发送按键 · 已发送 0 次" : String.Format("本次已发送 {0:N0} 次  /  {1:N0} 次", engine.Sent, active || engine.Target > 0 ? engine.Target : (int)target.Value);
            progress.Maximum = engine.Target > 0 ? engine.Target : (int)target.Value;
            progress.Value = Math.Min(engine.Sent, progress.Maximum);
            progress.Visible = !resultOnly;
            RenderChestState();
            if (!hasRun) return;
            if (active && !sessionStarted)
            {
                state.Text = String.Format("等待闲置 · 还需 {0} 秒", (idleRemainingMs + 999) / 1000);
                detail.Text = runChestOnly ? "保持本窗口在前台，倒计时结束后开始监测宝箱。" : "现在可以离开电脑。保持本窗口在前台，倒计时结束后开始发送。";
            }
            else if (active && sessionMonitoring)
            {
                state.Text = runChestOnly ? "正在监测宝箱" : "按键已达标 · 继续监测宝箱";
                detail.Text = "仅在界面明确就绪时领取。保持助手在前台，回来操作即停止。";
            }
            else if (active)
            {
                state.Text = "正在发送 · " + key.Text;
                detail.Text = "Windows 接收成功才计入发送数；桌宠可能忽略模拟输入。回来操作就会停止。";
            }
            else if (!String.IsNullOrEmpty(stopReason))
            {
                state.Text = "已停止";
                detail.Text = stopReason.TrimEnd('。') + "。继续使用需要手动点击开始。";
            }
            else if (engine.State == RunState.Completed)
            {
                state.Text = "目标已完成";
                detail.Text = "已停止发送。请到桌宠核对实际次数；开箱仍需满足原有冷却。";
            }
        }

        private void RenderChestState()
        {
            if (sessionActive && runAutoChest)
            {
                string message = sessionStarted ? chestMessage : String.Format("等待闲置 · 还需 {0} 秒", (idleRemainingMs + 999) / 1000);
                chestState.Text = String.Format("自动开箱：{0}\n本次确认领取 {1:N0} 次 · 尝试 {2:N0} 次", message, chestConfirmed, chestAttempts);
            }
            else if (hasRun && runAutoChest)
            {
                chestState.Text = String.Format("自动开箱已停止 · {0}\n本次确认领取 {1:N0} 次 · 尝试 {2:N0} 次", chestMessage, chestConfirmed, chestAttempts);
            }
            else
            {
                chestState.Text = autoChest.Checked ? "自动开箱：等待开始 · 仅在界面明确就绪时领取" : "自动开箱：未启用";
            }
        }

        private void UpdateEstimate()
        {
            if (autoChest.Checked && chestOnly.Checked)
            {
                estimate.Text = String.Format("连续闲置 {0:N0} 秒后监测宝箱 · 保留原有冷却", idle.Value);
                return;
            }
            double seconds = Math.Max(0, (double)target.Value - 1) * (double)interval.Value / 1000 + (double)idle.Value;
            TimeSpan span = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            estimate.Text = String.Format("约每秒 {0:0.##} 次 · 含等待，预计 {1} 小时 {2} 分 {3} 秒", 1000m / interval.Value, (int)span.TotalHours, span.Minutes, span.Seconds);
            if (autoChest.Checked) estimate.Text += "，之后继续监测宝箱";
        }

        private void SessionChanged(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                QueueStop("会话已锁定或断开");
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) QueueStop("电脑进入休眠");
        }

        private void QueueStop(string reason)
        {
            if (!closing && IsHandleCreated)
            {
                try { BeginInvoke((Action)delegate { StopRun(reason); }); }
                catch (InvalidOperationException) { }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == NativeInput.HotkeyId) StopRun("已按全局急停快捷键停止");
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys plainKey = keyData & Keys.KeyCode;
            if (plainKey >= Keys.F21 && plainKey <= Keys.F24) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true;
                StopRun("已关闭");
                timer.Dispose();
                native.Dispose();
                SystemEvents.SessionSwitch -= SessionChanged;
                SystemEvents.PowerModeChanged -= PowerChanged;
            }
            base.Dispose(disposing);
        }

        private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal increment)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Increment = increment, ThousandsSeparator = true, Width = 134 };
        }

        private static Label TextLabel(string text, float size, FontStyle style)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = Color.FromArgb(31, 42, 56), Margin = new Padding(0, 4, 0, 4) };
        }

        private static void AddSetting(TableLayoutPanel panel, int row, string label, Control input, Control hint)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label caption = TextLabel(label, 10, FontStyle.Regular);
            caption.Anchor = AnchorStyles.Left;
            input.Margin = new Padding(0, 6, 0, 6);
            input.Anchor = AnchorStyles.Left;
            hint.Anchor = AnchorStyles.Left;
            panel.Controls.Add(caption, 0, row);
            panel.Controls.Add(input, 1, row);
            panel.Controls.Add(hint, 2, row);
        }
    }
}
