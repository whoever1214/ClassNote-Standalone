namespace ClassNote.Models;

/// <summary>Server response for initializing a chunked audio upload.</summary>
public class AudioUploadInitResult
{
    public string UploadId { get; set; } = "";
    public int PartSize { get; set; }
}
