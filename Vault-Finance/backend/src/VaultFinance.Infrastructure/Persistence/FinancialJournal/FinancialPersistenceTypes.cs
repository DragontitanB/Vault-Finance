namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

// Command scopes and durable outcomes are persistence protocol metadata, not new Domain workflows.
public enum FinancialCommandType
{
    InitializeOpening = 1,
    CorrectOpening = 2,
    Income = 3,
    Expense = 4,
    Transfer = 5,
    Reverse = 6
}

public enum FinancialOutcomeKind
{
    Posted = 1,
    Rejected = 2
}
