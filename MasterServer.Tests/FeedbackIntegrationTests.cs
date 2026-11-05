using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MasterServer.Data;
using MasterServer.Data.Models;
using MasterServer.Feedback;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasterServer.Tests;

public sealed class FeedbackIntegrationTests : IDisposable
{
    private const string Key = "feedback-owner-key-test-0123456789abcdef";
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly TestClock clock = new();
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/feedback-v1.json"));
    public FeedbackIntegrationTests()
    {
        var name = "feedback-" + Guid.NewGuid();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Profile"] = "development", ["Auth:Mode"] = "development-guest",
                ["Jwt:Secret"] = "feedback-jwt-secret-test-0123456789abcdef", ["Console:Key"] = Key,
                ["RateLimit:MaxRequestsPerWindow"] = "1000"
            }));
            builder.ConfigureServices(services =>
            {
                services.Remove(services.Single(s => s.ServiceType == typeof(DbContextOptions<AppDbContext>)));
                services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(name));
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        client = factory.CreateClient();
    }
    [Fact]
    public void Golden_fixture_is_byte_identical_and_parses()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/feedback-v1.json"));
        Assert.Equal("11b9a55df387d2e2529e4f59c610105a3ea9271bff522f8c206c5d0f4c6be316", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal("feel", FeedbackSubmission.Parse(bytes).Kind);
    }
    [Fact]
    public async Task Authentication_is_required()
    {
        using var response = await Send();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task Acceptance_binds_JWT_identity_and_repeat_returns_original_receipt_and_evidence()
    {
        var steamId = await Login();
        using var first = await Send(image: [255,216,255,1]);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var receipt = await first.Content.ReadAsStringAsync();
        using var repeat = await Send(message: "changed retry content");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(receipt, await repeat.Content.ReadAsStringAsync());
        var id = JsonNode.Parse(receipt)!["reportId"]!.GetValue<string>();
        using var detail = await Owner("/owner/feedback/" + id);
        var data = JsonNode.Parse(await detail.Content.ReadAsStringAsync())!;
        Assert.Equal(steamId, data["reporter"]!["steamId"]!.GetValue<string>());
        Assert.Equal("The landing feels late here", data["message"]!.GetValue<string>());
        Assert.Equal(4, data["screenshot"]!["bytes"]!.GetValue<int>());
        using var image = await Owner("/owner/feedback/" + id + "/screenshot");
        Assert.Equal(new byte[] {255,216,255,1}, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType!.MediaType);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.FeedbackReports.ToListAsync());
        Assert.Single(await db.FeedbackScreenshots.ToListAsync());
    }
    [Fact]
    public async Task Rolling_quota_rejects_new_reports_but_repeats_are_free_and_window_expires()
    {
        var reporter = long.Parse(await Login());
        var firstId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 20; i++) db.FeedbackReports.Add(new FeedbackReport
            {
                Id = Guid.NewGuid(), SubmissionId = i == 0 ? firstId : Guid.NewGuid(), ReporterUserId = reporter,
                ReceivedAtUtc = clock.GetUtcNow().UtcDateTime.AddHours(-23), Kind = "bug", ContextJson = "{}", AppVersion = "test"
            });
            await db.SaveChangesAsync();
        }
        using var repeat = await Send(id: firstId);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        using var limited = await Send(id: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("feedback-rate-limited", JsonNode.Parse(await limited.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromHours(1), limited.Headers.RetryAfter!.Delta);
        clock.Now = clock.Now.AddHours(1);
        using var after = await Send(id: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
    }
    [Theory]
    [InlineData("version", 422, "unsupported-feedback-version")]
    [InlineData("unknown", 400, "invalid-feedback")]
    [InlineData("nested", 400, "invalid-feedback")]
    [InlineData("identity", 400, "invalid-feedback")]
    [InlineData("jpeg", 415, "unsupported-image")]
    [InlineData("large", 413, "feedback-too-large")]
    [InlineData("metadata", 413, "feedback-too-large")]
    public async Task Invalid_submissions_return_fixed_codes(string fault, int status, string code)
    {
        await Login();
        var json = JsonNode.Parse(Fixture)!;
        if (fault == "version") json["schemaVersion"] = 2;
        if (fault == "unknown") json["unknown"] = "private text";
        if (fault == "nested") json["context"]!["unknown"] = true;
        if (fault == "identity") json["reporterUserId"] = 123;
        if (fault == "metadata") json["message"] = new string('x', 17000);
        using var response = await Send(json: json.ToJsonString(), image: fault == "jpeg" ? [1,2,3] : fault == "large" ? new byte[FeedbackSubmission.BodyLimit + 1] : null);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("/owner/feedback")]
    [InlineData("/owner/feedback/00000000-0000-0000-0000-000000000001")]
    [InlineData("/owner/feedback/00000000-0000-0000-0000-000000000001/screenshot")]
    public async Task All_owner_routes_require_owner_key_even_with_player_JWT(string route)
    {
        await Login();
        using var denied = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(denied.Headers.GetValues("X-Content-Type-Options")));
        using var owner = await Owner(route);
        Assert.Equal(route == "/owner/feedback" ? HttpStatusCode.OK : HttpStatusCode.NotFound, owner.StatusCode);
    }
    [Fact]
    public async Task List_pages_by_cursor_without_image_or_player_text()
    {
        await Login();
        for (var i = 0; i < 3; i++)
        {
            using var sent = await Send(id: Guid.NewGuid(), image: [255,216,255]);
            Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        }
        var ids = new HashSet<string>();
        var route = "/owner/feedback?limit=1";
        for (var i = 0; i < 3; i++)
        {
            using var page = await Owner(route);
            var text = await page.Content.ReadAsStringAsync();
            Assert.DoesNotContain("jpeg", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", text);
            Assert.DoesNotContain("bytes", text);
            var json = JsonNode.Parse(text)!;
            Assert.True(ids.Add(json["items"]![0]!["reportId"]!.GetValue<string>()));
            var cursor = json["nextCursor"]?.GetValue<string>();
            if (i == 2) Assert.Null(cursor);
            else Assert.NotNull(cursor);
            route = "/owner/feedback?limit=1&cursor=" + Uri.EscapeDataString(cursor ?? "");
        }
        using var invalid = await Owner("/owner/feedback?cursor=bad");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    [Fact]
    public async Task Feedback_has_its_own_ten_request_IP_budget()
    {
        await Login();
        for (var i = 0; i < 10; i++) { using var accepted = await Send(); Assert.True(accepted.IsSuccessStatusCode); }
        using var limited = await Send();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        using var control = await client.PostAsJsonAsync("/auth/guest", new { });
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
    }
    [Fact]
    public async Task Unknown_length_upload_is_capped_while_streaming()
    {
        await Login();
        using var body = new StreamingContent();
        using var response = await client.PostAsync("/feedback", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("feedback-too-large", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("frontend")]
    [InlineData("nul")]
    [InlineData("too-many-fighters")]
    public void Parser_rejects_invalid_closed_context(string fault)
    {
        var json = JsonNode.Parse(Fixture)!;
        if (fault == "frontend") json["context"]!["phase"] = "frontend";
        if (fault == "nul") json["message"] = "hello\0world";
        if (fault == "too-many-fighters")
        {
            var fighters = json["context"]!["match"]!["fighters"]!.AsArray();
            for (var i = 0; i < 3; i++) fighters.Add(fighters[0]!.DeepClone());
        }
        var text = json.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1");
        Assert.Equal(400, Assert.Throws<FeedbackError>(() => FeedbackSubmission.Parse(Encoding.UTF8.GetBytes(text))).Status);
    }
    private sealed class StreamingContent : HttpContent
    {
        public StreamingContent() => Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data")
        { Parameters = { new NameValueHeaderValue("boundary", "feedback") } };
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[8192];
            for (var written = 0; written <= FeedbackSubmission.BodyLimit; written += buffer.Length)
                await stream.WriteAsync(buffer);
        }
    }
    private async Task<string> Login()
    {
        using var response = await client.PostAsJsonAsync("/auth/guest", new { });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json["token"]!.GetValue<string>());
        return json["steamId"]!.ToString();
    }
    private Task<HttpResponseMessage> Send(string? json = null, byte[]? image = null, Guid? id = null, string? message = null)
    {
        var node = JsonNode.Parse(json ?? Fixture)!;
        if (id.HasValue) node["submissionId"] = id.Value.ToString();
        if (message is not null) node["message"] = message;
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"), "metadata");
        if (image is not null)
        {
            var part = new ByteArrayContent(image); part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            body.Add(part, "screenshot", "feedback.jpg");
        }
        return client.PostAsync("/feedback", body);
    }
    private Task<HttpResponseMessage> Owner(string route)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add("X-Console-Key", Key);
        return client.SendAsync(request);
    }
    public void Dispose() { client.Dispose(); factory.Dispose(); }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
