// Test-only entry point compiled into a disposable GhostHandles.exe on hosted CI.
using System;
using System.IO;
using System.Threading;

namespace GhostUI {
    internal static class UpdateHandoffParent {
        [STAThread] private static int Main(string[] args) {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted") return 2;
            if (args.Length == 2 && args[0] == "--apply-update") return UpdateInstaller.Run(args[1]);
            try {
                using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\FirefoxGhostUIHandlesStop")) {
                    var store = new UpdateStore();
                    var state = store.Read();
                    var profiles = store.Profiles();
                    if (profiles.Length != 1 || !UpdateRules.Valid(state.Release)) throw new InvalidDataException("Incomplete handoff fixture.");
                    UpdateInstaller.Start(store, state.Release, profiles[0].Path);
                    return stop.WaitOne(TimeSpan.FromSeconds(45)) ? 0 : 1;
                }
            } catch (Exception error) {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 1;
            }
        }
    }
}
