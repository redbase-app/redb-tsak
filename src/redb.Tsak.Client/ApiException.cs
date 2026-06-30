using redb.Tsak.Contracts;

namespace redb.Tsak.Client;

/// <summary>
/// Base exception thrown when the Tsak API returns a non-success status code.
/// Existing <c>catch (ApiException)</c> sites continue to work — typed subclasses below
/// (<see cref="NotFoundException"/>, <see cref="ForbiddenException"/>, etc.) extend this type
/// for HTTP-status-driven control flow without breaking callers.
/// </summary>
public class ApiException : Exception
{
    /// <summary>HTTP status code returned by the API.</summary>
    public int StatusCode { get; }

    /// <summary>Parsed error response body, if available.</summary>
    public ApiErrorResponse? ErrorResponse { get; }

    /// <summary>
    /// Initializes a new API exception.
    /// </summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="message">Error message.</param>
    /// <param name="errorResponse">Parsed error response, if available.</param>
    public ApiException(int statusCode, string message, ApiErrorResponse? errorResponse = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorResponse = errorResponse;
    }

    /// <summary>
    /// Creates a typed <see cref="ApiException"/> subclass matching the HTTP status code.
    /// Falls back to the base <see cref="ApiException"/> for status codes without a dedicated type.
    /// </summary>
    public static ApiException Create(int statusCode, string message, ApiErrorResponse? errorResponse = null)
        => statusCode switch
        {
            400 => new BadRequestException(message, errorResponse),
            401 => new UnauthorizedException(message, errorResponse),
            403 => new ForbiddenException(message, errorResponse),
            404 => new NotFoundException(message, errorResponse),
            409 => new ConflictException(message, errorResponse),
            429 => new ThrottledException(message, errorResponse),
            _ => new ApiException(statusCode, message, errorResponse)
        };
}

/// <summary>HTTP 400 Bad Request — request payload was rejected by the API.</summary>
public sealed class BadRequestException : ApiException
{
    /// <summary>Initializes a new instance.</summary>
    public BadRequestException(string message, ApiErrorResponse? errorResponse = null)
        : base(400, message, errorResponse) { }
}

/// <summary>HTTP 401 Unauthorized — credentials missing or invalid.</summary>
public sealed class UnauthorizedException : ApiException
{
    /// <summary>Initializes a new instance.</summary>
    public UnauthorizedException(string message, ApiErrorResponse? errorResponse = null)
        : base(401, message, errorResponse) { }
}

/// <summary>HTTP 403 Forbidden — caller authenticated but lacks permission.</summary>
public sealed class ForbiddenException : ApiException
{
    /// <summary>Initializes a new instance.</summary>
    public ForbiddenException(string message, ApiErrorResponse? errorResponse = null)
        : base(403, message, errorResponse) { }
}

/// <summary>HTTP 404 Not Found — resource does not exist.</summary>
public sealed class NotFoundException : ApiException
{
    /// <summary>Initializes a new instance.</summary>
    public NotFoundException(string message, ApiErrorResponse? errorResponse = null)
        : base(404, message, errorResponse) { }
}

/// <summary>HTTP 409 Conflict — request conflicts with current resource state.</summary>
public sealed class ConflictException : ApiException
{
    /// <summary>Initializes a new instance.</summary>
    public ConflictException(string message, ApiErrorResponse? errorResponse = null)
        : base(409, message, errorResponse) { }
}

/// <summary>HTTP 429 Too Many Requests — caller exceeded a rate limit (e.g. AuthThrottle).</summary>
public sealed class ThrottledException : ApiException
{
    /// <summary>Initializes a new instance.</summary>
    public ThrottledException(string message, ApiErrorResponse? errorResponse = null)
        : base(429, message, errorResponse) { }
}
