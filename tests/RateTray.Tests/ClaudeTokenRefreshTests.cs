using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Providers;

namespace RateTray.Tests;

public class ClaudeTokenRefreshTests
{
    public ClaudeTokenRefreshTests() => Loc.Use("en");

    [Fact]
    public async Task Manual_refresh_exchanges_a_valid_token_even_when_automatic_refresh_is_disabled()
    {
        using var credentials = new CredentialFile();
        var requests = 0;
        using var client = Client(async request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/oauth/token", request.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("refresh_token", (string?)body["grant_type"]);
            Assert.Equal("old-refresh", (string?)body["refresh_token"]);
            Assert.Equal("test-client", (string?)body["client_id"]);
            return JsonResponse("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":7200}""");
        });
        var options = credentials.Options();
        options.AutoRefreshToken = false;
        options.Enabled = false;
        options.UsageUrl = "http://insecure.example.com/usage";

        var result = await new ClaudeUsageProvider(options, client).RefreshTokenAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, requests);
        Assert.InRange(result.Auth!.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddMinutes(119), DateTimeOffset.UtcNow.AddMinutes(121));
        Assert.Equal("OAuth · test-plan", result.Auth.Detail);
        var saved = credentials.Read();
        Assert.Equal("new-access", (string?)saved["claudeAiOauth"]!["accessToken"]);
        Assert.Equal("new-refresh", (string?)saved["claudeAiOauth"]!["refreshToken"]);
        Assert.Equal("test-plan", (string?)saved["claudeAiOauth"]!["subscriptionType"]);
        Assert.Equal("keep", (string?)saved["other"]);
        Assert.False(File.Exists(credentials.Path + ".tmp"));
    }

    [Fact]
    public async Task Automatic_refresh_uses_the_same_exchange_before_polling_usage()
    {
        using var credentials = new CredentialFile(expired: true, subscriptionType: "enterprise");
        var requests = 0;
        using var client = Client(async request =>
        {
            requests++;
            if (request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal("old-refresh", (string?)body["refresh_token"]);
                return JsonResponse("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("new-access", request.Headers.Authorization.Parameter);
            return JsonResponse("""{"limits":[],"extra_usage":{"is_enabled":true,"utilization":77}}""");
        });
        var options = credentials.Options();
        options.AutoRefreshToken = true;

        var result = await new ClaudeUsageProvider(options, client).ReadAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, requests);
        Assert.Equal("Spend limit", Assert.Single(result.Readings).Label);
        Assert.Equal("OAuth · enterprise", result.Auth!.Detail);
        Assert.Equal("new-refresh", (string?)credentials.Read()["claudeAiOauth"]!["refreshToken"]);
    }

    [Fact]
    public async Task Poll_uses_the_subscription_from_credentials_to_label_extra_usage()
    {
        using var credentials = new CredentialFile(subscriptionType: "enterprise");
        using var client = Client(_ => Task.FromResult(JsonResponse(
            """{"limits":[],"extra_usage":{"is_enabled":true,"utilization":77}}""")));

        var result = await new ClaudeUsageProvider(credentials.Options(), client).ReadAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var reading = Assert.Single(result.Readings);
        Assert.Equal("claude.extra_usage", reading.Id);
        Assert.Equal("Spend limit", reading.Label);
        Assert.Equal(77, reading.Percent);
    }

    [Fact]
    public async Task Rejected_refresh_reports_status_without_changing_credentials_or_exposing_response()
    {
        using var credentials = new CredentialFile();
        var original = File.ReadAllText(credentials.Path);
        using var client = Client(_ => Task.FromResult(JsonResponse("""{"secret":"not-for-output"}""", HttpStatusCode.BadRequest)));

        var result = await new ClaudeUsageProvider(credentials.Options(), client).RefreshTokenAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 400", result.Error);
        Assert.DoesNotContain("not-for-output", result.Error);
        Assert.Equal(original, File.ReadAllText(credentials.Path));
    }

    [Fact]
    public async Task Insecure_token_endpoint_is_rejected_without_sending_credentials()
    {
        using var credentials = new CredentialFile();
        using var client = Client(_ => throw new InvalidOperationException("Request should not be sent"));
        var options = credentials.Options();
        options.TokenUrl = "http://example.com/v1/oauth/token";
        var original = File.ReadAllText(credentials.Path);

        var result = await new ClaudeUsageProvider(options, client).RefreshTokenAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not https", result.Error);
        Assert.Equal(original, File.ReadAllText(credentials.Path));
    }

    [Fact]
    public async Task Missing_refresh_token_is_reported_without_sending_a_request()
    {
        using var credentials = new CredentialFile(withRefreshToken: false);
        using var client = Client(_ => throw new InvalidOperationException("Request should not be sent"));

        var result = await new ClaudeUsageProvider(credentials.Options(), client).RefreshTokenAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no refresh token", result.Error);
    }

    [Fact]
    public async Task Invalid_response_does_not_change_credentials()
    {
        using var credentials = new CredentialFile();
        var original = File.ReadAllText(credentials.Path);
        using var client = Client(_ => Task.FromResult(JsonResponse("""{"expires_in":3600}""")));

        var result = await new ClaudeUsageProvider(credentials.Options(), client).RefreshTokenAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("invalid response", result.Error);
        Assert.Equal(original, File.ReadAllText(credentials.Path));
    }

    [Fact]
    public async Task An_expired_token_is_not_refreshed_during_a_normal_poll_when_disabled()
    {
        using var credentials = new CredentialFile(expired: true);
        using var client = Client(_ => throw new InvalidOperationException("Request should not be sent"));

        var result = await new ClaudeUsageProvider(credentials.Options(), client).ReadAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Token expired", result.Error);
    }

    [Fact]
    public async Task Refresh_uses_the_existing_refresh_token_when_the_response_does_not_rotate_it()
    {
        using var credentials = new CredentialFile();
        using var client = Client(_ => Task.FromResult(JsonResponse("""{"access_token":"new-access","expires_in":3600}""")));

        var result = await new ClaudeUsageProvider(credentials.Options(), client).RefreshTokenAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("old-refresh", (string?)credentials.Read()["claudeAiOauth"]!["refreshToken"]);
    }

    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send)) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }

    private sealed class CredentialFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ratetray-{Guid.NewGuid():N}.json");

        public CredentialFile(bool expired = false, bool withRefreshToken = true, string subscriptionType = "test-plan")
        {
            var expires = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 2).ToUnixTimeMilliseconds();
            var root = new JsonObject
            {
                ["other"] = "keep",
                ["claudeAiOauth"] = new JsonObject
                {
                    ["accessToken"] = "old-access",
                    ["refreshToken"] = withRefreshToken ? "old-refresh" : null,
                    ["expiresAt"] = expires,
                    ["subscriptionType"] = subscriptionType,
                },
            };
            File.WriteAllText(Path, root.ToJsonString());
        }

        public ClaudeOptions Options() => new()
        {
            CredentialsPath = Path,
            TokenUrl = "https://token.example.com/v1/oauth/token",
            UsageUrl = "https://usage.example.com/api/oauth/usage",
            ClientId = "test-client",
        };

        public JsonNode Read() => JsonNode.Parse(File.ReadAllText(Path))!;

        public void Dispose()
        {
            File.Delete(Path);
            File.Delete(Path + ".tmp");
        }
    }
}
