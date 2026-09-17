using System.Net;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class ConnectionErrorMessageTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Access token")]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    [InlineData(HttpStatusCode.NotFound, "address")]
    [InlineData(HttpStatusCode.BadGateway, "server")]
    [InlineData(HttpStatusCode.TooManyRequests, "Wait")]
    [InlineData(HttpStatusCode.Redirect, "direct Home Assistant address")]
    public void HttpFailuresExplainTheRecoveryWithoutExposingResponseDetails(HttpStatusCode status, string expected)
    {
        var error = new HttpRequestException("private-token-and-server-response", null, status);
        var message = ConnectionErrorMessage.Describe(error);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("private-token", message);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "server name")]
    [InlineData(HttpRequestError.SecureConnectionError, "certificate")]
    [InlineData(HttpRequestError.ConnectionError, "network")]
    public void NetworkFailuresAreDistinctFromAuthentication(HttpRequestError reason, string expected)
    {
        var message = ConnectionErrorMessage.Describe(new HttpRequestException(reason));
        Assert.Contains(expected, message);
        Assert.DoesNotContain("access token", message);
    }

    [Fact]
    public void UnexpectedErrorsDoNotDisplayInternalDetails()
    {
        var message = ConnectionErrorMessage.Describe(new Exception("private-file-path"));
        Assert.DoesNotContain("private-file-path", message);
        Assert.Contains("diagnostic logs", message);
    }
}
