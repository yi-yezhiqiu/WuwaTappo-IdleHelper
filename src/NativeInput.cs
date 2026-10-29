using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TappoIdle
{
    // Only the input's injected flag is inspected; no key/text/position history is kept.
    internal sealed class NativeInput : IDisposable
    {
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        private readonly HookProc keyboardCallback;
        private readonly HookProc mouseCallback;
        private IntPtr keyboardHook;
        private IntPtr mouseHook;
        private IntPtr hotkeyWindow;
        public event Action PhysicalInput;
        public const int HotkeyId = 0x5712;

        public NativeInput()
        {
            keyboardCallback = KeyboardHook;
            mouseCallback = MouseHook;
        }

        public void Install(IntPtr window)
        {
            IntPtr module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(13, keyboardCallback, module, 0);
            if (keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听键盘，已禁止发送。");
            mouseHook = SetWindowsHookEx(14, mouseCallback, module, 0);
            if (mouseHook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error, "无法监听鼠标，已禁止发送。");
            }
            // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, F10. F12 is reserved by Windows.
            if (!RegisterHotKey(window, HotkeyId, 0x4003, 0x79))
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                string message = error == 1409 ? "Ctrl+Alt+F10 急停快捷键被占用，已禁止发送。"
                    : "无法注册 Ctrl+Alt+F10 急停快捷键（Windows 错误 " + error + "），已禁止发送。";
                throw new Win32Exception(error, message);
            }
            hotkeyWindow = window;
        }

        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && (Marshal.ReadInt32(data, 8) & 0x12) == 0) NotifyPhysical();
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && (Marshal.ReadInt32(data, 12) & 0x03) == 0) NotifyPhysical();
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private void NotifyPhysical()
        {
            Action handler = PhysicalInput;
            if (handler != null) handler();
        }

        public static bool IsForeground(IntPtr window)
        {
            return GetForegroundWindow() == window;
        }

        public static bool ForegroundIsTransitioning { get { return GetForegroundWindow() == IntPtr.Zero; } }

        public static bool IsPetRunning()
        {
            Process[] pets = Process.GetProcessesByName("WuwaTappo");
            bool found = pets.Length > 0;
            foreach (Process pet in pets) pet.Dispose();
            return found;
        }

        public static bool SendKeyPair(ushort virtualKey, IntPtr inputWindow)
        {
            // Recheck immediately before injection to reduce focus-change races.
            if (!IsForeground(inputWindow)) return false;
            INPUT down = new INPUT();
            down.type = 1;
            down.data.keyboard.virtualKey = virtualKey;
            INPUT up = down;
            up.data.keyboard.flags = 2;
            INPUT[] pair = new INPUT[] { down, up };
            uint sent = SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
            if (sent == 2) return true;
            // If Windows accepted only key-down, make a best-effort release.
            if (sent == 1) SendInput(1, new INPUT[] { up }, Marshal.SizeOf(typeof(INPUT)));
            return false;
        }

        public static void VerifyInputLayout()
        {
            int expected = IntPtr.Size == 8 ? 40 : 28;
            if (Marshal.SizeOf(typeof(INPUT)) != expected)
                throw new InvalidOperationException("INPUT 布局不符合本机架构，禁止发送。");
        }

        public void Dispose()
        {
            if (hotkeyWindow != IntPtr.Zero) UnregisterHotKey(hotkeyWindow, HotkeyId);
            hotkeyWindow = IntPtr.Zero;
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            keyboardHook = mouseHook = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT keyboard;
            [FieldOffset(0)] public MOUSEINPUT mouse;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort virtualKey, scanCode;
            public uint flags, time;
            public UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, flags, time;
            public UIntPtr extraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
