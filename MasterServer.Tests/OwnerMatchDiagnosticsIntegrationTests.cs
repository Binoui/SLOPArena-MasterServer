using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MasterServer.Configuration;
using MasterServer.Data;
using MasterServer.Data.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasterServer.Tests;

public class OwnerMatchDiagnosticsIntegrationTests
{
    private const string OwnerKey = "owner-match-diagnostics-key-0123456789abcdef";
    private const string ControlKey = "private-gamehost-control-key-0123456789abcdef";
    private readonly Guid _matchId = Guid.NewGuid();
    private readonly Guid _hostId = Guid.NewGuid();

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public Uri? Uri;
        public string? Authorization;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public object? Body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = JsonContent.Create(Body) });
        }
    }

    private WebApplicationFactory<Program> Factory(Handler handler, string? key = OwnerKey)
    {
        string database = $"diagnostics-{Guid.NewGuid()}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Deployment:Profile"] = "development", ["Auth:Mode"] = "steam",
                    ["Steam:ApiKey"] = "local-test-key", ["Steam:AppId"] = "5325920",
                    ["Steam:Identity"] = "sloparena-playtest",
                    ["Jwt:Secret"] = "diagnostics-jwt-test-key-0123456789abcdef",
                    ["Console:Key"] = key, ["Room:CatalogHash"] = new string('a', 64),
                    ["Room:AdmittedCharacters:0"] = "Manki", ["Room:AdmittedArenas:0"] = "slop_court"
                }));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database));
                services.AddSingleton(new MasterDeploymentOptions("vps", _hostId,
                    ControlUrl: new Uri("http://game:7777/match/start"), MatchControlKey: ControlKey));
                services.AddHttpClient("OwnerMatchDiagnosticsReader").ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
    }

    private object Payload(Guid? match = null) => new
    {
        matchId = (match ?? _matchId).ToString("D"), arena = "slop_court", transport = "steam-p2p",
        capturedAtUtc = DateTimeOffset.UtcNow, startedAtUtc = DateTimeOffset.UtcNow,
        endedAtUtc = DateTimeOffset.UtcNow, serverTick = 60, tickSamples = 60,
        meanTickMilliseconds = 2d, maxTickMilliseconds = 5d, maxScheduleDelayMilliseconds = 1d,
        slowTicks = 0, scheduleResets = 0, eventLimit = 256, droppedEvents = 0,
        peers = Array.Empty<object>(),
        events = new[] { new { sequence = 1, atUtc = DateTimeOffset.UtcNow, tick = 60, level = "info", message = "GO!" } },
        privateUnknownField = ControlKey // Must never be forwarded by the typed projection.
    };

    private async Task SeedAsync(WebApplicationFactory<Program> factory, Guid? host = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Matches.Add(new Match { Id = _matchId, ServerId = host ?? _hostId, Player1SteamId = 1, Player2SteamId = 2 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RouteRequiresOwnerKeyAndReturnsSafeDiagnosticsForKnownProvisionedMatch()
    {
        var handler = new Handler { Body = Payload() };
        using var factory = Factory(handler);
        using var client = factory.CreateClient();
        await SeedAsync(factory);
        var route = $"/owner/matches/{_matchId:D}/diagnostics";
        using var anonymous = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, handler.Calls);
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal($"http://game:7777/match/diagnostics/{_matchId:D}", handler.Uri?.ToString());
        Assert.Equal($"Bearer {ControlKey}", handler.Authorization);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ControlKey, body);
        Assert.DoesNotContain("privateUnknownField", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("GO!", json.RootElement.GetProperty("events")[0].GetProperty("message").GetString());
        using var unknown = await client.GetAsync($"/owner/matches/{Guid.NewGuid():D}/diagnostics");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(404, 410)]
    [InlineData(401, 503)]
    [InlineData(302, 503)]
    [InlineData(500, 503)]
    public async Task UpstreamFailureDoesNotInventLogsOrExposeUpstreamErrors(int upstream, int expected)
    {
        var handler = new Handler { Status = (HttpStatusCode)upstream, Body = new { privateError = ControlKey } };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory);
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync($"/owner/matches/{_matchId:D}/diagnostics");
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.DoesNotContain(ControlKey, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WrongMatchPayloadAndUnprovisionedHostAreRejected()
    {
        var handler = new Handler { Body = Payload(Guid.NewGuid()) };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory);
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync($"/owner/matches/{_matchId:D}/diagnostics");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Matches.FindAsync(_matchId))!.ServerId = Guid.NewGuid();
        await db.SaveChangesAsync();
        using var otherHost = await client.GetAsync($"/owner/matches/{_matchId:D}/diagnostics");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, otherHost.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("events", "[null]")]
    [InlineData("peers", "[null]")]
    [InlineData("events", "null")]
    [InlineData("peers", "null")]
    public async Task MalformedCollectionsReturnControlledError(string field, string invalidJson)
    {
        var payload = JsonSerializer.SerializeToNode(Payload())!;
        payload[field] = System.Text.Json.Nodes.JsonNode.Parse(invalidJson);
        var handler = new Handler { Body = payload };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory);
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync($"/owner/matches/{_matchId:D}/diagnostics");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(ControlKey, await response.Content.ReadAsStringAsync());
    }
}
