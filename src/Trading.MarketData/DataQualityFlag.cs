namespace Trading.MarketData;

public enum DataQualityIssue
{
    None = 0,
    Missing = 1,
    Duplicate = 2,
    Stale = 3,
    Late = 4,
    OutOfOrder = 5,
    Incomplete = 6,
    Derived = 7
}
