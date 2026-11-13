using ft.IO.Files;
using System.Net;
using System.Net.Http.Headers;

namespace ft_tests
{
    /// <summary>
    /// Dropbox's token refresh: a server error, a 429 or a request that never reached Dropbox is tried again, up
    /// to three times, and a refusal fails at once. The client sends to an in-memory server that answers the token
    /// requests as each test scripts them.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class DropboxTokenTests
    {
        [TestMethod]
        public void RetriesServerErrorsAndRateLimits()
        {
            var server = new TokenServer(
                Answer(HttpStatusCode.InternalServerError),    // no Retry-After: waits 1 s
                Answer(HttpStatusCode.TooManyRequests, retryAfterSeconds: 0),
                Answer(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 0),
                Token());

            var dropbox = new Dropbox("app-key", "app-secret", "refresh-token", messageHandler: server);

            Assert.AreEqual(4, server.TokenRequests);
            Assert.IsFalse(dropbox.Exists("1.dat"));
            CollectionAssert.AreEqual(new[] { "Bearer token" }, server.ApiAuthorizations);
        }

        [TestMethod]
        public void RetriesWhenDropboxCannotBeReached()
        {
            var server = new TokenServer(Unreachable(), Token());

            var dropbox = new Dropbox("app-key", "app-secret", "refresh-token", messageHandler: server);

            Assert.AreEqual(2, server.TokenRequests);
            Assert.IsFalse(dropbox.Exists("1.dat"));
        }

        [TestMethod]
        public void GivesUpAfterThreeRetries()
        {
            var server = new TokenServer(
                Answer(HttpStatusCode.InternalServerError, retryAfterSeconds: 0),
                Answer(HttpStatusCode.BadGateway, retryAfterSeconds: 0),
                Answer(HttpStatusCode.InternalServerError, retryAfterSeconds: 0),
                Answer(HttpStatusCode.InternalServerError, retryAfterSeconds: 0, body: "The server has either erred"));

            var ex = Assert.ThrowsExactly<Exception>(() => new Dropbox("app-key", "app-secret", "refresh-token", messageHandler: server));

            Assert.AreEqual(4, server.TokenRequests);
            StringAssert.Contains(ex.Message, "(500) 4 times in a row");
            StringAssert.Contains(ex.Message, "The server has either erred");
        }

        [TestMethod]
        [DataRow(HttpStatusCode.BadRequest, """{"error": "invalid_grant"}""")]
        [DataRow(HttpStatusCode.Unauthorized, """{"error": "invalid_client"}""")]
        public void DoesNotRetryARefusal(HttpStatusCode status, string body)
        {
            var server = new TokenServer(Answer(status, body: body), Token());

            var ex = Assert.ThrowsExactly<Exception>(() => new Dropbox("app-key", "app-secret", "refresh-token", messageHandler: server));

            Assert.AreEqual(1, server.TokenRequests);
            StringAssert.Contains(ex.Message, "Check the app key, app secret and refresh token");
            StringAssert.Contains(ex.Message, body);
        }

        static Func<HttpResponseMessage> Answer(HttpStatusCode status, int? retryAfterSeconds = null, string body = "") => () =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfterSeconds is int seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }
            return response;
        };

        static Func<HttpResponseMessage> Token() =>
            Answer(HttpStatusCode.OK, body: """{"access_token":"token","expires_in":14400}""");

        static Func<HttpResponseMessage> Unreachable() =>
            () => throw new HttpRequestException("No such host is known. (api.dropboxapi.com:443)");

        // Answers each token request with the next scripted answer, and every API request with "not found". Keeps
        // the Authorization header of each API request.
        sealed class TokenServer(params Func<HttpResponseMessage>[] tokenAnswers) : HttpMessageHandler
        {
            public int TokenRequests { get; private set; }
            public List<string> ApiAuthorizations { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(Send(request, cancellationToken));

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath == "/oauth2/token")
                {
                    if (TokenRequests == tokenAnswers.Length)
                    {
                        throw new AssertFailedException($"Token request {TokenRequests + 1}, but the test scripted only {tokenAnswers.Length}.");
                    }
                    return tokenAnswers[TokenRequests++]();
                }

                ApiAuthorizations.Add(request.Headers.Authorization?.ToString() ?? "");
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"error_summary":"path/not_found/"}""") };
            }
        }
    }
}
