using System.Net;
using System.Text;

namespace SSO_Auth.Tests.Helpers;

/// <summary>
/// Serves pre-canned responses keyed by URL for use in tests that need to stub HTTP calls.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;

    public MockHttpMessageHandler(Dictionary<string, string> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (_responses.TryGetValue(url, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
