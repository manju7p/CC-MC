using System.Net;
using CCMC.Application.Abstractions;
using CCMC.Infrastructure.Sync;
using Xunit;

namespace CCMC.Tests.Sync;

public class HttpResponseClassifierTests
{
    [Fact]
    public void Classify_201WithCreatedOutcome_ReturnsCreated()
    {
        Assert.Equal(CloudReceptionOutcome.Created, HttpResponseClassifier.Classify(HttpStatusCode.Created, "created"));
    }

    [Fact]
    public void Classify_201WithDuplicateOutcome_ReturnsDuplicate_NotCreated()
    {
        // Regression: the cloud returns 201 for BOTH created and duplicate (context.md) -
        // classifying by status code alone would misreport a duplicate as fresh.
        Assert.Equal(CloudReceptionOutcome.Duplicate, HttpResponseClassifier.Classify(HttpStatusCode.Created, "duplicate"));
    }

    [Fact]
    public void Classify_201WithNoOutcomeField_DefaultsToCreated()
    {
        Assert.Equal(CloudReceptionOutcome.Created, HttpResponseClassifier.Classify(HttpStatusCode.Created, null));
    }

    [Fact]
    public void Classify_409_ReturnsConflict()
    {
        Assert.Equal(CloudReceptionOutcome.Conflict, HttpResponseClassifier.Classify(HttpStatusCode.Conflict, null));
    }

    [Fact]
    public void Classify_401_ReturnsAuthRetryable()
    {
        Assert.Equal(CloudReceptionOutcome.AuthRetryable, HttpResponseClassifier.Classify(HttpStatusCode.Unauthorized, null));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Classify_429Or5xx_ReturnsRetryable(int statusCode)
    {
        Assert.Equal(CloudReceptionOutcome.Retryable, HttpResponseClassifier.Classify((HttpStatusCode)statusCode, null));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    public void Classify_Other4xx_ReturnsTerminal(int statusCode)
    {
        Assert.Equal(CloudReceptionOutcome.Terminal, HttpResponseClassifier.Classify((HttpStatusCode)statusCode, null));
    }
}
