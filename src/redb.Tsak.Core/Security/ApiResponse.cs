using redb.Route.Abstractions;
using redb.Route.Controllers;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Unified API error/success response helpers for all Tsak controllers.
/// Uses <see cref="ControllerErrorResponse"/> from redb.Route.Core for consistency.
/// </summary>
public static class ApiResponse
{
    private const string StatusCodeHeader = "status.code";
    private const string ContentTypeHeader = "Content-Type";
    private const string JsonContentType = "application/json";

    /// <summary>Write a success response to exchange.Out.</summary>
    public static void Ok(IExchange exchange, object? body = null)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = body;
        exchange.Out.Headers[StatusCodeHeader] = 200;
        exchange.Out.Headers[ContentTypeHeader] = JsonContentType;
    }

    /// <summary>Write a 201 Created response.</summary>
    public static void Created(IExchange exchange, object? body = null)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = body;
        exchange.Out.Headers[StatusCodeHeader] = 201;
        exchange.Out.Headers[ContentTypeHeader] = JsonContentType;
    }

    /// <summary>Write a 204 No Content response.</summary>
    public static void NoContent(IExchange exchange)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = null;
        exchange.Out.Headers[StatusCodeHeader] = 204;
    }

    /// <summary>Write an error response using the standard ControllerErrorResponse format.</summary>
    public static void Error(IExchange exchange, int statusCode, string error, string message)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = new ControllerErrorResponse
        {
            Error = error,
            Message = message,
            StatusCode = statusCode
        };
        exchange.Out.Headers[StatusCodeHeader] = statusCode;
        exchange.Out.Headers[ContentTypeHeader] = JsonContentType;
    }

    /// <summary>400 Bad Request.</summary>
    public static void BadRequest(IExchange exchange, string message)
        => Error(exchange, 400, "BadRequest", message);

    /// <summary>401 Unauthorized.</summary>
    public static void Unauthorized(IExchange exchange, string message = "Unauthorized")
        => Error(exchange, 401, "Unauthorized", message);

    /// <summary>403 Forbidden.</summary>
    public static void Forbidden(IExchange exchange, string message = "Forbidden")
        => Error(exchange, 403, "Forbidden", message);

    /// <summary>404 Not Found.</summary>
    public static void NotFound(IExchange exchange, string message)
        => Error(exchange, 404, "NotFound", message);

    /// <summary>429 Too Many Requests.</summary>
    public static void TooManyRequests(IExchange exchange, string message = "Too many requests")
        => Error(exchange, 429, "TooManyRequests", message);

    /// <summary>409 Conflict.</summary>
    public static void Conflict(IExchange exchange, string message)
        => Error(exchange, 409, "Conflict", message);

    /// <summary>500 Internal Server Error.</summary>
    public static void InternalError(IExchange exchange, string message = "Internal server error")
        => Error(exchange, 500, "InternalError", message);

    /// <summary>503 Service Unavailable.</summary>
    public static void ServiceUnavailable(IExchange exchange, string message = "Service unavailable")
        => Error(exchange, 503, "ServiceUnavailable", message);
}
