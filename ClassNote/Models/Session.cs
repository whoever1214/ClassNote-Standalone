namespace ClassNote.Models;
public class Session
{
    public Guid Id { get; set; }
    public string Course { get; set; } = "";
    public string? Title { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? Duration { get; set; }
    public string Status { get; set; } = "recording";
}
