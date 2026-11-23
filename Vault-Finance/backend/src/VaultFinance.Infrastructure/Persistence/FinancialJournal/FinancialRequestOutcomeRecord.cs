namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

/// <summary>Storage for a future final result. This type does not implement replay.</summary>
public sealed class FinancialRequestOutcomeRecord
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public FinancialCommandType CommandType { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string PayloadFingerprint { get; private set; }
    public int FingerprintVersion { get; private set; }
    public FinancialOutcomeKind OutcomeKind { get; private set; }
    public Guid? BatchId { get; private set; }
    public string ResultCode { get; private set; }
    public string ReceiptJson { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }

    public FinancialRequestOutcomeRecord(Guid id, Guid userId, FinancialCommandType commandType,
        string idempotencyKey, string payloadFingerprint, int fingerprintVersion,
        FinancialOutcomeKind outcomeKind, Guid? batchId, string resultCode, string receiptJson,
        DateTimeOffset recordedAtUtc)
    {
        Id = id; UserId = userId; CommandType = commandType; IdempotencyKey = idempotencyKey;
        PayloadFingerprint = payloadFingerprint; FingerprintVersion = fingerprintVersion;
        OutcomeKind = outcomeKind; BatchId = batchId; ResultCode = resultCode;
        ReceiptJson = receiptJson; RecordedAtUtc = recordedAtUtc;
    }

    internal void ValidateForStorage()
    {
        // Validate before varchar/char parameter conversion can replace or truncate input.
        if (string.IsNullOrEmpty(IdempotencyKey) || IdempotencyKey.Length > 128 ||
            IdempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidOperationException("An idempotency key must contain 1-128 supported ASCII characters.");
        }

        if (PayloadFingerprint is null || PayloadFingerprint.Length != 64 ||
            PayloadFingerprint.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
        {
            throw new InvalidOperationException("A stored SHA-256 fingerprint requires exactly 64 uppercase hexadecimal characters.");
        }
    }
}
