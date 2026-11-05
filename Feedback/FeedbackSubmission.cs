using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace MasterServer.Feedback;

public sealed record FeedbackSubmission(Guid SubmissionId, string Kind, string Message,
    string ContextJson, string AppVersion, Guid? OnlineMatchId, byte[]? Screenshot)
{
    public const int BodyLimit = 1_114_112;
    public static async Task<FeedbackSubmission> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = BodyLimit;
        if (request.ContentLength > BodyLimit) throw new FeedbackError(413, "feedback-too-large");
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128 || boundary.Any(c => c < 32 || c > 126)) throw Invalid();
        // Bound the transport before multipart parsing, including chunked requests and trailing bytes.
        using var body = new MemoryStream(await ReadBounded(request.Body, BodyLimit, ct));
        var reader = new MultipartReader(boundary, body) { HeadersLengthLimit = 4096, HeadersCountLimit = 8 };
        var metadata = await reader.ReadNextSectionAsync(ct);
        if (metadata is null || PartName(metadata) != "metadata" || !IsType(metadata.ContentType, "application/json")) throw Invalid();
        var json = await ReadBounded(metadata.Body, 16 * 1024, ct);
        var result = Parse(json);
        var image = await reader.ReadNextSectionAsync(ct);
        if (image is not null)
        {
            if (PartName(image) != "screenshot") throw Invalid();
            if (!IsType(image.ContentType, "image/jpeg")) throw new FeedbackError(415, "unsupported-image");
            var bytes = await ReadBounded(image.Body, 1024 * 1024, ct);
            if (bytes.Length < 3 || bytes[0] != 255 || bytes[1] != 216 || bytes[2] != 255)
                throw new FeedbackError(415, "unsupported-image");
            result = result with { Screenshot = bytes };
            if (await reader.ReadNextSectionAsync(ct) is not null) throw Invalid();
        }
        return result;
    }

    private static bool IsType(string? value, string expected) =>
        MediaTypeHeaderValue.TryParse(value, out var type) && type.MediaType.Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static string? PartName(MultipartSection part) =>
        ContentDispositionHeaderValue.TryParse(part.ContentDisposition, out var disposition) &&
        disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
            ? HeaderUtilities.RemoveQuotes(disposition.Name).Value : null;
    private static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), ct)) > 0)
        {
            if (output.Length + read > limit) throw new FeedbackError(413, "feedback-too-large");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static FeedbackSubmission Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(json.Span);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            Closed(root, "schemaVersion", "submissionId", "kind", "message", "context");
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var version)) throw Invalid();
            if (version != 1) throw new FeedbackError(422, "unsupported-feedback-version");
            var id = root.GetProperty("submissionId").GetGuid();
            if (id == Guid.Empty) throw Invalid();
            var kind = Text(root, "kind", 4);
            if (kind is not ("bug" or "feel" or "idea" or "love")) throw Invalid();
            var message = Text(root, "message", 2000, true);
            var context = root.GetProperty("context");
            Closed(context, "appVersion", "protocolVersion", "platform", "scene", "phase", "observedAtUtc", "match");
            var app = Text(context, "appVersion", 128);
            if (!context.GetProperty("protocolVersion").TryGetInt32(out var protocol) || protocol < 0) throw Invalid();
            Text(context, "platform", 128); Text(context, "scene", 128);
            var phase = Text(context, "phase", 16);
            if (phase is not ("frontend" or "match" or "results" or "replay")) throw Invalid();
            if (!context.GetProperty("observedAtUtc").TryGetDateTimeOffset(out var observed) || observed.Offset != TimeSpan.Zero) throw Invalid();
            Guid? online = null;
            if (context.TryGetProperty("match", out var match) && match.ValueKind != JsonValueKind.Null)
            {
                if (phase == "frontend") throw Invalid();
                Closed(match, "mode", "format", "stageId", "onlineMatchId", "localEntityId", "lastObservedServerTick", "fighters");
                Text(match, "mode", 128);
                OptionalText(match, "format"); OptionalText(match, "stageId");
                if (Present(match, "onlineMatchId", out var matchId)) online = matchId.GetGuid();
                if (Present(match, "localEntityId", out var entity) && (!entity.TryGetUInt64(out var entityId) || entityId == 0)) throw Invalid();
                if (Present(match, "lastObservedServerTick", out var tick) && !tick.TryGetUInt32(out _)) throw Invalid();
                var fighters = match.GetProperty("fighters");
                if (fighters.ValueKind != JsonValueKind.Array || fighters.GetArrayLength() > 4) throw Invalid();
                var ids = new HashSet<ulong>();
                foreach (var fighter in fighters.EnumerateArray())
                {
                    Closed(fighter, "entityId", "packageId", "packageVersion", "cookedContentHash");
                    if (!fighter.GetProperty("entityId").TryGetUInt64(out var fighterId) || fighterId == 0 || !ids.Add(fighterId)) throw Invalid();
                    Text(fighter, "packageId", 128); OptionalText(fighter, "packageVersion");
                    if (Present(fighter, "cookedContentHash", out var hash))
                    {
                        var value = hash.GetString();
                        if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
                    }
                }
            }
            return new(id, kind, message, context.GetRawText(), app, online, null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or DecoderFallbackException)
        { throw Invalid(); }
    }
    private static bool Present(JsonElement obj, string name, out JsonElement value) => obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    private static void OptionalText(JsonElement obj, string name) { if (Present(obj, name, out _)) Text(obj, name, 128); }
    private static string Text(JsonElement obj, string name, int max, bool empty = false)
    {
        var value = obj.GetProperty(name).GetString();
        if (value is null || value.Length > max || value.Contains('\0') || (!empty && value.Length == 0)) throw Invalid();
        return value;
    }
    private static void Closed(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
            if (!names.Contains(property.Name) || !seen.Add(property.Name)) throw Invalid();
    }
    private static FeedbackError Invalid() => new(400, "invalid-feedback");
}

public sealed class FeedbackError(int status, string code) : Exception
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
