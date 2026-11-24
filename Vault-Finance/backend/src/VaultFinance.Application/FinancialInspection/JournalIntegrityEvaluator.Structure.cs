namespace VaultFinance.Application.FinancialInspection;

public sealed partial class JournalIntegrityEvaluator
{
    private sealed partial class Evaluation
    {
        private void OpeningsAndBatches()
        {
            var versionContents = new Dictionary<Guid, HashSet<Guid>>();
            foreach (var duplicate in data.Openings.GroupBy(x => (x.AccountId, x.Currency, x.Revision)).Where(x => x.Count() > 1))
                Add("opening.duplicate_revision", account: duplicate.Key.AccountId, revision: duplicate.Key.Revision);
            foreach (var group in data.Openings.GroupBy(x => x.AccountId))
            {
                var chain = group.OrderBy(x => x.Revision).ToArray();
                foreach (var fork in chain.Where(x => x.PreviousRevision.HasValue).GroupBy(x => x.PreviousRevision).Where(x => x.Count() > 1))
                    Add("opening.fork", account: group.Key, revision: fork.Key);
                for (int index = 0; index < chain.Length; index++)
                {
                    var version = chain[index]; var contents = new HashSet<Guid>();
                    if (!accounts.TryGetValue(version.AccountId, out var account) || account.Currency != version.Currency ||
                        !Reason(version.Reason, true) || version.Revision != (long)index + 1 ||
                        version.PreviousRevision != (version.Revision == 1 ? null : (int?)(version.Revision - 1)))
                        Add("opening.chain_invalid", account: version.AccountId, batch: version.BatchId, revision: version.Revision);
                    JournalOperation? replacement = null;
                    if (version.OpeningOperationId is Guid id)
                    {
                        contents.Add(id);
                        if (!operations.TryGetValue(id, out replacement) || replacement.Kind != 1 || replacement.Currency != version.Currency ||
                            replacement.DestinationAccountId != version.AccountId || replacement.BatchId != version.BatchId || version.OpeningOperationKind != 1 || !Magnitude(replacement.Amount, out _))
                            Add("opening.reference_invalid", account: version.AccountId, operation: id, revision: version.Revision);
                    }
                    else if (version.OpeningOperationKind is not null) Add("opening.reference_invalid", account: version.AccountId, revision: version.Revision);

                    if (!batches.TryGetValue(version.BatchId, out var batch) || batch.CommandType != (version.Revision == 1 ? 1 : 2))
                        Add("opening.batch_invalid", account: version.AccountId, batch: version.BatchId);
                    if (index > 0)
                    {
                        var previous = chain[index - 1];
                        decimal previousAmount = 0m;
                        if (previous.OpeningOperationId is Guid oldId)
                        {
                            if (operations.TryGetValue(oldId, out var old)) previousAmount = old.Amount ?? 0m;
                            var compensation = data.Operations.Where(x => x.OriginalOperationId == oldId).ToArray();
                            if (compensation.Length != 1 || compensation[0].BatchId != version.BatchId || compensation[0].Kind != 5 || compensation[0].OriginalOperationKind != 1)
                                Add("opening.compensation_invalid", account: version.AccountId, batch: version.BatchId, operation: oldId);
                            foreach (var row in compensation.Where(x => x.BatchId == version.BatchId)) contents.Add(row.Id);
                        }
                        if (previousAmount == (replacement?.Amount ?? 0m)) Add("opening.unchanged_revision", account: version.AccountId, revision: version.Revision);
                        if (batches.TryGetValue(previous.BatchId, out var previousBatch) && batch is not null && batch.Sequence <= previousBatch.Sequence)
                            Add("opening.causality_invalid", account: version.AccountId, revision: version.Revision);
                    }
                    if (!versionContents.TryAdd(version.BatchId, contents)) Add("opening.batch_reused", account: version.AccountId, batch: version.BatchId);
                }
                if (chain.LastOrDefault()?.OpeningOperationId is Guid current && data.Operations.Any(x => x.OriginalOperationId == current))
                    Add("opening.current_compensated", account: group.Key, operation: current);
            }

            foreach (var opening in data.Operations.Where(x => x.Kind == 1))
                if (data.Openings.Count(x => x.OpeningOperationId == opening.Id) != 1) Add("opening.operation_unversioned", operation: opening.Id);
            foreach (var batch in data.Batches)
            {
                var rows = data.Operations.Where(x => x.BatchId == batch.Id).ToArray();
                var versions = data.Openings.Where(x => x.BatchId == batch.Id).ToArray();
                bool valid = batch.CommandType switch
                {
                    1 or 2 => versions.Length == 1 && versionContents.TryGetValue(batch.Id, out var expected) && expected.SetEquals(rows.Select(x => x.Id)) && rows.Length == expected.Count,
                    3 => versions.Length == 0 && rows.Length == 1 && rows[0].Kind == 2,
                    4 => versions.Length == 0 && rows.Length == 1 && rows[0].Kind == 3,
                    5 => versions.Length == 0 && rows.Length == 1 && rows[0].Kind == 4,
                    6 => versions.Length == 0 && rows.Length == 1 && rows[0].Kind == 5 && rows[0].OriginalOperationKind is 2 or 3 or 4,
                    _ => false
                };
                if (!valid) Add("batch.composition_invalid", batch: batch.Id);
                if (data.Outcomes.Count(x => x.BatchId == batch.Id && x.OutcomeKind == 1 && x.CommandType == batch.CommandType) != 1)
                    Add("batch.outcome_missing_or_duplicate", batch: batch.Id);
            }
        }
    }
}
