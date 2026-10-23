using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MasterServer.Tests;

internal static class ArenaMatchContentFixture
{
    public static JsonElement Create(string arenaName, string? digest = null) => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = 2,
        entries = Array.Empty<object>(),
        arena = new { name = arenaName, digest = digest ?? new string('c', 64) }
    });

    public static string Hash(JsonElement content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.GetRawText()))).ToLowerInvariant();
}
