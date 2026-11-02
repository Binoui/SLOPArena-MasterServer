using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MasterServer.Configuration;
using MasterServer.Data;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.DTOs;

internal sealed record OwnerReplay(string MatchId, long Bytes, bool Complete, DateTimeOffset ModifiedAtUtc);
internal sealed record OwnerReplayList(IReadOnlyList<OwnerReplay> Replays);

/// <summary>Bounded, read-only access to recordings on the provisioned GameHost.</summary>
internal sealed class OwnerReplays(AppDbContext db, HttpClient http, MasterDeploymentOptions deployment)
{
    internal const long MaxReplayBytes = 64_000_000;
    private const int MaxListBytes = 512 * 1024;
    private bool Available => deployment.IsVps && deployment.ControlUrl is not null;

    private HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(deployment.ControlUrl!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deployment.MatchControlKey);
        return request;
    }

    public async Task<IResult> ListAsync(CancellationToken ct)
    {
        if (!Available) return Error(503, "GameHost replays are unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var request = Request("/match/replays");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK) return Error(503, "GameHost replays are unavailable.");
            if (response.Content.Headers.ContentLength > MaxListBytes) return InvalidList();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > MaxListBytes) return InvalidList();
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("replays", out var entries) ||
                entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 2000) return InvalidList();
            var replays = new List<OwnerReplay>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("matchId", out var id) || id.ValueKind != JsonValueKind.String ||
                    !Guid.TryParseExact(id.GetString(), "D", out var guid) || id.GetString() != guid.ToString("D") ||
                    !entry.TryGetProperty("bytes", out var size) || size.ValueKind != JsonValueKind.Number ||
                    !size.TryGetInt64(out var bytes) || bytes < 0 || bytes > MaxReplayBytes ||
                    !entry.TryGetProperty("complete", out var complete) ||
                    complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !entry.TryGetProperty("modifiedAtUtc", out var modified) || modified.ValueKind != JsonValueKind.String ||
                    !modified.TryGetDateTimeOffset(out var at) || at.Offset != TimeSpan.Zero) return InvalidList();
                replays.Add(new(guid.ToString("D"), bytes, complete.GetBoolean(), at));
            }
            return Results.Ok(new OwnerReplayList(replays));
        }
        catch (JsonException) { return InvalidList(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Error(503, "GameHost replays timed out."); }
        catch (HttpRequestException) { return Error(503, "GameHost replays are unavailable."); }
        catch (IOException) { return Error(503, "GameHost replays are unavailable."); }
    }

    public async Task DownloadAsync(Guid matchId, HttpContext context)
    {
        var ct = context.RequestAborted;
        var match = await db.Matches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null) { await Error(404, "Match is not recorded by Master.").ExecuteAsync(context); return; }
        if (!Available || match.ServerId != deployment.ApprovedHostId)
        { await Error(503, "Replays are unavailable for this GameServer.").ExecuteAsync(context); return; }
        try
        {
            using var request = Request($"/match/replays/{matchId:D}");
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headersTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = response.StatusCode switch
                { HttpStatusCode.NotFound => 410, HttpStatusCode.Conflict => 409, _ => 503 };
                await Error(status, status == 410 ? "GameHost has no recording retained for this match." :
                    status == 409 ? "GameHost is still recording this match." : "GameHost replays are unavailable.").ExecuteAsync(context);
                return;
            }
            if (response.Content.Headers.ContentLength > MaxReplayBytes)
            { await Error(502, "GameHost replay exceeds the response limit.").ExecuteAsync(context); return; }
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyTimeout.CancelAfter(TimeSpan.FromSeconds(120));
            await using var stream = await response.Content.ReadAsStreamAsync(bodyTimeout.Token);
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{matchId:D}.slopreplay\"";
            context.Response.ContentLength = response.Content.Headers.ContentLength;
            var chunk = new byte[64 * 1024];
            long total = 0;
            int count;
            while ((count = await stream.ReadAsync(chunk, bodyTimeout.Token)) > 0)
            {
                total += count;
                if (total > MaxReplayBytes)
                {
                    if (context.Response.HasStarted) context.Abort();
                    else { context.Response.Clear(); await Error(502, "GameHost replay exceeds the response limit.").ExecuteAsync(context); }
                    return;
                }
                await context.Response.Body.WriteAsync(chunk.AsMemory(0, count), bodyTimeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (context.Response.HasStarted || ct.IsCancellationRequested) context.Abort();
            else
            {
                context.Response.Clear();
                context.Response.Headers.CacheControl = "no-store";
                await Error(503, "GameHost replays are unavailable or timed out.").ExecuteAsync(context);
            }
        }
    }

    private static IResult InvalidList() => Error(502, "GameHost returned invalid or oversized replay data.");
    private static IResult Error(int status, string message) => Results.Json(new { message }, statusCode: status);
}
