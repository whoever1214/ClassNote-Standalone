using System.Windows;

namespace ClassNote;

public partial class App : Application
{
    public App()
    {
        // 全局滚轮手感优化：平滑滚动替代默认的按行跳变
        Controls.SmoothScroll.Enable();
    }
}