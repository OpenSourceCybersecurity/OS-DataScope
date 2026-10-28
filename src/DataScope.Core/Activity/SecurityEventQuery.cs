using System.Globalization;

namespace DataScope.Core.Activity;

/// <summary>The event log XPath query that asks a file server only for collected events, so it filters them itself.</summary>
public static class SecurityEventQuery
{
    /// <summary>Collected events with a record id above <paramref name="afterRecordId"/>. Built from numbers only.</summary>
    public static string For(long? afterRecordId)
    {
        var ids = string.Join(" or ", ActivityInterpreter.CollectedEventIds.Select(id => string.Create(CultureInfo.InvariantCulture, $"EventID={id}")));
        var after = afterRecordId is { } a ? string.Create(CultureInfo.InvariantCulture, $" and EventRecordID>{a}") : "";
        return $"*[System[({ids}){after}]]";
    }
}
