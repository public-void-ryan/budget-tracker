namespace BudgetTracker.Api.Validation;

/// <summary>
/// What a stored transaction amount may be. Shared by manual entry and CSV import so the two
/// can never disagree: numeric(19,4) allows 15 digits before the decimal point and 4 after.
/// </summary>
public static class MoneyRules
{
    public const decimal MaxAmount = 999_999_999_999_999.9999m;
    public const int Scale = 4;

    /// <summary>The problem with an amount, or null if it is acceptable.</summary>
    public static (string Code, string Message)? Check(decimal amount)
    {
        if (amount == 0m)
        {
            return ("amount.zero", "Amount must not be zero.");
        }

        if (Math.Abs(amount) > MaxAmount)
        {
            return ("amount.out_of_range", "Amount is too large.");
        }

        // Refused rather than rounded: silently changing a money amount is never right.
        if (decimal.Round(amount, Scale) != amount)
        {
            return ("amount.too_precise", $"Amount may have at most {Scale} decimal places.");
        }

        return null;
    }
}
