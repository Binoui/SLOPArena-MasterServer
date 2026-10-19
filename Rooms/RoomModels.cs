using MasterServer.Lobbies;

namespace MasterServer.Rooms;

public sealed record RoomMember(long SteamId, string Name, bool IsLeader,
    string? CharacterSelection = null, bool LockedIn = false);

public sealed record RoomSnapshot(
    Guid Id,
    string Name,
    string Phase,
    long LeaderSteamId,
    IReadOnlyList<RoomMember> Members,
    int MemberCount,
    int Capacity,
    bool Joinable,
    string? ArenaName,
    IReadOnlyList<string> AdmittedCharacters,
    IReadOnlyList<string> AdmittedArenas,
    Guid? ActiveMatchId = null,
    string Format = RoomFormats.Stock,
    IReadOnlyList<RoomArcadeEntry>? AdmittedArcade = null);

/// <summary>An Arcade format and the arena it plays on, admitted for Rooms (ADR-0040).</summary>
public sealed record RoomArcadeEntry(string Format, string Arena);

internal sealed record RoomObservationSnapshot(
    Guid Id,
    string Name,
    string Phase,
    long LeaderSteamId,
    IReadOnlyList<RoomMember> Members,
    int MemberCount,
    int Capacity,
    bool Joinable,
    string? ArenaName,
    Guid? ActiveMatchId);

public sealed record RoomLaunchPreparation(Guid RoomId, Guid MatchId, string ArenaName,
    IReadOnlyList<LobbyPlayer> Players, string Format = RoomFormats.Stock);


public sealed record RoomSummary(
    Guid Id,
    string Name,
    string Phase,
    long LeaderSteamId,
    int MemberCount,
    int Capacity,
    bool Joinable);

public sealed record RoomConnectionResult(RoomSnapshot? Snapshot, RoomMutationResult? Change);

public sealed record RoomMutationResult(
    Guid RoomId,
    RoomSnapshot? Snapshot,
    string? Error,
    bool Changed,
    bool Deleted,
    IReadOnlyList<string> ConnectionIds);
