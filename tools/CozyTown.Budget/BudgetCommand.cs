using System.Text.Json;
using CozyTown.Model;

namespace CozyTown.Budget;

public static class BudgetCommand
{
    public static int Run(string[] args, TextWriter output)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--help" }))
            {
                output.WriteLine("CozyTown.Budget --initialize --profile <full-demo-v1|full-demo-v2> --ledger-file <new-absolute-path>");
                output.WriteLine("CozyTown.Budget --stop --profile <registered-profile> --ledger-file <existing-absolute-path> --reason <reason-code>");
                output.WriteLine("Initialization never resets existing files. Stop preserves all attempts, costs, slots and the first stop reason.");
                return 0;
            }
            bool stop = args.Length == 7 && args[0] == "--stop" && args[5] == "--reason"
                && !string.IsNullOrWhiteSpace(args[6]);
            bool initialize = args.Length == 5 && args[0] == "--initialize";
            if ((!stop && !initialize) || args[1] != "--profile" || args[3] != "--ledger-file"
                || string.IsNullOrWhiteSpace(args[4]) || !Path.IsPathFullyQualified(args[4]))
                throw new ModelException("budget_arguments_required");
            if (stop)
            {
                var budget = PersistentBudgetLedger.OpenExisting(args[4], args[2]);
                var before = budget.Snapshot();
                budget.Stop(args[6]);
                Write(output, new { operation = "stop", profileVersion = budget.Profile.Version, before, after = budget.Snapshot() });
            }
            else
            {
                var budget = PersistentBudgetLedger.InitializeNew(args[4], args[2]);
                Write(output, new { operation = "initialize", profileVersion = budget.Profile.Version, budget = budget.Snapshot() });
            }
            return 0;
        }
        catch (ModelException error)
        {
            Write(output, new { operation = "failed", code = error.Code });
            return 1;
        }
        catch
        {
            Write(output, new { operation = "failed", code = "unexpected_failure" });
            return 1;
        }
    }

    private static void Write(TextWriter output, object value) => output.WriteLine(JsonSerializer.Serialize(value,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
}
