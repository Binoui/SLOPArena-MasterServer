// MasterServer/Data/Models/Match.cs
namespace MasterServer.Data.Models;

public class Match
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Player1SteamId { get; set; }
    public long Player2SteamId { get; set; }
    public long? Player3SteamId { get; set; }
    public long? Player4SteamId { get; set; }
    public long? WinnerSteamId { get; set; } // Null for draws or disconnects
    public string ServerRegion { get; set; } = string.Empty;
    public Guid? ServerId { get; set; }
    public Guid? RoomId { get; set; }
    public DateTime? CanceledAt { get; set; }
    public string? CancelReason { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    // Seconds each rostered player's Steam peer stayed connected, from the GameHost result.
    public int? Player1ConnectedSeconds { get; set; }
    public int? Player2ConnectedSeconds { get; set; }
    public int? Player3ConnectedSeconds { get; set; }
    public int? Player4ConnectedSeconds { get; set; }

    // Navigation properties
    public User? Player1 { get; set; }
    public User? Player2 { get; set; }
    public User? Winner { get; set; }
}
