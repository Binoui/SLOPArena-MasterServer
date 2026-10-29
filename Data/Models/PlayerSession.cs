// MasterServer/Data/Models/PlayerSession.cs
namespace MasterServer.Data.Models;

/// <summary>
/// One authenticated LobbyHub connection: the time a player spent online, from menus to
/// matches. A null <see cref="DisconnectedAt"/> after a Master restart means the end was lost.
/// </summary>
public class PlayerSession
{
    public long Id { get; set; }
    public long SteamId { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
}
