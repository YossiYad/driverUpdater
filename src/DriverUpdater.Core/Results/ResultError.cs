namespace DriverUpdater.Core.Results;

public sealed record ResultError(string Code, string Message, Exception? Cause = null)
{
    public static ResultError From(string code, string message) => new(code, message);

    public static ResultError From(string code, Exception cause) =>
        new(code, cause.Message, cause);

    public override string ToString() => $"{Code}: {Message}";
}
