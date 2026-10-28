namespace DataScope.Api.Endpoints;

internal static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static (int Limit, int Offset) Clamp(int? limit, int? offset) =>
        (Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), Math.Max(offset ?? 0, 0));
}

public sealed record Page<T>(IReadOnlyList<T> Items, int Limit, int Offset, int Total);
