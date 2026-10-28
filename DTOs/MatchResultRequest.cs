// MasterServer/DTOs/MatchResultRequest.cs
namespace MasterServer.DTOs;

public record MatchResultRequest(Guid MatchId, long WinnerSteamId,
    IReadOnlyList<MatchPlayerTime>? Players = null);

/// <summary>How long one player's Steam peer stayed connected to the match.</summary>
public record MatchPlayerTime(long SteamId, int ConnectedSeconds);
