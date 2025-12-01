namespace RecipeSearchWeb.Domain.Common;

/// <summary>
/// Generic Result pattern for handling success/failure scenarios without exceptions.
/// Used for operations that can fail in a predictable way.
/// </summary>
/// <typeparam name="T">Type of the successful result value</typeparam>
public class Result<T>
{
    public T? Value { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? Error { get; }
    public string? ErrorCode { get; }

    protected Result(T? value, bool isSuccess, string? error, string? errorCode = null)
    {
        Value = value;
        IsSuccess = isSuccess;
        Error = error;
        ErrorCode = errorCode;
    }

    public static Result<T> Success(T value) => new(value, true, null);
    public static Result<T> Failure(string error, string? errorCode = null) => new(default, false, error, errorCode);

    /// <summary>
    /// Map the result to another type if successful
    /// </summary>
    public Result<TNew> Map<TNew>(Func<T, TNew> mapper)
    {
        return IsSuccess 
            ? Result<TNew>.Success(mapper(Value!)) 
            : Result<TNew>.Failure(Error!, ErrorCode);
    }

    /// <summary>
    /// Execute an action if the result is successful
    /// </summary>
    public Result<T> OnSuccess(Action<T> action)
    {
        if (IsSuccess && Value is not null)
            action(Value);
        return this;
    }

    /// <summary>
    /// Execute an action if the result is a failure
    /// </summary>
    public Result<T> OnFailure(Action<string> action)
    {
        if (IsFailure && Error is not null)
            action(Error);
        return this;
    }

    /// <summary>
    /// Match the result to a function based on success or failure
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<string, TResult> onFailure)
    {
        return IsSuccess ? onSuccess(Value!) : onFailure(Error!);
    }
}

/// <summary>
/// Non-generic Result for operations that don't return a value
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? Error { get; }
    public string? ErrorCode { get; }

    protected Result(bool isSuccess, string? error, string? errorCode = null)
    {
        IsSuccess = isSuccess;
        Error = error;
        ErrorCode = errorCode;
    }

    public static Result Success() => new(true, null);
    public static Result Failure(string error, string? errorCode = null) => new(false, error, errorCode);

    public Result OnSuccess(Action action)
    {
        if (IsSuccess)
            action();
        return this;
    }

    public Result OnFailure(Action<string> action)
    {
        if (IsFailure && Error is not null)
            action(Error);
        return this;
    }
}
