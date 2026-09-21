using CozyTown.Model;

namespace CozyTown.Model.Tests;

// The test suite starts separate processes at a file barrier to exercise the real OS lock.
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 5 || args[0] != "--child-reserve") return 64;
        string ledger = args[1], ready = args[2], gate = args[3], mode = args[4];
        File.WriteAllText(ready, "ready");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(gate))
        {
            if (DateTime.UtcNow >= until) return 65;
            Thread.Sleep(10);
        }
        try
        {
            var budget = PersistentBudgetLedger.OpenExisting(ledger);
            _ = budget.Reserve(ModelStage.Baseline, "shared-original", recovery: mode != "original");
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
            return error.Code == "recovery_limit" ? 10 : 11;
        }
    }
}
