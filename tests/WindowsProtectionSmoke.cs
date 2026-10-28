using System;
using System.Windows.Forms;
using TappoIdle;

internal static class WindowsProtectionSmoke
{
    [STAThread]
    private static int Main()
    {
        // No window is shown, no task is started and SendInput is never called.
        // Check that this Windows session permits both hooks and the hotkey,
        // and that they can be unregistered and registered again cleanly.
        try
        {
            NativeInput.VerifyInputLayout();
            using (Form owner = new Form())
            using (NativeInput input = new NativeInput())
            {
                IntPtr window = owner.Handle;
                input.Install(window);
                input.Dispose();
                input.Install(window);
            }
            Console.WriteLine("PASS: native INPUT layout; keyboard/mouse hooks and Ctrl+Alt+F10 install, cleanup and reinstall (no input sent)");
            return 0;
        }
        catch (Exception ex)
        {
            System.ComponentModel.Win32Exception nativeError = ex as System.ComponentModel.Win32Exception;
            Console.WriteLine("FAIL: " + ex.Message + (nativeError == null ? "" : " Win32=" + nativeError.NativeErrorCode));
            return 1;
        }
    }
}
