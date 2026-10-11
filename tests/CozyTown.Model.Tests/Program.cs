using CozyTown.Model;

namespace CozyTown.Model.Tests;

// The test suite starts separate processes at a file barrier to exercise the real OS lock.
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length is not (5 or 6) || args[0] != "--child-reserve") return 64;
        string ledger = args[1], ready = args[2], gate = args[3], mode = args[4];
        string profile = args.Length == 6 ? args[5] : ModelBudgetProfile.FirstSliceVersion;
        File.WriteAllText(ready, "ready");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(gate))
        {
            if (DateTime.UtcNow >= until) return 65;
            Thread.Sleep(10);
        }
        try
        {
            if (mode == "initialize")
            {
                _ = PersistentBudgetLedger.InitializeNew(ledger, profile);
                return 0;
            }
            var budget = PersistentBudgetLedger.OpenExisting(ledger, profile);
            _ = mode == "manual-original"
                ? budget.Reserve(ModelStage.Manual, ready)
                : budget.Reserve(profile is ModelBudgetProfile.FullDemoVersion or ModelBudgetProfile.FullDemoV2Version ? ModelStage.PlayerDialogue : ModelStage.Baseline,
                    "shared-original", recovery: mode != "original");
            if (mode == "hold")
            {
                File.WriteAllText(ready + ".reserved", "reserved");
                Thread.Sleep(Timeout.Infinite);
            }
            return 0;
        }
        catch (ModelException error)
        {
            Console.WriteLine(error.Code);
            return error.Code is "recovery_limit" or "concurrency_limit" or "ledger_already_exists" or "ledger_initialization_failed" ? 10 : 11;
        }
    }
}
