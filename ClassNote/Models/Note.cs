namespace ClassNote.Models;
public class Note
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string? Title { get; set; }
    public string? ContentMarkdown { get; set; }
    public object? MindmapData { get; set; }
    public string? Summary { get; set; }
}
