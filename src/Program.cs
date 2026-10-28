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
        private readonly Label estimate = TextLabel("", 10, FontStyle.Regular);
        private readonly Label state = TextLabel("尚未启动", 17, FontStyle.Bold);
        private readonly Label count = TextLabel("本次已发送 0 次", 11, FontStyle.Regular);
        private readonly Label detail = TextLabel("请先打开官方桌宠，再点开始。", 10, FontStyle.Regular);
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly bool smoke;
        private bool ready;
        private bool closing;
        private ushort chosenKey;
        private readonly int[] presetValues = { 2000, 1000, 500, 200, 100, 50 };

        public MainForm(bool smokeMode)
        {
            smoke = smokeMode;
            Text = "鸣潮桌宠 · 闲置按键助手";
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(660, 770);
            MinimumSize = new Size(660, 580);
            BackColor = Color.FromArgb(246, 248, 250);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.AutoScroll = true;
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
            Controls.Add(root);

            Label heading = TextLabel("闲置时补足次数", 23, FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(heading, 0, 0);
            Label intro = TextLabel("速度由你选择 · 回来操作即停止", 11, FontStyle.Regular);
            intro.ForeColor = Color.FromArgb(80, 94, 110);
            intro.Margin = new Padding(0, 0, 0, 18);
            root.Controls.Add(intro, 0, 1);

            Label risk = TextLabel("模拟按键仍可能违反官方规则，不能保证不受处罚。\n已发送次数不等于桌宠实际入账；开箱仍受原有冷却限制。", 10, FontStyle.Regular);
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
            AddSetting(settings, 1, "本次发送目标", target, TextLabel("次，达标停止", 10, FontStyle.Regular));
            AddSetting(settings, 2, "启动前连续闲置", idle, TextLabel("秒，操作会重新计时", 10, FontStyle.Regular));
            key.DropDownStyle = ComboBoxStyle.DropDownList;
            key.Items.AddRange(new object[] { "F24", "F23", "F22", "F21" });
            key.SelectedIndex = 0;
            key.Width = 134;
            AddSetting(settings, 3, "模拟按键", key, TextLabel("特殊功能键，无文字输入", 10, FontStyle.Regular));
            root.Controls.Add(settings, 0, 3);

            estimate.ForeColor = Color.FromArgb(30, 102, 107);
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
            statusPanel.RowCount = 4;
            for (int row = 0; row < 4; row++) statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            state.Margin = new Padding(0, 0, 0, 10);
            count.Margin = new Padding(0, 0, 0, 12);
            progress.Dock = DockStyle.Top;
            progress.Height = 12;
            progress.Margin = new Padding(0, 0, 0, 12);
            detail.MaximumSize = new Size(540, 0);
            statusPanel.Controls.Add(state, 0, 0);
            statusPanel.Controls.Add(count, 0, 1);
            statusPanel.Controls.Add(progress, 0, 2);
            statusPanel.Controls.Add(detail, 0, 3);
            root.Controls.Add(statusPanel, 0, 6);

            Label footer = TextLabel("运行期间保持本窗口在前台。移动鼠标或敲键盘即停止。\n全局急停：Ctrl + Alt + F10；锁屏、休眠或切换窗口也会停止。\n不自动开箱；发送后请在桌宠中核对实际次数。", 9, FontStyle.Regular);
            footer.ForeColor = Color.FromArgb(95, 105, 117);
            footer.Margin = new Padding(0, 18, 0, 0);
            root.Controls.Add(footer, 0, 8);

            presets.SelectedIndexChanged += delegate { if (presets.SelectedIndex >= 0) interval.Value = presetValues[presets.SelectedIndex]; };
            interval.ValueChanged += delegate { UpdateEstimate(); };
            target.ValueChanged += delegate { UpdateEstimate(); };
            idle.ValueChanged += delegate { UpdateEstimate(); };
            start.Click += delegate { StartRun(); };
            stop.Click += delegate { StopRun("已手动停止"); };
            native.PhysicalInput += delegate { engine.PhysicalInput(clock.ElapsedMilliseconds); };
            timer.Interval = 20;
            timer.Tick += delegate { TickRun(); };
            Deactivate += delegate { StopRun("窗口已离开前台"); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { StopRun("已按 Esc 停止"); e.Handled = e.SuppressKeyPress = true; }
            };
            FormClosing += delegate { closing = true; engine.Stop("已关闭"); timer.Stop(); native.Dispose(); };
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
            if (!ready || closing) return;
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
            engine.Start(now, now, (int)interval.Value, (int)target.Value, (int)idle.Value);
            RenderState();
        }

        private void TickRun()
        {
            try
            {
                bool active = engine.State == RunState.Waiting || engine.State == RunState.Running;
                if (active)
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
                    engine.Tick(now, foreground, petRunning, delegate { return NativeInput.SendKeyPair(chosenKey, Handle); });
                }
                RenderState();
            }
            catch (Exception ex)
            {
                engine.Stop("运行异常，已停止：" + ex.Message);
                RenderState();
            }
        }

        private void StopRun(string reason)
        {
            if (engine.State == RunState.Waiting || engine.State == RunState.Running)
            {
                engine.Stop(reason);
                RenderState();
            }
        }

        private void RenderState()
        {
            bool active = engine.State == RunState.Waiting || engine.State == RunState.Running;
            start.Enabled = ready && !active;
            stop.Enabled = active;
            interval.Enabled = target.Enabled = idle.Enabled = key.Enabled = presets.Enabled = !active;
            count.Text = String.Format("本次已发送 {0:N0} 次  /  {1:N0} 次", engine.Sent, active || engine.Target > 0 ? engine.Target : (int)target.Value);
            progress.Maximum = engine.Target > 0 ? engine.Target : (int)target.Value;
            progress.Value = Math.Min(engine.Sent, progress.Maximum);
            if (engine.Target == 0) return;
            if (engine.State == RunState.Waiting)
            {
                state.Text = String.Format("等待闲置 · 还需 {0} 秒", (engine.IdleRemainingMs + 999) / 1000);
                detail.Text = "现在可以离开电脑。保持本窗口在前台，倒计时结束后开始发送。";
            }
            else if (engine.State == RunState.Running)
            {
                state.Text = "正在发送 · " + key.Text;
                detail.Text = "Windows 接收成功才计入发送数；桌宠可能忽略模拟输入。回来操作就会停止。";
            }
            else if (engine.State == RunState.Completed)
            {
                state.Text = "目标已完成";
                detail.Text = "已停止发送。请到桌宠核对实际次数；开箱仍需满足原有冷却。";
            }
            else if (!String.IsNullOrEmpty(engine.Reason))
            {
                state.Text = "已停止";
                detail.Text = engine.Reason.TrimEnd('。') + "。继续使用需要手动点击开始。";
            }
        }

        private void UpdateEstimate()
        {
            double seconds = Math.Max(0, (double)target.Value - 1) * (double)interval.Value / 1000 + (double)idle.Value;
            TimeSpan span = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            estimate.Text = String.Format("约每秒 {0:0.##} 次 · 含等待，预计 {1} 小时 {2} 分 {3} 秒", 1000m / interval.Value, (int)span.TotalHours, span.Minutes, span.Seconds);
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
