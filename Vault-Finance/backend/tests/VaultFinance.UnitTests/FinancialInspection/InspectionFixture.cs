using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Application.FinancialInspection;
using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.UnitTests.FinancialInspection;

// Synthetic states only. Invalid snapshots never reach SQL or production constraints/triggers.
internal sealed class InspectionFixture(string currency = "USD")
{
    public Guid Owner { get; } = Guid.NewGuid();
    public Guid A { get; } = Guid.NewGuid();
    public Guid B { get; } = Guid.NewGuid();
    public List<JournalBatch> Batches { get; } = [];
    public List<JournalOperation> Operations { get; } = [];
    public List<JournalOpening> Openings { get; } = [];
    public List<JournalOutcome> Outcomes { get; } = [];
    public List<JournalAudit> Audits { get; } = [];
    public List<JournalEffect> Effects { get; } = [];
    private readonly Dictionary<Guid, decimal> balances = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public Guid? Open(Guid account, decimal amount)
    {
        var batch = Batch(1); var op = amount == 0m ? null : Op(batch, 1, amount, null, account, reason: "Synthetic opening");
        var version = new JournalOpening(Owner, currency, account, 1, null, batch.Id, op?.Id, op is null ? null : 1, "Synthetic opening"); Openings.Add(version);
        Finish(batch, new OpeningReceipt(1, account, batch.Id, op?.Id, 1, amount, currency, batch.RecordedAtUtc, batch.EffectiveDate),
            $"VF-OPEN\n1\n{account:D}\n{Money(amount)}\n{currency}\n{Tail(version.Reason)}", new { accountId = account, batchId = batch.Id });
        balances.TryAdd(account, 0m); return op?.Id;
    }
    public Guid Income(Guid account, decimal amount, string? reason = null) => Entry(account, amount, true, reason);
    public Guid Expense(Guid account, decimal amount) => Entry(account, amount, false, null);
    private Guid Entry(Guid account, decimal amount, bool income, string? reason)
    {
        var batch = Batch(income ? 3 : 4); var op = Op(batch, income ? 2 : 3, amount, income ? null : account, income ? account : null, reason: reason);
        Finish(batch, new FinancialEntryReceipt(1, account, batch.Id, op.Id, (FinancialOperationKind)op.Kind, amount, currency, batch.RecordedAtUtc, batch.EffectiveDate),
            $"{(income ? "VF-INCOME" : "VF-EXPENSE")}\n1\n{account:D}\n{Money(amount)}\n{currency}\n{Tail(reason)}", new { accountId = account, batchId = batch.Id });
        return op.Id;
    }
    public Guid Transfer(Guid from, Guid to, decimal amount)
    {
        var batch = Batch(5); var op = Op(batch, 4, amount, from, to);
        Finish(batch, new FinancialTransferReceipt(1, from, to, batch.Id, op.Id, FinancialOperationKind.Transfer, amount, currency, batch.RecordedAtUtc, batch.EffectiveDate),
            $"VF-TRANSFER\n1\n{from:D}\n{to:D}\n{Money(amount)}\n{currency}\n-1:", new { sourceAccountId = from, destinationAccountId = to, batchId = batch.Id });
        return op.Id;
    }
    public Guid Reverse(Guid originalId)
    {
        var original = Operations.Single(x => x.Id == originalId); var batch = Batch(6); var op = Compensate(batch, original);
        Guid? debit = original.Kind is 2 or 4 ? original.DestinationAccountId : null;
        Guid? credit = original.Kind is 3 or 4 ? original.SourceAccountId : null;
        Finish(batch, new FinancialReversalReceipt(1, original.Id, (FinancialOperationKind)original.Kind, batch.Id, op.Id, FinancialOperationKind.Reversal,
            original.Amount!.Value, currency, debit, credit, batch.RecordedAtUtc, batch.EffectiveDate),
            $"VF-REVERSE\n1\n{original.Id:D}\n{Tail(op.Reason)}", new { originalOperationId = original.Id, batchId = batch.Id });
        return op.Id;
    }
    public void Correct(Guid account, decimal amount)
    {
        var previous = Openings.Where(x => x.AccountId == account).MaxBy(x => x.Revision)!;
        var old = Operations.FirstOrDefault(x => x.Id == previous.OpeningOperationId); var batch = Batch(2);
        var compensation = old is null ? null : Compensate(batch, old);
        var replacement = amount == 0m ? null : Op(batch, 1, amount, null, account, reason: "Synthetic correction");
        var version = new JournalOpening(Owner, currency, account, previous.Revision + 1, previous.Revision, batch.Id, replacement?.Id, replacement is null ? null : 1, "Synthetic correction"); Openings.Add(version);
        Finish(batch, new OpeningCorrectionReceipt(1, account, batch.Id, previous.Revision, version.Revision, old?.Id, compensation?.Id, replacement?.Id,
            old?.Amount ?? 0m, amount, currency, batch.RecordedAtUtc, batch.EffectiveDate),
            $"VF-CORRECT-OPEN\n1\n{account:D}\n{previous.Revision}\n{Money(amount)}\n{currency}\n{Tail(version.Reason)}", new { accountId = account, expectedRevision = previous.Revision, batchId = batch.Id });
    }
    public void Reject(int command, string code, Guid? requested = null)
    {
        var id = Guid.NewGuid(); var time = Start.AddMinutes(20); Guid target = requested ?? A;
        var row = new JournalOutcome(id, Owner, command, "rejected-" + id.ToString("N"), new string('A', 64), 1, 2, null, code,
            JsonSerializer.Serialize(new { receiptVersion = 1, failure = code }, Json), time); Outcomes.Add(row);
        object summary = command switch { 5 => new { sourceAccountId = target, destinationAccountId = B, batchId = (Guid?)null },
            6 => new { originalOperationId = target, batchId = (Guid?)null }, 2 => new { accountId = target, expectedRevision = 1, batchId = (Guid?)null },
            _ => new { accountId = target, batchId = (Guid?)null } };
        Audits.Add(new(Guid.NewGuid(), Owner, id, time, $"financial.{Name(command)}.rejected", JsonSerializer.Serialize(summary, Json)));
    }
    public JournalSnapshot Snapshot() => new(Guid.NewGuid(), Owner, Start.AddHours(1), "synthetic-fixture-v1", true,
        [new(A, Owner, currency, 1), new(B, Owner, currency, 1)], Batches.ToArray(), Operations.ToArray(), Openings.ToArray(), Outcomes.ToArray(), Audits.ToArray(), Effects.ToArray(),
        new[] { A, B }.Select(id => new JournalReportedBalance(id, currency, Openings.Any(x => x.AccountId == id && x.Revision == 1), balances.TryGetValue(id, out var value) ? value : null, null)).ToArray());

