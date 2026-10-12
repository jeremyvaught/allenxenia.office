namespace AllenBuilding.Domain.Billing;

public readonly record struct Money
{
    public long MinorUnits { get; }
    public string Currency { get; }

    public Money(long minorUnits, string currency = "USD")
    {
        if (minorUnits < 0) throw new ArgumentOutOfRangeException(nameof(minorUnits));
        if (currency != "USD") throw new ArgumentException("Only USD is enabled.");
        MinorUnits = minorUnits; Currency = currency;
    }

    public Money Add(Money other)
    {
        if (Currency != other.Currency) throw new ArgumentException("Currencies must match.");
        return new Money(checked(MinorUnits + other.MinorUnits), Currency);
    }

    public bool IsBelowCardMinimum => MinorUnits is > 0 and < 50;
}
