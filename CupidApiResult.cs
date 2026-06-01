public sealed record ApiError(string Message);

public sealed class CupidApiResult<T>
{
    private CupidApiResult(int statusCode, T? value, string? error)
    {
        StatusCode = statusCode;
        Value = value;
        Error = error;
    }

    public int StatusCode { get; }

    public T? Value { get; }

    public string? Error { get; }

    public static CupidApiResult<T> Ok(T value) => new(StatusCodes.Status200OK, value, null);

    public static CupidApiResult<T> Created(T value) => new(StatusCodes.Status201Created, value, null);

    public static CupidApiResult<T> NoContent() => new(StatusCodes.Status204NoContent, default, null);

    public static CupidApiResult<T> BadRequest(string message) => new(StatusCodes.Status400BadRequest, default, message);

    public static CupidApiResult<T> NotFound(string message) => new(StatusCodes.Status404NotFound, default, message);

    public static CupidApiResult<T> Conflict(string message) => new(StatusCodes.Status409Conflict, default, message);

    public IResult ToHttpResult()
    {
        if (StatusCode == StatusCodes.Status204NoContent)
        {
            return Results.NoContent();
        }

        if (Error is not null)
        {
            return Results.Json(new ApiError(Error), statusCode: StatusCode);
        }

        return Results.Json(Value, statusCode: StatusCode);
    }
}
