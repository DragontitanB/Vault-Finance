namespace VaultFinance.Application.FinancialInspection;

public sealed class FinancialInspectionService(IJournalInspectionReader reader) : IFinancialInspectionService
{
    public async Task<FinancialInspectionReport> InspectAsync(FinancialInspectionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.CanInspectJournal)
            return Failed(InspectionStatus.Forbidden, context.OwnerId, "inspection.forbidden");
        if (context.ActorId == Guid.Empty || context.OwnerId == Guid.Empty)
            return Failed(InspectionStatus.ExecutionFailed, context.OwnerId, "inspection.invalid_context");
        if (cancellationToken.IsCancellationRequested)
            return Failed(InspectionStatus.ExecutionFailed, context.OwnerId, "inspection.cancelled");
        try
        {
            var captured = await reader.CaptureAsync(context.OwnerId, cancellationToken);
            if (captured.ErrorCode is not null || captured.Snapshot is null)
                return Failed(captured.ErrorCode == "inspection.limit_exceeded" ? InspectionStatus.Incomplete : InspectionStatus.ExecutionFailed,
                    context.OwnerId, captured.ErrorCode ?? "inspection.capture_incomplete");
            if (captured.Snapshot.OwnerId != context.OwnerId)
                return Failed(InspectionStatus.ExecutionFailed, context.OwnerId, "inspection.scope_invalid");
            cancellationToken.ThrowIfCancellationRequested();
            return new JournalIntegrityEvaluator().Evaluate(captured.Snapshot);
        }
        catch (OperationCanceledException)
        { return Failed(InspectionStatus.ExecutionFailed, context.OwnerId, "inspection.cancelled"); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return Failed(InspectionStatus.ExecutionFailed, context.OwnerId, "inspection.calendar_unavailable"); }
    }

    internal static FinancialInspectionReport Failed(InspectionStatus status, Guid owner, string code) =>
        new(status, owner, null, null, null, [], [], code);
}
