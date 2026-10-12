using System.Globalization;
using MasterServer.Data;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.DTOs;

internal sealed record RegisteredPlayer(
    string SteamId,
    string Name,
    string AuthProvider,
    DateTimeOffset FirstLoginUtc,
    DateTimeOffset? LastLoginUtc);

internal sealed record OwnerRegisteredPlayers(
    DateTimeOffset CapturedAtUtc,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<RegisteredPlayer> Players)
{
    internal const int Size = 100;

    internal static async Task<OwnerRegisteredPlayers> CaptureAsync(
        AppDbContext db, TimeProvider clock, int page, string search, CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking();
        if (search.Length > 0)
        {
            var name = search.ToLowerInvariant();
            var isId = long.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
            query = query.Where(user => user.Username.ToLower().Contains(name) || isId && user.SteamId == id);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(user => user.LastLogin ?? user.CreatedAt)
            .ThenBy(user => user.SteamId)
            .Skip(page * Size).Take(Size)
            .Select(user => new { user.SteamId, user.Username, user.AuthProvider, user.CreatedAt, user.LastLogin })
            .ToListAsync(cancellationToken);
        return new OwnerRegisteredPlayers(clock.GetUtcNow(), page, Size, total,
            rows.Select(user => new RegisteredPlayer(
                user.SteamId.ToString(CultureInfo.InvariantCulture), user.Username, user.AuthProvider,
                new DateTimeOffset(DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc)),
                user.LastLogin is { } lastLogin
                    ? new DateTimeOffset(DateTime.SpecifyKind(lastLogin, DateTimeKind.Utc)) : null)).ToArray());
    }
}
