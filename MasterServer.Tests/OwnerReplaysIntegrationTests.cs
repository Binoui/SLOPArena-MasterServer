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

public class OwnerReplaysIntegrationTests
{
    // Low-entropy fake keys built at runtime, so secret scanning never flags them.
    private static readonly string OwnerKey = "owner-replays-test-key-" + new string('o', 24);
    private static readonly string ControlKey = "gamehost-replays-test-key-" + new string('c', 24);
    private readonly Guid _matchId = Guid.NewGuid();
    private readonly Guid _hostId = Guid.NewGuid();

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public Uri? Uri;
        public string? Authorization;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public object? Body;
        public Func<HttpContent>? Content;
        public bool Unavailable;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
            if (Unavailable) throw new HttpRequestException("private upstream failure");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = Content?.Invoke() ?? JsonContent.Create(Body) });
        }
    }

    private WebApplicationFactory<Program> Factory(Handler handler)
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
                    ["Console:Key"] = OwnerKey, ["Room:CatalogHash"] = new string('a', 64),
                    ["Room:AdmittedCharacters:0"] = "Manki", ["Room:AdmittedArenas:0"] = "slop_court"
                }));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database));
                services.AddSingleton(new MasterDeploymentOptions("vps", _hostId,
                    ControlUrl: new Uri("http://game:7777/match/start"), MatchControlKey: ControlKey));
                services.AddHttpClient("OwnerReplays").ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
    }

    private object Payload() => new { replays = new[] { new {
        matchId = _matchId.ToString("D"), bytes = 123L, complete = true,
        modifiedAtUtc = DateTimeOffset.UtcNow, privateUnknownField = ControlKey } } };

    private string Download => $"/owner/matches/{_matchId:D}/replay";
    private async Task SeedAsync(WebApplicationFactory<Program> factory, Guid? host = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Matches.Add(new Match { Id = _matchId, ServerId = host ?? _hostId, Player1SteamId = 1, Player2SteamId = 2 });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerKeyRequired(bool download)
    {
        var handler = new Handler();
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory);
        using var response = await client.GetAsync(download ? Download : "/owner/replays");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, handler.Calls);
        client.DefaultRequestHeaders.Add("X-Console-Key", "wrong-key");
        using var wrong = await client.GetAsync(download ? Download : "/owner/replays");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnknownMatchAndWrongHostDoNotCallGameHost()
    {
        var handler = new Handler();
        using var factory = Factory(handler); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var unknown = await client.GetAsync(Download);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await SeedAsync(factory, Guid.NewGuid());
        using var wrongHost = await client.GetAsync(Download);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, wrongHost.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ListUsesPrivateRouteAndSafeValidatedProjection()
    {
        var handler = new Handler { Body = Payload() };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync("/owner/replays");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("http://game:7777/match/replays", handler.Uri?.ToString());
        Assert.Equal($"Bearer {ControlKey}", handler.Authorization);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ControlKey, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(_matchId.ToString("D"), json.RootElement.GetProperty("replays")[0].GetProperty("matchId").GetString());
        Assert.Equal(123L, json.RootElement.GetProperty("replays")[0].GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task DownloadStreamsExactBytesAndAttachmentHeaders()
    {
        var bytes = new byte[150_123]; new Random(42).NextBytes(bytes);
        var handler = new Handler { Content = () => new ByteArrayContent(bytes) };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory);
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync(Download);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"attachment; filename=\"{_matchId:D}.slopreplay\"", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"http://game:7777/match/replays/{_matchId:D}", handler.Uri?.ToString());
        Assert.Equal($"Bearer {ControlKey}", handler.Authorization);
    }

    [Theory]
    [InlineData(404, 410)]
    [InlineData(409, 409)]
    [InlineData(401, 503)]
    [InlineData(302, 503)]
    [InlineData(500, 503)]
    public async Task DownloadMapsUpstreamStatus(int upstream, int expected)
    {
        var handler = new Handler { Status = (HttpStatusCode)upstream, Body = new { secret = ControlKey } };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory); client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync(Download);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.DoesNotContain(ControlKey, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableGameHostReturns503(bool download)
    {
        var handler = new Handler { Unavailable = true };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory); client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync(download ? Download : "/owner/replays");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private upstream", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"replays\":null}")]
    [InlineData("{\"replays\":[null]}")]
    [InlineData("{\"replays\":[{}]}")]
    public async Task InvalidListReturns502(string json)
    {
        var handler = new Handler { Content = () => new StringContent(json) };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync("/owner/replays");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("matchId", "\"AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA\"")]
    [InlineData("matchId", "\"../file\"")]
    [InlineData("bytes", "-1")]
    [InlineData("bytes", "64000001")]
    [InlineData("bytes", "1.5")]
    [InlineData("complete", "null")]
    [InlineData("modifiedAtUtc", "\"2026-11-02T12:00:00+02:00\"")]
    [InlineData("modifiedAtUtc", "\"invalid\"")]
    public async Task InvalidListEntryReturns502(string field, string invalid)
    {
        var payload = JsonSerializer.SerializeToNode(Payload())!;
        payload["replays"]![0]![field] = System.Text.Json.Nodes.JsonNode.Parse(invalid);
        var handler = new Handler { Body = payload };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync("/owner/replays");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task TooManyListEntriesReturn502()
    {
        var handler = new Handler { Body = new { replays = Enumerable.Range(0, 2001).Select(_ => new {
            matchId = _matchId.ToString("D"), bytes = 0, complete = false, modifiedAtUtc = DateTimeOffset.UtcNow }) } };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync("/owner/replays");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    // Generate large upstream bodies without allocating a replay-sized buffer.
    private sealed class GeneratedStream(long length) : Stream
    {
        private long _remaining = length;
        public long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _remaining);
            buffer.AsSpan(offset, read).Clear(); _remaining -= read; BytesRead += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Clear(); _remaining -= read; BytesRead += read; return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizeContentLengthRejectedWithoutReading(bool list)
    {
        var source = new GeneratedStream(64_000_001);
        var handler = new Handler { Content = () => {
            var content = new StreamContent(source);
            content.Headers.ContentLength = list ? 512 * 1024 + 1 : 64_000_001;
            return content;
        } };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory); client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync(list ? "/owner/replays" : Download);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, source.BytesRead);
    }

    [Fact]
    public async Task ListWithoutContentLengthIsStillBounded()
    {
        var source = new GeneratedStream(600_000);
        var handler = new Handler { Content = () => new StreamContent(source) };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync("/owner/replays");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.InRange(source.BytesRead, 512 * 1024 + 1, 512 * 1024 + 8192);
    }

    [Fact]
    public async Task DownloadWithoutContentLengthAbortsWhenCapIsExceeded()
    {
        var source = new GeneratedStream(65_000_000);
        var handler = new Handler { Content = () => new StreamContent(source) };
        using var factory = Factory(handler); using var client = factory.CreateClient();
        await SeedAsync(factory); client.DefaultRequestHeaders.Add("X-Console-Key", OwnerKey);
        using var response = await client.GetAsync(Download, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[65536]; long delivered = 0;
        var failure = await Record.ExceptionAsync(async () => {
            int count; while ((count = await stream.ReadAsync(buffer)) > 0) delivered += count;
        });
        Assert.NotNull(failure);
        Assert.InRange(delivered, 1, 64_000_000);
        Assert.InRange(source.BytesRead, 64_000_001, 64_000_000 + 65536);
    }
}
