using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace TappoIdle
{
    internal sealed class ChestStatus
    {
        public string Message;
        public int Confirmed;
        public int Attempts;
        public bool Faulted;
    }

    // UI Automation stays on a background MTA thread. No AutomationElement crosses threads.
    internal sealed class ChestService : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly object lifetime = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HeartbeatLease heartbeat;
        private Thread worker;
        private IntPtr helperWindow;
        private volatile bool disposed;
        private bool workerFinished, sourceDisposed;
        private string clickError;
        private int expectedFocus;
        private long focusDeadline;
        private volatile uint focusProcess;
        private IntPtr focusVisual;
        private string failureReason;
        public event Action<ChestStatus> StatusChanged;
        public event Action<string> Diagnostic;

        public ChestService() { heartbeat = new HeartbeatLease(delegate { return clock.ElapsedMilliseconds; }); }

        public void Start(IntPtr helper)
        {
            if (disposed || worker != null) throw new InvalidOperationException("开箱监测已启动或已停止，请重新开始任务。");
            helperWindow = helper;
            heartbeat.Pulse();
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "Tappo chest UI Automation";
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public void Heartbeat() { heartbeat.Pulse(); }
        public void Stop() { heartbeat.Revoke(); lock (lifetime) { if (!sourceDisposed) cancellation.Cancel(); } }
        public void Dispose()
        {
            lock (lifetime)
            {
                disposed = true;
                heartbeat.Revoke();
                if (!sourceDisposed) cancellation.Cancel();
                if (!sourceDisposed && (worker == null || workerFinished)) { cancellation.Dispose(); sourceDisposed = true; }
            }
        }

        private bool Allowed()
        {
            if (disposed || cancellation.IsCancellationRequested) return false;
            if (!heartbeat.IsAlive())
            {
                failureReason = "界面心跳超过保护时限，自动开箱已停止，请手动重新开始";
                Diagnose("heartbeat expired");
                return false;
            }
            if (NativeInput.IsForeground(helperWindow) || IsExpectedFocusTransition) return true;
            failureReason = "窗口焦点转移到未授权窗口，自动开箱已停止，请手动重新开始";
            Diagnose("unexpected foreground");
            heartbeat.Revoke();
            return false;
        }

        private void Diagnose(string message)
        {
            Action<string> handler = Diagnostic;
            if (handler == null) return;
            IntPtr foreground = GetForegroundWindow();
            uint pid; GetWindowThreadProcessId(foreground, out pid);
            string details = String.Format("{0}ms {1}; fg={2} pid={3} class={4} root={5} owner={6} marker={7} deadline={8}",
                clock.ElapsedMilliseconds, message, foreground, pid, WindowClass(foreground), GetAncestor(foreground, 2), GetAncestor(foreground, 3), Thread.VolatileRead(ref expectedFocus), Interlocked.Read(ref focusDeadline));
            try { handler(details); } catch (Exception) { }
        }

        public bool IsExpectedFocusTransition
        {
            get
            {
                return IsFocusTransitionInProgress && PetOwnsForeground(GetForegroundWindow());
            }
        }

        public bool IsFocusTransitionInProgress
        {
            get
            {
                return !disposed && !cancellation.IsCancellationRequested && Thread.VolatileRead(ref expectedFocus) != 0 &&
                    clock.ElapsedMilliseconds <= Interlocked.Read(ref focusDeadline) && heartbeat.IsAlive();
            }
        }

        public bool IsActionTimedOut { get { return Thread.VolatileRead(ref expectedFocus) != 0 && clock.ElapsedMilliseconds > Interlocked.Read(ref focusDeadline); } }

        private bool PetOwnsForeground(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            uint pid; GetWindowThreadProcessId(window, out pid);
            if (pid == focusProcess) return true;
            uint visualPid; GetWindowThreadProcessId(focusVisual, out visualPid);
            return visualPid == focusProcess && (GetAncestor(window, 2) == focusVisual || GetAncestor(window, 3) == focusVisual);
        }

        private void Run()
        {
            ChestEngine policy = new ChestEngine();
            try
            {
                while (Allowed())
                {
                    Probe probe;
                    try { probe = Read(); }
                    catch (Exception ex)
                    {
                        policy.Observe(clock.ElapsedMilliseconds, new ChestSnapshot());
                        Publish(policy, "暂时无法读取宝箱界面：" + FriendlyError(ex));
                        if (cancellation.Token.WaitHandle.WaitOne(3000)) return;
                        continue;
                    }
                    if (!Allowed()) return;
                    int slot = policy.Observe(clock.ElapsedMilliseconds, probe.Snapshot);
                    if (slot >= 0)
                    {
                        clickError = null;
                        bool posted = false;
                        bool waitingForClicks = false;
                        try
                        {
                            // Re-read just before acting: stale screen coordinates never trigger a click.
                            Probe fresh = Read();
                            if (!Allowed()) return;
                            if (fresh.Snapshot.AvailableClicks.HasValue && fresh.Snapshot.AvailableClicks.Value < 1000)
                            {
                                policy.Observe(clock.ElapsedMilliseconds, fresh.Snapshot);
                                waitingForClicks = true;
                            }
                            else if (fresh.Visual == probe.Visual && fresh.Input == probe.Input &&
                                fresh.ProcessId == probe.ProcessId && fresh.Ready(slot) &&
                                SamePosition(probe.Buttons[slot], fresh.Buttons[slot]))
                                posted = Click(fresh, slot);
                        }
                        catch (Exception) { posted = false; }
                        if (!Allowed()) return;
                        if (!waitingForClicks) policy.RecordClick(clock.ElapsedMilliseconds, slot, posted);
                    }
                    Publish(policy, probe.Description + (clickError == null ? "" : "；" + clickError));
                    if (policy.Faulted) return;
                    // Read frequently near readiness/confirmation; long cooldowns need fewer UIA calls.
                    int delay = policy.PendingSlot >= 0 || slot >= 0 ? 1000 : ScanDelay(probe.Snapshot);
                    if (cancellation.Token.WaitHandle.WaitOne(delay)) return;
                }
            }
            catch (Exception ex)
            {
                if (!cancellation.IsCancellationRequested)
                    Publish(new ChestStatus { Message = "开箱监测已停止：" + FriendlyError(ex), Faulted = true });
            }
            finally
            {
                if (!cancellation.IsCancellationRequested && !disposed && !policy.Faulted)
                    Publish(new ChestStatus { Message = failureReason ?? "开箱保护已停止：窗口焦点或界面心跳未保持，请手动重新开始", Confirmed = policy.Confirmed, Attempts = policy.Attempts, Faulted = true });
                lock (lifetime)
                {
                    workerFinished = true;
                    if (disposed && !sourceDisposed) { cancellation.Dispose(); sourceDisposed = true; }
                }
            }
        }

        private static int ScanDelay(ChestSnapshot snapshot)
        {
            if (snapshot.Lower.State == ChestSlotState.Ready || snapshot.Upper.State == ChestSlotState.Ready) return 1000;
            int seconds = Int32.MaxValue;
            if (snapshot.Lower.State == ChestSlotState.Cooling) seconds = Math.Min(seconds, snapshot.Lower.Seconds);
            if (snapshot.Upper.State == ChestSlotState.Cooling) seconds = Math.Min(seconds, snapshot.Upper.Seconds);
            return seconds == Int32.MaxValue ? 3000 : Math.Max(1000, Math.Min(10000, (seconds - 3) * 1000));
        }

        private static string FriendlyError(Exception ex)
        {
            if (ex is ElementNotAvailableException) return "桌宠界面正在变化，稍后重试";
            if (ex is UnauthorizedAccessException) return "当前会话无权读取桌宠界面";
            return ex.Message;
        }

        private void Publish(ChestEngine policy, string description)
        {
            string message = description + "；" + policy.Message;
            Publish(new ChestStatus { Message = message, Confirmed = policy.Confirmed, Attempts = policy.Attempts, Faulted = policy.Faulted });
        }

        private void Publish(ChestStatus status)
        {
            if (cancellation.IsCancellationRequested || disposed) return;
            Action<ChestStatus> handler = StatusChanged;
            if (handler != null) handler(status);
        }

        public static string DescribeCurrent()
        {
            // Read-only diagnostic; never calls Click or sends input.
            Probe probe = Read();
            return probe.Description + Environment.NewLine + "visual=" + probe.Visual + "; input=" + probe.Input + "; pid=" + probe.ProcessId;
        }

        private sealed class Probe
        {
            public IntPtr Visual, Input;
            public uint ProcessId;
            public RECT WindowBounds;
            public ChestSnapshot Snapshot = new ChestSnapshot();
            public Rect[] Buttons = new Rect[2];
            public AutomationElement[] AccessibleButtons = new AutomationElement[2];
            public string Description;
            public bool Ready(int slot)
            {
                return (slot == 0 ? Snapshot.Lower : Snapshot.Upper).State == ChestSlotState.Ready && Valid(Buttons[slot]);
            }
        }

        private static Probe Read()
        {
            List<IntPtr> visual = new List<IntPtr>();
            List<IntPtr> inputs = new List<IntPtr>();
            HashSet<uint> petProcesses = new HashSet<uint>();
            foreach (Process process in Process.GetProcessesByName("WuwaTappo"))
            {
                petProcesses.Add((uint)process.Id);
                process.Dispose();
            }
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                uint pid; GetWindowThreadProcessId(window, out pid);
                if (!petProcesses.Contains(pid) || !IsWindowVisible(window) || IsIconic(window)) return true;
                string title = WindowText(window);
                string cls = WindowClass(window);
                if (title == "Wuwa Tappo") visual.Add(window);
                if (cls == "DesktopCatInput") inputs.Add(window);
                return true;
            }, IntPtr.Zero);
            Probe result = new Probe();
            if (visual.Count != 1)
            {
                result.Description = visual.Count == 0 ? "未发现可见桌宠窗口" : "检测到多个桌宠窗口，暂不点击";
                return result;
            }
            result.Visual = visual[0];
            uint visualPid; GetWindowThreadProcessId(result.Visual, out visualPid);
            result.ProcessId = visualPid;
            List<IntPtr> matching = inputs.FindAll(delegate(IntPtr input)
            {
                uint pid; GetWindowThreadProcessId(input, out pid);
                RECT inputRect, visualRect;
                return pid == visualPid && GetWindowRect(input, out inputRect) && GetWindowRect(result.Visual, out visualRect) &&
                    inputRect.Left == visualRect.Left && inputRect.Top == visualRect.Top &&
                    inputRect.Right == visualRect.Right && inputRect.Bottom == visualRect.Bottom;
            });
            if (matching.Count != 1)
            {
                result.Description = "未找到同进程、同位置的桌宠点击窗口，暂不点击";
                return result;
            }
            result.Input = matching[0];
            if (!GetWindowRect(result.Input, out result.WindowBounds)) throw new InvalidOperationException("无法读取桌宠窗口位置。");
            AutomationElement root = AutomationElement.FromHandle(result.Visual);
            List<Rect> docks = new List<Rect>();
            List<UiPart> rewards = new List<UiPart>();
            List<UiPart> numbers = new List<UiPart>();
            CacheRequest cache = new CacheRequest();
            {
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.IsEnabledProperty);
                using (cache.Activate())
                {
                    AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    if (elements.Count > 2000) throw new InvalidOperationException("桌宠界面过于复杂，暂不自动点击。");
                    foreach (AutomationElement element in elements)
                    {
                        string cls = element.Cached.ClassName;
                        Rect rect = element.Cached.BoundingRectangle;
                        if (element.Cached.IsOffscreen || !element.Cached.IsEnabled || !Valid(rect)) continue;
                        if (CssClass(cls, "pet-dock-content")) docks.Add(rect);
                        if (CssClass(cls, "reward-countdown") || CssClass(cls, "reward-btn"))
                            rewards.Add(new UiPart { ClassName = cls, Name = element.Cached.Name, Bounds = rect, Element = element });
                        long displayed;
                        if (TryAvailableClicks(element.Cached.Name, out displayed))
                            numbers.Add(new UiPart { Name = element.Cached.Name, Bounds = rect });
                    }
                }
            }
            if (docks.Count != 1)
            {
                result.Description = "无法唯一识别自己的桌宠，暂不点击";
                return result;
            }
            Rect dock = docks[0];
            int balances = 0;
            foreach (UiPart number in numbers)
            {
                Rect bounds = number.Bounds;
                double centerX = bounds.X + bounds.Width / 2;
                double centerY = bounds.Y + bounds.Height / 2;
                if (centerX < dock.Left || centerX > dock.Right || centerY < dock.Bottom - dock.Height * 0.20 || centerY > dock.Bottom + dock.Height * 0.05) continue;
                long value;
                if (TryAvailableClicks(number.Name, out value)) { result.Snapshot.AvailableClicks = value; balances++; }
            }
            if (balances != 1) result.Snapshot.AvailableClicks = null;
            int[] seen = new int[2];
            foreach (UiPart part in rewards)
            {
                double x = part.Bounds.X + part.Bounds.Width / 2;
                double y = part.Bounds.Y + part.Bounds.Height / 2;
                // Local chest slots sit to the right of the pet, lower and upper.
                // A gap between bands rejects ambiguous geometry instead of guessing.
                double height = dock.Height;
                double dy = (dock.Bottom - y) / height;
                if (x < dock.Left || x > dock.Right + dock.Width || height < 60 || height > 1200) continue;
                int slot = dy >= 0.10 && dy <= 0.34 ? 0 : dy >= 0.40 && dy <= 0.70 ? 1 : -1;
                if (slot < 0) continue;
                seen[slot]++;
                ChestSlot value = slot == 0 ? result.Snapshot.Lower : result.Snapshot.Upper;
                if (CssClass(part.ClassName, "reward-countdown"))
                {
                    int seconds;
                    if (TryCountdown(part.Name, out seconds) && seconds > 0)
                    {
                        value.State = ChestSlotState.Cooling;
                        value.Seconds = seconds;
                    }
                }
                else if (CssClass(part.ClassName, "reward-btn"))
                {
                    value.State = ChestSlotState.Ready;
                    result.Buttons[slot] = part.Bounds;
                    result.AccessibleButtons[slot] = part.Element;
                }
            }
            // Duplicate or overlapping cooldown/ready controls are a transition, not permission.
            if (seen[0] != 1) result.Snapshot.Lower.State = ChestSlotState.Unknown;
            if (seen[1] != 1) result.Snapshot.Upper.State = ChestSlotState.Unknown;
            result.Description = "下箱 " + Describe(result.Snapshot.Lower) + " · 上箱 " + Describe(result.Snapshot.Upper);
            if (result.Snapshot.AvailableClicks.HasValue && result.Snapshot.AvailableClicks.Value < 1000) result.Description += "；次数不足 1000，等待补足";
            if (seen[0] + seen[1] == 0) result.Description += "；请在桌宠中启用始终显示宝箱倒计时";
            return result;
        }

        private sealed class UiPart { public string ClassName, Name; public Rect Bounds; public AutomationElement Element; }
        private static bool CssClass(string value, string name) { return (" " + value + " ").Contains(" " + name + " "); }
        private static bool Valid(Rect rect)
        {
            return !rect.IsEmpty && rect.Width > 2 && rect.Height > 2 && !Double.IsInfinity(rect.X) && !Double.IsInfinity(rect.Y) &&
                !Double.IsNaN(rect.X) && !Double.IsNaN(rect.Y) && rect.Width < 20000 && rect.Height < 20000;
        }
        private static bool SamePosition(Rect a, Rect b)
        {
            return Valid(a) && Valid(b) && Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 &&
                Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;
        }
        private static bool TryCountdown(string value, out int seconds)
        {
            seconds = 0;
            string[] parts = (value ?? "").Trim().Split(':');
            int minutes, remainder;
            if (parts.Length != 2 || !Int32.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
                !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out remainder) ||
                minutes < 0 || minutes > 30 || remainder < 0 || remainder > 59) return false;
            seconds = minutes * 60 + remainder;
            return seconds <= 1800;
        }
        private static bool TryAvailableClicks(string text, out long value)
        {
            value = 0;
            string number = (text ?? "").Trim().TrimEnd('+');
            if (number.Length == 0 || number.Length > 18) return false;
            foreach (char c in number) if ((c < '0' || c > '9') && c != ',') return false;
            return Int64.TryParse(number, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) && value >= 0;
        }
        private static string Describe(ChestSlot slot)
        {
            if (slot.State == ChestSlotState.Ready) return "可开启";
            if (slot.State == ChestSlotState.Cooling) return String.Format("{0:00}:{1:00}", slot.Seconds / 60, slot.Seconds % 60);
            return "状态未知";
        }

        private bool Click(Probe probe, int slot)
        {
            if (!Allowed() || !probe.Ready(slot) || !ValidPair(probe)) return false;
            object pattern;
            AutomationElement button = probe.AccessibleButtons[slot];
            if (button != null && button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                // The actual client exposes a public button InvokePattern. Prefer it to coordinates.
                // After attempting Invoke, never also send mouse messages for the same decision.
                if (!Allowed() || !ValidPair(probe)) return false;
                focusProcess = probe.ProcessId;
                focusVisual = probe.Visual;
                Interlocked.Exchange(ref focusDeadline, clock.ElapsedMilliseconds + 2000);
                Interlocked.Exchange(ref expectedFocus, 1);
                Diagnose("invoke start");
                try
                {
                    ((InvokePattern)pattern).Invoke();
                    Diagnose("invoke returned");
                    // Invoke returns before Windows finishes activation. The transition may
                    // pass through the previously active application. Wait without sending
                    // any input; only a stable helper/pet foreground authorizes restoration.
                    if (!WaitForActionForeground(false))
                    {
                        failureReason = "领取后的窗口激活未在保护时限内稳定，已停止";
                        heartbeat.Revoke();
                        return false;
                    }
                    // A normal Invoke may activate the pet. Restore only our own original
                    // foreground, only while this exact action still has permission.
                    if (!NativeInput.IsForeground(helperWindow))
                    {
                        IntPtr beforeRestore = GetForegroundWindow();
                        if ((beforeRestore != helperWindow && !PetOwnsForeground(beforeRestore)) || !Allowed())
                        {
                            clickError = "领取已触发，但恢复前的窗口归属发生变化，已停止";
                            failureReason = clickError;
                            heartbeat.Revoke();
                            return false;
                        }
                        bool requested = SetForegroundWindow(helperWindow);
                        Diagnose("foreground restore request returned " + requested);
                        // Cross-thread activation is asynchronous. Its eventual observed
                        // foreground is the result, not this request's immediate return value.
                    }
                    if (!WaitForActionForeground(true))
                    {
                        failureReason = "领取已触发，但助手前台未能稳定恢复，已停止";
                        heartbeat.Revoke();
                        return false;
                    }
                    return Allowed() && NativeInput.IsForeground(helperWindow);
                }
                catch (Exception ex) { clickError = "标准按钮调用未确认：" + FriendlyError(ex); return false; }
                finally { Interlocked.Exchange(ref expectedFocus, 0); }
            }
            Rect box = probe.Buttons[slot];
            POINT point = new POINT { X = (int)Math.Round(box.X + box.Width / 2), Y = (int)Math.Round(box.Y + box.Height / 2) };
            if (!ScreenToClient(probe.Input, ref point)) return false;
            RECT client;
            if (!GetClientRect(probe.Input, out client) || point.X < 0 || point.Y < 0 || point.X >= client.Right || point.Y >= client.Bottom ||
                point.X > Int16.MaxValue || point.Y > Int16.MaxValue) return false;
            IntPtr coordinates = new IntPtr((point.Y << 16) | (point.X & 0xffff));
            if (!Post(probe.Input, 0x0200, IntPtr.Zero, coordinates)) return false;
            if (cancellation.Token.WaitHandle.WaitOne(30) || !Allowed() || !ValidPair(probe)) return false;
            if (!Post(probe.Input, 0x0201, new IntPtr(1), coordinates)) return false;
            cancellation.Token.WaitHandle.WaitOne(40);
            // Always release a down already enqueued, including during cancellation.
            bool released = Post(probe.Input, 0x0202, IntPtr.Zero, coordinates);
            return released && !cancellation.IsCancellationRequested;
        }

        private bool WaitForActionForeground(bool helperOnly)
        {
            long began = clock.ElapsedMilliseconds;
            long stableSince = -1;
            IntPtr last = IntPtr.Zero;
            while (IsFocusTransitionInProgress)
            {
                long now = clock.ElapsedMilliseconds;
                IntPtr foreground = GetForegroundWindow();
                bool trusted = foreground == helperWindow || (!helperOnly && PetOwnsForeground(foreground));
                if (trusted)
                {
                    if (foreground != last || stableSince < 0) stableSince = now;
                    if (now - began >= (helperOnly ? 50 : 250) && now - stableSince >= 50) return true;
                }
                else stableSince = -1;
                last = foreground;
                if (cancellation.Token.WaitHandle.WaitOne(10)) return false;
            }
            return false;
        }

        private bool Post(IntPtr window, uint message, IntPtr parameter, IntPtr coordinates)
        {
            if (PostMessage(window, message, parameter, coordinates)) return true;
            int error = Marshal.GetLastWin32Error();
            clickError = error == 5 ? "Windows 拒绝发送点击消息（错误 5），请检查两个程序的权限是否一致"
                : "点击消息发送失败（Windows 错误 " + error + "）";
            return false;
        }

        private static bool ValidPair(Probe probe)
        {
            uint visualPid, inputPid;
            RECT current;
            GetWindowThreadProcessId(probe.Visual, out visualPid);
            GetWindowThreadProcessId(probe.Input, out inputPid);
            return GetWindowRect(probe.Input, out current) && current.Left == probe.WindowBounds.Left && current.Top == probe.WindowBounds.Top &&
                current.Right == probe.WindowBounds.Right && current.Bottom == probe.WindowBounds.Bottom &&
                visualPid == probe.ProcessId && inputPid == probe.ProcessId && IsWindowVisible(probe.Visual) &&
                IsWindowVisible(probe.Input) && !IsIconic(probe.Visual) && WindowText(probe.Visual) == "Wuwa Tappo" && WindowClass(probe.Input) == "DesktopCatInput";
        }

        private static string WindowText(IntPtr window) { StringBuilder text = new StringBuilder(256); GetWindowText(window, text, text.Capacity); return text.ToString(); }
        private static string WindowClass(IntPtr window) { StringBuilder text = new StringBuilder(128); GetClassName(window, text, text.Capacity); return text.ToString(); }
        private delegate bool WindowCallback(IntPtr window, IntPtr unused);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr unused);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr parameter, IntPtr coordinates);
    }
}
