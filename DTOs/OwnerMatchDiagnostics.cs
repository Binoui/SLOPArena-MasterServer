using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MasterServer.Configuration;
using MasterServer.Data;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.DTOs;

internal sealed record ObservedMatchLog(long Sequence, DateTimeOffset AtUtc, uint Tick, string Level, string Message);
internal sealed record ObservedMatchPeer(string EntityId, bool Connected, DateTimeOffset SampledAtUtc,
    int? PingMilliseconds, float? ReceiveQuality, float? SendQuality);
internal sealed record ObservedMatchDiagnostics(string MatchId, string Arena, string Transport,
    DateTimeOffset CapturedAtUtc, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc,
    uint ServerTick, long TickSamples, double MeanTickMilliseconds, double MaxTickMilliseconds,
    double MaxScheduleDelayMilliseconds, long SlowTicks, long ScheduleResets,
    int EventLimit, long DroppedEvents, IReadOnlyList<ObservedMatchPeer> Peers, IReadOnlyList<ObservedMatchLog> Events);

internal sealed record MatchDiagnosticsRead(int StatusCode, ObservedMatchDiagnostics? Diagnostics = null,
    string? Message = null);

/// <summary>Read a known match only from the provisioned private GameHost control route.</summary>
internal sealed class OwnerMatchDiagnosticsReader(AppDbContext db, HttpClient http, MasterDeploymentOptions deployment)
{
    private const int MaxResponseBytes = 512 * 1024;

    public async Task<MatchDiagnosticsRead> ReadAsync(Guid matchId, CancellationToken ct)
    {
        var match = await db.Matches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null) return new(404, Message: "Match is not recorded by Master.");
        if (!deployment.IsVps || match.ServerId != deployment.ApprovedHostId || deployment.ControlUrl is null)
            return new(503, Message: "Diagnostics are unavailable for this GameServer.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(deployment.ControlUrl, $"/match/diagnostics/{matchId:D}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deployment.MatchControlKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(410, Message: "GameHost has no retained logs for this match. Logs expire after 50 completed matches or a GameHost restart.");
            if (!response.IsSuccessStatusCode)
                return new(503, Message: "GameHost diagnostics are unavailable.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return new(502, Message: "GameHost diagnostics exceed the response limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > MaxResponseBytes)
                    return new(502, Message: "GameHost diagnostics exceed the response limit.");
                buffer.Write(chunk, 0, count);
            }
            var data = JsonSerializer.Deserialize<ObservedMatchDiagnostics>(buffer.ToArray(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (data is null || data.MatchId != matchId.ToString("D") ||
                data.Events is null || data.Peers is null || data.Events.Count > 256 || data.Peers.Count > 4 ||
                data.Events.Any(e => e is null || e.Message is null || e.Message.Length > 512) ||
                data.Peers.Any(p => p is null))
                return new(502, Message: "GameHost returned invalid match diagnostics.");
            return new(200, data);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(503, Message: "GameHost diagnostics timed out."); }
        catch (HttpRequestException)
        { return new(503, Message: "GameHost diagnostics are unavailable."); }
        catch (JsonException)
        { return new(502, Message: "GameHost returned invalid match diagnostics."); }
    }
}
