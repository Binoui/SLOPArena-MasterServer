using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MasterServer.Data;
using MasterServer.Data.Models;
using MasterServer.DTOs;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.Feedback;

public static class FeedbackEndpoints
{
    // Bounded stripes also serialize the nonrelational test provider. PostgreSQL row locks
    // arbitrate across processes; no in-process lock is relied on for durable admission.
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public static void MapFeedback(this WebApplication app)
    {
        app.MapPost("/feedback", Accept).RequireAuthorization();
        app.MapGet("/owner/feedback", List);
        app.MapGet("/owner/feedback/{id:guid}", Detail);
        app.MapGet("/owner/feedback/{id:guid}/screenshot", Screenshot);
    }
    private static IResult Error(int status, string code) => Results.Json(new { code }, statusCode: status);
    private static async Task<IResult> Accept(HttpContext context, AppDbContext db, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var reporter)) return Results.Unauthorized();
        FeedbackSubmission submission;
        try { submission = await FeedbackSubmission.ReadAsync(context.Request, context.RequestAborted); }
        catch (FeedbackError e) { return Error(e.Status, e.Code); }
        catch (BadHttpRequestException e) when (e.StatusCode == 413) { return Error(413, "feedback-too-large"); }
        catch (Exception e) when (e is InvalidDataException or IOException) { return Error(400, "invalid-feedback"); }
        var ct = context.RequestAborted;
        var gate = Gates[(int)((ulong)reporter % (ulong)Gates.Length)];
        await gate.WaitAsync(ct);
        try
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            var player = db.Database.IsRelational()
                ? (await db.Users.FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"SteamId\" = {reporter} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
                : await db.Users.FindAsync([reporter], ct);
            if (player is null || player.AuthProvider != context.User.FindFirstValue("provider")) return Results.Unauthorized();
            var existing = await db.FeedbackReports.AsNoTracking().SingleOrDefaultAsync(r => r.ReporterUserId == reporter && r.SubmissionId == submission.SubmissionId, ct);
            if (existing is not null) return Results.Ok(new { reportId = existing.Id, existing.ReceivedAtUtc });
            var now = clock.GetUtcNow().UtcDateTime;
            // PostgreSQL timestamps have microsecond precision. Store that same value in the first receipt.
            now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
            var recent = db.FeedbackReports.Where(r => r.ReporterUserId == reporter && r.ReceivedAtUtc > now.AddHours(-24));
            if (await recent.CountAsync(ct) >= 20)
            {
                var oldest = await recent.MinAsync(r => r.ReceivedAtUtc, ct);
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((oldest.AddHours(24) - now).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Error(429, "feedback-rate-limited");
            }
            var report = new FeedbackReport { Id = Guid.NewGuid(), ReporterUserId = reporter,
                SubmissionId = submission.SubmissionId, ReceivedAtUtc = now, Kind = submission.Kind,
                Message = submission.Message, ContextJson = submission.ContextJson, AppVersion = submission.AppVersion,
                OnlineMatchId = submission.OnlineMatchId, HasScreenshot = submission.Screenshot is not null };
            db.FeedbackReports.Add(report);
            if (submission.Screenshot is { } image)
                db.FeedbackScreenshots.Add(new FeedbackScreenshot { ReportId = report.Id, Jpeg = image,
                    Bytes = image.Length, Sha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant() });
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Results.Json(new { reportId = report.Id, report.ReceivedAtUtc }, statusCode: 201);
        }
        finally { gate.Release(); }
    }
    private static IResult? Reject(HttpContext context, IConfiguration configuration)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return OwnerConsoleAccess.Reject(context, configuration);
    }
    private static async Task<IResult> List(HttpContext context, IConfiguration configuration, AppDbContext db)
    {
        if (Reject(context, configuration) is { } rejected) return rejected;
        var limit = 100;
        if (context.Request.Query.TryGetValue("limit", out var raw) && (!int.TryParse(raw, out limit) || limit is < 1 or > 100)) return Error(400, "invalid-feedback");
        var query = db.FeedbackReports.AsNoTracking().AsQueryable();
        var cursor = context.Request.Query["cursor"].ToString();
        if (cursor.Length > 0)
        {
            try
            {
                if (cursor.Length > 128) throw new FormatException();
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
                if (parts.Length != 3 || parts[0] != "1") throw new FormatException();
                var at = new DateTime(long.Parse(parts[1], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                var id = Guid.ParseExact(parts[2], "N");
                query = query.Where(r => r.ReceivedAtUtc > at || (r.ReceivedAtUtc == at && r.Id.CompareTo(id) > 0));
            }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { return Error(400, "invalid-feedback"); }
        }
        var rows = await query.OrderBy(r => r.ReceivedAtUtc).ThenBy(r => r.Id).Take(limit + 1)
            .Select(r => new { reportId = r.Id, r.ReceivedAtUtc, r.Kind, r.AppVersion, r.HasScreenshot }).ToListAsync(context.RequestAborted);
        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes($"1|{last.ReceivedAtUtc.Ticks}|{last.reportId:N}"));
        }
        return Results.Ok(new { items = rows, nextCursor });
    }
    private static async Task<IResult> Detail(Guid id, HttpContext context, IConfiguration configuration, AppDbContext db)
    {
        if (Reject(context, configuration) is { } rejected) return rejected;
        var report = await db.FeedbackReports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, context.RequestAborted);
        if (report is null) return Results.NotFound();
        var player = await db.Users.AsNoTracking().SingleAsync(u => u.SteamId == report.ReporterUserId, context.RequestAborted);
        var image = await db.FeedbackScreenshots.AsNoTracking().Where(s => s.ReportId == id)
            .Select(s => new { s.Bytes, s.Sha256 }).SingleOrDefaultAsync(context.RequestAborted);
        using var json = JsonDocument.Parse(report.ContextJson);
        return Results.Ok(new { reportId = report.Id, report.ReceivedAtUtc,
            reporter = new { steamId = player.SteamId.ToString(CultureInfo.InvariantCulture), displayName = player.Username },
            report.Kind, report.Message, context = json.RootElement.Clone(), report.OnlineMatchId, screenshot = image });
    }
    private static async Task<IResult> Screenshot(Guid id, HttpContext context, IConfiguration configuration, AppDbContext db)
    {
        if (Reject(context, configuration) is { } rejected) return rejected;
        var image = await db.FeedbackScreenshots.AsNoTracking().Where(s => s.ReportId == id).Select(s => s.Jpeg).SingleOrDefaultAsync(context.RequestAborted);
        return image is null ? Results.NotFound() : Results.Bytes(image, "image/jpeg");
    }
}
