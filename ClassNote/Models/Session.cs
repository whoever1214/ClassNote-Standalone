using System.ComponentModel;

namespace ClassNote.Models;

public class Session : INotifyPropertyChanged
{
    public Guid Id { get; set; }
    public string Course { get; set; } = "";
    public string? Title { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? Duration { get; set; }
    public string Status { get; set; } = "recording";

    private bool _isSelected;

    /// <summary>最近记录列表中的勾选状态（用于多选 / 全选 / 批量导出 / 批量删除）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
