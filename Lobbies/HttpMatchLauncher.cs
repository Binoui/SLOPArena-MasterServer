using MasterServer.Rooms;
// MasterServer/Lobbies/HttpMatchLauncher.cs
using System.Net.Http.Json;
using System.Net.Http.Headers;
using MasterServer.Configuration;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MasterServer.Data;
using Microsoft.EntityFrameworkCore;
namespace MasterServer.Lobbies;

/// <summary>
/// Launches a selected PvP arena on the registered GameServer.
/// </summary>
public interface IMatchLauncher
{
    /// <summary>
    /// Starts a match and returns both its assigned UDP port and the opaque
    /// authoritative content map emitted by the GameServer.
    /// </summary>
    Task<MatchLaunchResult> LaunchAsync(MatchStartedConfig config);

    /// <summary>Stops only the named Match through its GameHost's existing control endpoint.</summary>
    Task AbortAsync(Guid serverId, Guid matchId);
}

/// <summary>
/// Real <see cref="IMatchLauncher"/> (issue #35): POSTs the match-start command
/// to the game server over HTTP and returns its assigned UDP port plus the
/// opaque authoritative content map.
///
/// The listener exposes <c>POST /match/start</c>. Development mode uses the
/// registered address; VPS mode uses the separately provisioned private control
/// URL and bearer key. The roster is sent and the authoritative content map is
/// forwarded unchanged (ADR-0008).
/// </summary>
public sealed class HttpMatchLauncher : IMatchLauncher
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<HttpMatchLauncher> _logger;
    private readonly MasterDeploymentOptions _deployment;
    private readonly int _maxPlayersPerLobby;

    /// <param name="http">Managed <see cref="HttpClient"/> from <c>AddHttpClient</c>; reused across scopes to avoid socket exhaustion.</param>
    /// <param name="options">Lobby capacity options (issue #6); defaults to 4 per lobby.</param>
    public HttpMatchLauncher(
        AppDbContext db,
        ILogger<HttpMatchLauncher> logger,
        HttpClient http,
        MasterDeploymentOptions deployment,
        LobbyOptions? options = null)
    {
        _db = db;
        _logger = logger;
        _http = http;
        _deployment = deployment;
        _maxPlayersPerLobby = LobbyOptions.ResolveMax(options);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<MatchLaunchResult> LaunchAsync(MatchStartedConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ArenaName))
            throw new InvalidOperationException("A Match requires an explicit arena name.");
        if (config.FriendlyFire && config.Format != RoomFormats.SlopballCoop)
            throw new InvalidOperationException("Friendly fire is a co-op Slopball rule.");
        if (config.Format == RoomFormats.SlopballVersus
            ? config.Players.Count is not (2 or 4) || config.Players.Any(p => p.Camp is not ("teal" or "clay"))
                || config.Players.Count(p => p.Camp == "teal") != config.Players.Count / 2
            : config.BallOnly || config.Players.Any(p => !string.IsNullOrEmpty(p.Camp)))
            throw new InvalidOperationException("Invalid Versus Slopball camps or Ball only rule.");
        if (RoomFormats.IsCourse(config.Format) && (config.Players.Any(p => p.Colour > 3 || p.EntityId <= 0 || p.EntityId >= 128)
            || config.Players.Select(p => p.Colour).Distinct().Count() != config.Players.Count
            || config.Players.Select(p => p.Character).Distinct().Count() != 1))
            throw new InvalidOperationException("Invalid course fighter or colours.");
        if (_deployment.IsVps && config.ServerId != _deployment.ApprovedHostId)
            throw new InvalidOperationException("Only the provisioned GameServer can launch VPS matches.");
        var server = await _db.GameServers.FindAsync(config.ServerId);
        if (server is null)
            throw new InvalidOperationException(
                $"Game server {config.ServerId} is not registered — cannot start match.");
        if (_deployment.IsVps)
        {
            if (server.ProtocolVersion != SteamMatchDescriptor.Protocol || string.IsNullOrEmpty(server.SteamId) ||
                (server.InstanceId is null || server.InstanceId == Guid.Empty) ||
                server.CatalogHash is not { Length: 64 } ||
                server.LastHeartbeat < DateTime.UtcNow.AddSeconds(-15))
                throw new InvalidOperationException("Steam GameHost is not currently registered and ready.");
        }
        var instanceAtStart = server.InstanceId;
        var steamIdAtStart = server.SteamId;
        var catalogAtStart = server.CatalogHash;

        var arena = config.ArenaName;
        var matchGuid = config.MatchId == Guid.Empty ? Guid.NewGuid() : config.MatchId;
        var matchId = matchGuid.ToString();
        var players = config.Players;

        // The persisted Match row fits exactly 2–4 players — reject anything
        // else before creating the row or POSTing, so the roster and the row
        // can never diverge (issue #6).
        if (players.Count < LobbyLimits.MinPlayers || players.Count > _maxPlayersPerLobby)
        {
            throw new InvalidOperationException(
                $"Cannot launch match with {players.Count} players " +
                $"(expected {LobbyLimits.MinPlayers}–{_maxPlayersPerLobby}).");
        }
        if (_deployment.IsVps)
        {
            var identities = players.Select(p => p.SteamId).ToArray();
            if (identities.Any(id => id <= 0) || identities.Distinct().Count() != identities.Length ||
                players.Select(p => p.EntityId).Distinct().Count() != players.Count ||
                players.Any(p => p.EntityId <= 0 || string.IsNullOrEmpty(p.Character)))
                throw new InvalidOperationException("Match roster is not a unique assigned Steam roster.");
            int verified = await _db.Users.CountAsync(user => identities.Contains(user.SteamId) &&
                user.AuthProvider == "steam");
            if (verified != identities.Length)
                throw new InvalidOperationException("Every roster member must have a verified Steam identity.");
        }

        // Create the Match row up front so the game server's later
        // POST /match/result finds it (issue #40). Rolled back on launch failure.
        _db.Matches.Add(new MasterServer.Data.Models.Match
        {
            Id = matchGuid,
            Player1SteamId = players[0].SteamId,
            Player2SteamId = players[1].SteamId,
            Player3SteamId = players.Count > 2 ? players[2].SteamId : null,
            Player4SteamId = players.Count > 3 ? players[3].SteamId : null,
            ServerId = server.Id,
            RoomId = config.RoomId,
            ServerRegion = server.Region,
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var admissionDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var body = new Dictionary<string, object?>
        {
            ["matchId"] = matchId,
            ["arenaName"] = arena,
            ["players"] = config.Players.Select(p => new
                {
                    steamId = p.SteamId,
                    // Wire key stays `characterClass` — the game server's
                    // MatchStartRequestCodec reads it verbatim (issue #7).
                    characterClass = p.Character,
                    entityId = p.EntityId,
                    camp = p.Camp,
                    colour = p.Colour,
                })
                .ToArray(),
        };
        if (_deployment.IsVps)
        {
            body["protocolVersion"] = SteamMatchDescriptor.Protocol;
            body["virtualPort"] = 0;
            body["maxStocks"] = 3;
            body["catalogHash"] = catalogAtStart;
        }
        if (_deployment.IsVps || config.RoomId is not null)
        {
            body["admissionExpiresAtUtc"] = admissionDeadline;
            body["catalogHash"] = config.CatalogHash ?? catalogAtStart;
        }
        if (config.Format != RoomFormats.Stock)
            body["format"] = config.Format;
        if (config.Format == RoomFormats.SlopballCoop)
            body["friendlyFire"] = config.FriendlyFire;
        if (config.Format == RoomFormats.SlopballVersus)
            body["ballOnly"] = config.BallOnly;

        var url = _deployment.IsVps
            ? _deployment.ControlUrl!
            : new Uri($"http://{server.IpAddress}:{server.Port}/match/start");
        _logger.LogInformation(
            "Launching match {MatchId} on server {ServerId} with {Count} players",
            matchId, config.ServerId, config.Players.Count);

        bool launchSent = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body)
            };
            if (_deployment.IsVps)
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _deployment.MatchControlKey!);
            launchSent = true;
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MatchStartResponse>();
            if (result is null || result.Content.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("GameHost did not return match content.");
            ValidateMatchContent(result, arena);
            if (_deployment.IsVps || config.RoomId is not null)
                await _db.Entry(server).ReloadAsync();
            if (_deployment.IsVps)
            {
                if (result.MatchId != matchGuid || result.ServerSteamId != steamIdAtStart ||
                    server.SteamId != steamIdAtStart || server.InstanceId != instanceAtStart ||
                    server.CatalogHash != catalogAtStart || result.ActualCatalogHash != catalogAtStart ||
                    (config.CatalogHash != null && result.ActualCatalogHash != config.CatalogHash) ||
                    server.ProtocolVersion != SteamMatchDescriptor.Protocol || server.LastHeartbeat < DateTime.UtcNow.AddSeconds(-15) ||
                    result.ProtocolVersion != SteamMatchDescriptor.Protocol || result.VirtualPort != 0)
                    throw new InvalidOperationException("GameHost returned an incompatible or stale Steam match route.");
                var descriptor = new SteamMatchDescriptor("steam-p2p", result.ServerSteamId!,
                    matchGuid, 0, SteamMatchDescriptor.Protocol, result.ContentHash!, admissionDeadline);
                _logger.LogInformation("Steam match {MatchId} launched on host {ServerId}", matchId, server.Id);
                return new MatchLaunchResult(0, result.Content.Clone(), descriptor, matchGuid);
            }
            if (config.RoomId is not null &&
                (result.ActualCatalogHash != config.CatalogHash ||
                 server.CatalogHash != config.CatalogHash || server.CatalogHash != catalogAtStart ||
                 server.InstanceId != instanceAtStart ||
                 server.LastHeartbeat < DateTime.UtcNow.AddSeconds(-15)))
                throw new InvalidOperationException("GameHost returned content that differs from the Room's pinned catalog.");
            if (result.Port <= 0)
                throw new InvalidOperationException("Development GameServer did not return a match port.");
            return new MatchLaunchResult(result.Port, result.Content.Clone(), MatchId: matchGuid);
        }
        catch
        {
            if (launchSent)
            {
                try
                {
                    await AbortAsync(config.ServerId, matchGuid);
                }
                catch (Exception)
                {
                    _logger.LogWarning("GameHost abort unavailable for match {MatchId}.", matchId);
                }
            }
            // Roll the pre-created row back so a failed launch leaves no orphan.
            var row = await _db.Matches.FindAsync(matchGuid);
            if (row != null)
            {
                _db.Matches.Remove(row);
                await _db.SaveChangesAsync();
            }
            throw;
        }
    }

    public async Task AbortAsync(Guid serverId, Guid matchId)
    {
        if (matchId == Guid.Empty)
            throw new ArgumentException("A Match identity is required.", nameof(matchId));
        if (_deployment.IsVps && serverId != _deployment.ApprovedHostId)
            throw new InvalidOperationException("Only the provisioned GameServer can stop VPS matches.");
        var server = await _db.GameServers.FindAsync(serverId)
            ?? throw new InvalidOperationException("The Match's GameServer is no longer registered.");
        var url = _deployment.IsVps
            ? new Uri(_deployment.ControlUrl!, "/match/abort")
            : new Uri($"http://{server.IpAddress}:{server.Port}/match/abort");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { matchId })
        };
        if (_deployment.IsVps)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _deployment.MatchControlKey!);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static void ValidateMatchContent(MatchStartResponse result, string requestedArena)
    {
        var content = result.Content;
        if (!content.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version) || version != 2 ||
            !content.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            !content.TryGetProperty("arena", out var arena) || arena.ValueKind != JsonValueKind.Object ||
            arena.EnumerateObject().Count() != 2 ||
            !arena.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !string.Equals(name.GetString(), requestedArena, StringComparison.Ordinal) ||
            !arena.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            !IsHash(digest.GetString()) || !IsHash(result.ActualCatalogHash) || !IsHash(result.ContentHash))
            throw new InvalidOperationException("GameHost returned missing or mismatched arena identity.");
        // GameHost emits canonical content JSON. Validate those bytes and forward them unchanged.
        string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.GetRawText()))).ToLowerInvariant();
        if (!string.Equals(result.ContentHash, contentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("GameHost returned a match content hash that does not bind its arena and characters.");
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class MatchStartResponse
    {
        public int Port { get; set; }
        public Guid MatchId { get; set; }
        public string? ServerSteamId { get; set; }
        public int VirtualPort { get; set; }
        public int ProtocolVersion { get; set; }
        public string? ContentHash { get; set; }
        public string? ActualCatalogHash { get; set; }
        public JsonElement Content { get; set; }
    }
}
