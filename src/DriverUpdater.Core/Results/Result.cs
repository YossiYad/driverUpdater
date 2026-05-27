using System.Diagnostics.CodeAnalysis;

namespace DriverUpdater.Core.Results;

public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly ResultError? _error;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value on a failed Result.");

    public ResultError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Cannot access Error on a successful Result.");

    private Result(T value)
    {
        _value = value;
        _error = null;
        IsSuccess = true;
    }

    private Result(ResultError error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }

    public static Result<T> Failure(string code, string message) =>
        new(ResultError.From(code, message));

    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsSuccess;
    }

    public Result<TOut> Map<TOut>(Func<T, TOut> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return IsSuccess
            ? Result<TOut>.Success(projection(_value!))
            : Result<TOut>.Failure(_error!);
    }

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsSuccess
            ? next(_value!)
            : Result<TOut>.Failure(_error!);
    }

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<ResultError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(_error!);
    }

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(ResultError error) => Failure(error);
}
