using System;
using TappoIdle;

internal static class WindowsChestReadOnly
{
    [MTAThread]
    private static int Main()
    {
        try
        {
            // Uses the production reader, without starting a service or sending any input.
            Console.WriteLine(ChestService.DescribeCurrent());
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Read-only probe failed: " + ex.Message);
            return 1;
        }
    }
}
