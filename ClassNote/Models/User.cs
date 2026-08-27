namespace ClassNote.Models;
public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Token { get; set; }
}
