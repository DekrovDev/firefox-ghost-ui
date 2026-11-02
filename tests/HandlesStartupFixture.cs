// Inert fixture for the companion-start/stop race; never targets Firefox.
using System;
using System.Threading;

internal static class HandlesStartupFixture {
    private static int Main(string[] args) {
        Thread.Sleep(700);
        if (args.Length == 1 && args[0] == "exit") return 0;
        using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\FirefoxGhostUIHandlesStop"))
            return stop.WaitOne(8000) ? 0 : 1;
    }
}
