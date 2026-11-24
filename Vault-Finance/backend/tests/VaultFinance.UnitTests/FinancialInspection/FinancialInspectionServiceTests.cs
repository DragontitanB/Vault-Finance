using VaultFinance.Application.FinancialInspection;

namespace VaultFinance.UnitTests.FinancialInspection;

public sealed class FinancialInspectionServiceTests
{
    [Fact]
    public async Task OrdinaryContext_IsForbiddenWithoutReadingAnything()
    {
        var reader = new Stub(new()); var result = await new FinancialInspectionService(reader).InspectAsync(new(Guid.NewGuid(), Guid.NewGuid(), false));
        Assert.Equal(InspectionStatus.Forbidden, result.Status); Assert.Equal(0, reader.Calls);
    }
    [Theory]
    [InlineData("inspection.unavailable", InspectionStatus.ExecutionFailed)]
    [InlineData("inspection.permission_denied", InspectionStatus.ExecutionFailed)]
    [InlineData("inspection.read_failed", InspectionStatus.ExecutionFailed)]
    [InlineData("inspection.capture_incomplete", InspectionStatus.ExecutionFailed)]
    [InlineData("inspection.limit_exceeded", InspectionStatus.Incomplete)]
    public async Task CaptureFailure_IsAnExplicitNonHealthyReport(string code, InspectionStatus status)
    {
        var result = await new FinancialInspectionService(new Stub(new(ErrorCode: code))).InspectAsync(new(Guid.NewGuid(), Guid.NewGuid(), true));
        Assert.Equal(status, result.Status); Assert.Equal(code, result.ErrorCode); Assert.Null(result.InspectionId);
    }
    [Fact]
    public async Task AuthorizedScope_CanInspectOnlyTheRequestedOwnerAndHonorsCancellation()
    {
        var f = new InspectionFixture(); var reader = new Stub(new(f.Snapshot())); var service = new FinancialInspectionService(reader);
        Assert.Equal(InspectionStatus.Healthy, (await service.InspectAsync(new(Guid.NewGuid(), f.Owner, true))).Status); Assert.Equal(f.Owner, reader.Owner);
        Assert.Equal(InspectionStatus.ExecutionFailed, (await service.InspectAsync(new(Guid.NewGuid(), Guid.NewGuid(), true))).Status);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); int calls = reader.Calls;
        Assert.Equal("inspection.cancelled", (await service.InspectAsync(new(Guid.NewGuid(), f.Owner, true), cancellation.Token)).ErrorCode); Assert.Equal(calls, reader.Calls);
        Assert.Equal("inspection.invalid_context", (await service.InspectAsync(new(Guid.Empty, f.Owner, true))).ErrorCode);
    }
    private sealed class Stub(JournalCapture result) : IJournalInspectionReader
    {
        public int Calls { get; private set; }
        public Guid Owner { get; private set; }
        public Task<JournalCapture> CaptureAsync(Guid ownerId, CancellationToken cancellationToken) { Calls++; Owner = ownerId; return Task.FromResult(result); }
    }

    [Theory]
    [InlineData(true, "inspection.cancelled")]
    [InlineData(false, "inspection.calendar_unavailable")]
    public async Task KnownExecutionExceptions_ProduceAnErrorReport(bool cancelled, string code)
    {
        var report = await new FinancialInspectionService(new ThrowingReader(cancelled))
            .InspectAsync(new(Guid.NewGuid(), Guid.NewGuid(), true));
        Assert.Equal(InspectionStatus.ExecutionFailed, report.Status);
        Assert.Equal(code, report.ErrorCode);
        Assert.Null(report.InspectionId);
    }

    private sealed class ThrowingReader(bool cancelled) : IJournalInspectionReader
    {
        public Task<JournalCapture> CaptureAsync(Guid ownerId, CancellationToken cancellationToken) =>
            throw (cancelled ? new OperationCanceledException() : new TimeZoneNotFoundException());
    }
}
