using System.Text.Json.Serialization;
using CCMC.Cloud.Application.Common;

namespace CCMC.Cloud.Api.Middleware;

/// <summary>
/// A consistent error envelope for every non-2xx response: { message,
/// conflictingFields?, existingTransactionId? }. This is deliberately NOT
/// strict RFC7807 ProblemDetails - it exactly matches the shape the Windows
/// client's own HttpCloudApiClient already parses for error bodies
/// (CCMC.Contracts.Dtos.ReceptionConflictResponseDto - verified directly:
/// TryDeserialize&lt;ReceptionConflictResponseDto&gt;(rawBody)?.Message is read
/// for EVERY non-success reception response, not just 409). Client
/// compatibility takes priority over textbook RFC7807 here (this session's
/// explicit instruction: "current Contracts/client expectations must be
/// reconciled with the technical design"). HTTP status codes still follow
/// standard REST conventions (this satisfies "use appropriate HTTP status
/// codes" even though the body itself isn't ProblemDetails-shaped).
///
/// Never leaks a stack trace or raw database error - unexpected exceptions
/// become a generic 500 with a fixed message; the real exception is only
/// ever logged server-side, never returned to the client.
/// </summary>
public sealed class ApiErrorResponse
{
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("conflictingFields")] public IReadOnlyList<string>? ConflictingFields { get; init; }
    [JsonPropertyName("existingTransactionId")] public int? ExistingTransactionId { get; init; }
}

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var statusCode = Classify(ex);

            if (statusCode == StatusCodes.Status500InternalServerError)
            {
                logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
            }
            else
            {
                logger.LogWarning(
                    "{StatusCode} for {Method} {Path}: {Message}", statusCode, context.Request.Method, context.Request.Path, ex.Message);
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = statusCode;

            var response = new ApiErrorResponse
            {
                // Never the real ex.Message for an unexpected 500 - no internal/database detail leaked.
                Message = statusCode == StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : ex.Message,
                ConflictingFields = (ex as IdempotencyConflictException)?.ConflictingFields,
                ExistingTransactionId = (ex as IdempotencyConflictException)?.ExistingId,
            };

            await context.Response.WriteAsJsonAsync(response);
        }
    }

    private static int Classify(Exception ex) => ex switch
    {
        ValidationException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
        CentreAccessDeniedException => StatusCodes.Status403Forbidden,
        NotFoundException => StatusCodes.Status404NotFound,
        IdempotencyConflictException => StatusCodes.Status409Conflict,
        BusinessConflictException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };
}
