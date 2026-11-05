namespace MasterServer.Data.Models;

public sealed class FeedbackReport
{
    public Guid Id { get; set; }
    public long ReporterUserId { get; set; }
    public Guid SubmissionId { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public string ContextJson { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public Guid? OnlineMatchId { get; set; }
    public bool HasScreenshot { get; set; }
}
