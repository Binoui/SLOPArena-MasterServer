using System.Globalization;
using MasterServer.Lobbies;

namespace MasterServer.Rooms;

/// <summary>
/// Room entry requires the client's protocol, sent as the hub's <c>protocol</c> query value,
/// so an outdated build is refused before it meets a changed Room flow (ADR-0040).
/// </summary>
public static class RoomClientProtocol
{
    public const string QueryKey = "protocol";

    /// <summary>
    /// The VPS requires Master's protocol. Development also admits a missing value and the
    /// development protocol 0, matching its GameHost allocation.
    /// </summary>
    public static bool IsCompatible(string? value, bool isVps)
    {
        if (string.IsNullOrEmpty(value))
            return !isVps;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var protocol))
            return false;
        return protocol == SteamMatchDescriptor.Protocol || !isVps && protocol == 0;
    }
}