    private JournalBatch Batch(int command)
    {
        var time = Start.AddSeconds(Batches.Count + 1);
        var batch = new JournalBatch(Guid.NewGuid(), Owner, Batches.Count + 1, command, time, DateOnly.FromDateTime(time.AddHours(-4).DateTime), "America/Santo_Domingo"); Batches.Add(batch); return batch;
    }
    private JournalOperation Op(JournalBatch batch, int kind, decimal amount, Guid? from, Guid? to, string? reason = null)
    {
        var op = new JournalOperation(Guid.NewGuid(), Owner, batch.Id, kind, currency, amount, from, to, null, null, reason); Operations.Add(op);
        if (from.HasValue) Effect(op, batch, from.Value, -amount); if (to.HasValue) Effect(op, batch, to.Value, amount);
        return op;
    }
    private JournalOperation Compensate(JournalBatch batch, JournalOperation original)
    {
        string reason = batch.CommandType == 2 ? "Synthetic correction" : "Synthetic reversal";
        var op = new JournalOperation(Guid.NewGuid(), Owner, batch.Id, 5, currency, null, null, null, original.Id, original.Kind, reason); Operations.Add(op);
        foreach (var old in Effects.Where(x => x.OperationId == original.Id).ToArray()) Effect(op, batch, old.AccountId, -old.SignedAmount);
        return op;
    }
    private void Effect(JournalOperation op, JournalBatch batch, Guid account, decimal value)
    {
        Effects.Add(new(op.Id, Owner, account, currency, batch.Id, batch.Sequence, batch.RecordedAtUtc, batch.EffectiveDate, op.Kind, value));
        balances[account] = balances.GetValueOrDefault(account) + value;
    }
    private void Finish<T>(JournalBatch batch, T receipt, string canonical, object summary)
    {
        var outcome = new JournalOutcome(Guid.NewGuid(), Owner, batch.CommandType, "intent-" + batch.Id.ToString("N"), Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))), 1, 1, batch.Id,
            batch.CommandType switch { 1 => "opening.initialized", 2 => "opening.corrected", _ => Name(batch.CommandType) + ".posted" }, JsonSerializer.Serialize(receipt, Json), batch.RecordedAtUtc);
        Outcomes.Add(outcome); Audits.Add(new(Guid.NewGuid(), Owner, outcome.Id, batch.RecordedAtUtc, $"financial.{Name(batch.CommandType)}.posted", JsonSerializer.Serialize(summary, Json)));
    }
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Tail(string? value) => value is null ? "-1:" : value.Length + ":" + value;
    private static string Name(int command) => command switch { 1 => "opening", 2 => "opening_correction", 3 => "income", 4 => "expense", 5 => "transfer", _ => "reversal" };
}
