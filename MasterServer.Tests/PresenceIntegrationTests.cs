using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MasterServer.Chat;
using MasterServer.Data;
using MasterServer.DTOs;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasterServer.Tests;

public sealed class PresenceIntegrationTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly List<HubConnection> _connections = new();

    public PresenceIntegrationTests()
    {
        var databaseName = $"presence-{Guid.NewGuid():N}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Deployment:Profile"] = "development",
                    ["Auth:Mode"] = "development-guest"
                }));
            builder.ConfigureServices(services =>
            {
                services.Remove(services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)));
                services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Anonymous_request_returns_only_an_integer_count_even_without_a_database()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Deployment:Profile"] = "development",
                    ["Auth:Mode"] = "development-guest",
                    ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1"
                })));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/presence");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var property = Assert.Single(json.EnumerateObject());
        Assert.Equal("onlinePlayerCount", property.Name);
        Assert.Equal(0, property.Value.GetInt32());
    }

    [Fact]
    public async Task Count_tracks_connected_identities_deduplicates_connections_and_excludes_offline_accounts()
    {
        var alice = await RegisterAsync();
        var bob = await RegisterAsync();
        await RegisterAsync(); // A valid application session alone is not online.
        Assert.Equal(0, await CountAsync());

        var first = await ConnectAsync(alice.Token);
        Assert.Equal(1, await CountAsync());
        var second = await ConnectAsync(alice.Token);
        Assert.Equal(1, await CountAsync());
        var observer = await ConnectAsync(bob.Token);
        Assert.Equal(2, await CountAsync());

        await first.StopAsync();
        Assert.Equal(2, await CountAsync());

        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = observer.On<ChatPresence>("ChatPresenceChanged", presence =>
        {
            if (presence.Player.PlayerId == alice.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && !presence.Online)
                offline.TrySetResult();
        });
        await second.StopAsync();
        // StopAsync alone does not acknowledge server-side disconnect processing.
        await offline.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await CountAsync());

        await ConnectAsync(alice.Token);
        Assert.Equal(2, await CountAsync());
    }

    [Theory]
    [InlineData("https://playsloparena.com", true)]
    [InlineData("https://www.playsloparena.com", true)]
    [InlineData("https://sloparena.barakaslurp.fr", true)]
    [InlineData("https://abc123.playsloparena.pages.dev", true)]
    [InlineData("https://feature-name.playsloparena.pages.dev", true)]
    [InlineData("https://example.com", false)]
    [InlineData("http://playsloparena.com", false)]
    [InlineData("http://preview.playsloparena.pages.dev", false)]
    [InlineData("https://preview.playsloparena.pages.dev:444", false)]
    [InlineData("https://playsloparena.com.evil.example", false)]
    [InlineData("https://preview.playsloparena.pages.dev.evil.example", false)]
    [InlineData("https://other.pages.dev", false)]
    public async Task Cors_allows_only_website_and_preview_origins(string origin, bool allowed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/presence");
        request.Headers.Add("Origin", origin);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (allowed)
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        else
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Cors_is_scoped_to_presence_and_preflight_allows_only_get()
    {
        using var health = new HttpRequestMessage(HttpMethod.Get, "/health");
        health.Headers.Add("Origin", "https://playsloparena.com");
        using var healthResponse = await _client.SendAsync(health);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.False(healthResponse.Headers.Contains("Access-Control-Allow-Origin"));

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/presence");
        preflight.Headers.Add("Origin", "https://playsloparena.com");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var preflightResponse = await _client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal("https://playsloparena.com",
            Assert.Single(preflightResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("GET", Assert.Single(preflightResponse.Headers.GetValues("Access-Control-Allow-Methods")));

        using var postResponse = await _client.PostAsJsonAsync("/presence", new { });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);
    }

    private async Task<int> CountAsync()
    {
        var json = await _client.GetFromJsonAsync<JsonElement>("/presence");
        return json.GetProperty("onlinePlayerCount").GetInt32();
    }

    private async Task<GuestAuthResponse> RegisterAsync()
    {
        using var response = await _client.PostAsJsonAsync("/auth/guest", new { });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
    }

    private async Task<HubConnection> ConnectAsync(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _connections.Add(connection);
        await connection.StartAsync();
        await connection.InvokeAsync<ChatSnapshot>("GetChatState");
        return connection;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }
}
