using System.Text.Json;
using CozyTown.Model;

try
{
    if (args.SequenceEqual(new[] { "--help" }))
    {
        Console.WriteLine("CozyTown.Budget --initialize --profile full-demo-v1 --ledger-file <new-absolute-path>");
        Console.WriteLine("Creates one new authorized budget and durable lock marker. Existing files are never reset.");
        return 0;
    }
    if (args.Length != 5 || args[0] != "--initialize" || args[1] != "--profile"
        || args[3] != "--ledger-file" || string.IsNullOrWhiteSpace(args[4]) || !Path.IsPathFullyQualified(args[4]))
        throw new ModelException("initialization_arguments_required");
    var budget = PersistentBudgetLedger.InitializeNew(args[4], args[2]);
    Console.WriteLine(JsonSerializer.Serialize(new { profileVersion = budget.Profile.Version, budget = budget.Snapshot() },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    return 0;
}
catch (ModelException error)
{
    Console.Error.WriteLine("budget_initialization_failed:" + error.Code);
    return 1;
}
catch
{
    Console.Error.WriteLine("budget_initialization_failed:unexpected_failure");
    return 1;
}
