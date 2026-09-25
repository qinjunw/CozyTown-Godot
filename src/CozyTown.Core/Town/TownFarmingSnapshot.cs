using System.Diagnostics.CodeAnalysis;

namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    private static void ValidateFarmingStructure(TownContent content, FarmingState? farming, double worldMinute)
    {
        void Require([DoesNotReturnIf(false)] bool condition, string field)
        {
            if (!condition) throw new InvalidDataException($"Invalid farming snapshot: {field}");
        }
        Require(farming is not null && farming.Plots is not null && farming.Receipts is not null && farming.SleepReceipts is not null && farming.Mornings is not null, "required fields");
        Require(farming.Plots.All(p => p is not null) && farming.Plots.Select(p => p.Id).SequenceEqual(content.Data.Farming!.Plots.Select(p => p.Id)), "plot bindings/order");
        Require(farming.LastSettledDay == SettledDayAt(worldMinute), "morning cursor");
        long initialDay = SettledDayAt(content.Data.StartMinute);
        Require(farming.Mornings.Count == farming.LastSettledDay - initialDay
            && farming.Mornings.Select((r, i) => r is not null && r.Day == initialDay + i + 1).All(v => v), "morning sources");
        foreach (var plot in farming.Plots)
        {
            Require(plot.Generation >= 0 && plot.HarvestedCount >= 0 && plot.HarvestedCount <= plot.Generation, $"plot {plot.Id} generation/harvest");
            if (plot.CropId is null)
                Require(plot.GrowthDays == 0 && !plot.Watered && !plot.Mature && plot.Generation == plot.HarvestedCount, $"plot {plot.Id} empty state");
            else
            {
                var crop = content.Data.Farming!.Crops.SingleOrDefault(c => c.Id == plot.CropId);
                Require(crop is not null && plot.GrowthDays >= 0 && plot.GrowthDays <= crop.GrowthDays
                    && plot.Mature == (plot.GrowthDays == crop.GrowthDays) && (!plot.Mature || !plot.Watered)
                    && plot.Generation == (long)plot.HarvestedCount + 1, $"plot {plot.Id} crop state");
            }
        }
        foreach (var (id, receipt) in farming.Receipts)
        {
            Require(ValidOperationId(id) && receipt is not null && receipt.Request is not null && receipt.Result is not null, "farm receipt fields");
            var request = receipt.Request;
            var result = receipt.Result;
            Require(new[] { request.OperationId, request.ActorId, request.PlotId, request.Action, request.CropId ?? "" }
                .All(v => v is not null && v.Length <= 200 && !v.Any(char.IsControl)), "farm request text");
            Require(id == request.OperationId && id == result.OperationId && !result.IsDuplicate && result.Succeeded == (result.Code == "accepted")
                && FarmReceiptCodes.Contains(result.Code) && TownContent.Finite(receipt.WorldMinute), "farm receipt result");
        }
        foreach (var (id, receipt) in farming.SleepReceipts)
        {
            Require(ValidOperationId(id) && receipt is not null && receipt.Request is not null && receipt.Result is not null, "sleep receipt fields");
            var request = receipt.Request;
            var result = receipt.Result;
            Require(request.ActorId is not null && request.ActorId.Length <= 200 && !request.ActorId.Any(char.IsControl)
                && id == request.OperationId && id == result.OperationId && !result.IsDuplicate
                && result.Succeeded == (result.Code == "accepted") && SleepReceiptCodes.Contains(result.Code), "sleep receipt result");
            Require(TownContent.Finite(result.FromMinute) && TownContent.Finite(result.ToMinute) && result.FromMinute >= content.Data.StartMinute
                && result.ToMinute >= result.FromMinute && result.ToMinute <= worldMinute + 1e-7, "sleep interval");
            if (result.Succeeded)
                Require(request.ActorId == "player" && request.Hours is >= 1 and <= 12
                    && result.ToMinute - result.FromMinute >= request.Hours * 60 - 1e-7
                    && result.ToMinute - result.FromMinute <= request.Hours * 60 + MaximumAdvanceMinutes + 1e-7, "sleep duration");
            else Require(result.ToMinute == result.FromMinute, "rejected sleep interval");
        }
    }

    private static readonly HashSet<string> FarmReceiptCodes =
    ["accepted", "invalid_actor", "unknown_plot", "plot_out_of_reach", "unknown_crop", "plot_occupied", "insufficient_seed",
        "numeric_overflow", "unexpected_crop", "invalid_action", "empty_plot", "already_mature", "not_mature", "inventory_full"];
    private static readonly HashSet<string> SleepReceiptCodes =
    ["accepted", "invalid_actor", "invalid_sleep_hours", "paused", "bed_out_of_reach", "advance_limit", "advance_failed"];
}
