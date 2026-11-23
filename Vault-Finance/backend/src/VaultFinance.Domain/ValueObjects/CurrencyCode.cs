using VaultFinance.Domain.Exceptions;

namespace VaultFinance.Domain.ValueObjects;

internal static class CurrencyCode
{
    private const string ValidationMessage =
        "Currency must be a supported three-letter ISO 4217 code (DOP or USD).";

    internal static string Normalize(string currency)
    {
        var code = currency?.Trim();

        if (code is null || code.Length != 3 || !code.All(char.IsAsciiLetter))
        {
            throw new DomainException(ValidationMessage);
        }

        code = code.ToUpperInvariant();

        if (code is not ("DOP" or "USD"))
        {
            throw new DomainException(ValidationMessage);
        }

        return code;
    }
}
