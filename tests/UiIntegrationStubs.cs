using System;
using System.Collections.Generic;

// Compile these substitutes instead of src/NativeInput.cs and src/ChestService.cs.
// They never install hooks, inject input, inspect UIA, or send window messages.
namespace TappoIdle
{
    internal sealed class NativeInput : IDisposable
    {
        public static int Installs, Sends;
        public static bool Foreground = true, Pet = true, SendSuccess = true;
        public static Action DuringSend;
        public static bool ForegroundIsTransitioning { get; set; }
        public const int HotkeyId = 0x5712;
        public event Action PhysicalInput;

        public void Install(IntPtr owner) { Installs++; }
        public void Dispose() { }
        public void Raise() { if (PhysicalInput != null) PhysicalInput(); }
        public static bool IsForeground(IntPtr window) { return Foreground; }
        public static bool IsPetRunning() { return Pet; }
        public static bool SendKeyPair(ushort key, IntPtr window)
        {
            Sends++;
            if (DuringSend != null) DuringSend();
            return SendSuccess;
        }
        public static void VerifyInputLayout() { }
    }

    internal sealed class ChestStatus
    {
        public string Message;
        public int Confirmed, Attempts;
        public bool Faulted;
    }

    internal sealed class ChestService : IDisposable
    {
        public static readonly List<ChestService> Created = new List<ChestService>();
        public int Starts, Stops, Disposes, Heartbeats;
        public bool IsExpectedFocusTransition { get; set; }
        public bool InFlight;
        public bool IsFocusTransitionInProgress { get { return IsExpectedFocusTransition || InFlight; } }
        public bool IsActionTimedOut { get; set; }
        public event Action<ChestStatus> StatusChanged;

        public ChestService() { Created.Add(this); }
        public void Start(IntPtr window) { Starts++; }
        public void Heartbeat() { Heartbeats++; }
        public void Stop() { Stops++; }
        public void Dispose() { Disposes++; }
        public void Emit(string message, bool faulted, int confirmed, int attempts)
        {
            if (StatusChanged != null)
                StatusChanged(new ChestStatus { Message = message, Faulted = faulted, Confirmed = confirmed, Attempts = attempts });
        }
    }
}
