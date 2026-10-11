using System.Security.Cryptography;
using System.Text;

namespace MasterServer.DTOs;

internal static class OwnerConsoleAccess
{
    public static IResult? Reject(HttpContext context, IConfiguration configuration)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var key = configuration["Console:Key"];
        if (key is null || key.Length is < 32 or > 4096) return Results.NotFound();
        var values = context.Request.Headers["X-Console-Key"];
        if (values.Count != 1 || values[0] is not { Length: <= 4096 } supplied)
            return Results.Unauthorized();
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))
            ? null : Results.Unauthorized();
    }
}
