namespace MasterServer.Data.Models;

public sealed class FeedbackScreenshot
{
    public Guid ReportId { get; set; }
    public byte[] Jpeg { get; set; } = [];
    public int Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}
