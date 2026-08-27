namespace ClassNote.Models;
public class Screenshot
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int SeqNo { get; set; }
    public double Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string? ImageUrl { get; set; }
    public byte[]? ImageData { get; set; }
    public DateTime CapturedAt { get; set; }
}
