using System.Net;
using CCMC.Application.Abstractions;

namespace CCMC.Infrastructure.Sync;

/// <summary>
/// Maps an HTTP response from POST /reception to a CloudReceptionOutcome,
/// mirroring the cloud's own documented response contract exactly
/// (context.md "Cloud Responsibilities" - classifyHttpResponse() design):
/// 201 is returned for BOTH "created" and "duplicate" (varying only the
/// response body's `outcome` field, never the status code) - reading the
/// status code alone would misreport a duplicate as a fresh create.
/// </summary>
public static class HttpResponseClassifier
{
    public static CloudReceptionOutcome Classify(HttpStatusCode statusCode, string? outcomeField)
    {
        if (statusCode == HttpStatusCode.Created)
        {
            return outcomeField == "duplicate" ? CloudReceptionOutcome.Duplicate : CloudReceptionOutcome.Created;
        }

        if (statusCode == HttpStatusCode.Unauthorized) return CloudReceptionOutcome.AuthRetryable;
        if (statusCode == HttpStatusCode.Conflict) return CloudReceptionOutcome.Conflict;
        if (statusCode == (HttpStatusCode)429 || (int)statusCode >= 500) return CloudReceptionOutcome.Retryable;

        return CloudReceptionOutcome.Terminal; // any other 4xx
    }
}
