using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ClassNote.Controls;

/// <summary>
/// 沿"可视树 + 内容树"向上找祖先的统一实现。
///
/// 为什么必须单独抽出来：WPF 的命中测试会把 <b>文本行内元素</b>（<c>Run</c> / <c>Span</c> /
/// <c>Hyperlink</c>）当作 <c>e.OriginalSource</c> 交给事件处理器 ——
/// 「最近记录」列表的时间戳列在 v0.4.x 由 <c>TextBlock Text="..."</c> 改成
/// <c>TextBlock + 两个显式 &lt;Run&gt;</c>（要带星期几）之后就是这种情况。
///
/// 而 <c>Run</c> 是 <see cref="System.Windows.Documents.Run"/>（<c>FrameworkContentElement</c>），
/// <b>不是 Visual</b>，所以对它调用
/// <see cref="VisualTreeHelper.GetParent(DependencyObject)"/> 会直接抛
/// <c>InvalidOperationException</c>：
///   「System.Windows.Documents.Run 不是 Visual 或 Visual3D。」
/// 用户点一下记录就弹这个错误框，而且因为 <c>App</c> 的全局异常兜底把异常吞掉（Handled = true），
/// 页面既没打开笔记、也没有任何可用信息 —— 现场看到的就是"点了没反应/报错"。
///
/// 正确写法是分派到对应的"父级查询"API：
///   · <see cref="Visual"/> / <see cref="Visual3D"/> → <see cref="VisualTreeHelper.GetParent"/>；
///   · <see cref="ContentElement"/> → <see cref="ContentOperations.GetParent"/>（拿不到时看 <c>Parent</c>）；
///   · 其它 <see cref="FrameworkContentElement"/>（如框架级内容元素）→ <c>Parent</c>。
/// 这与 <c>Controls/SmoothScroll</c> 里滚轮祖先查找的写法一致（那里早就处理了这个分派）。
/// </summary>
public static class VisualTreeWalk
{
    /// <summary>
    /// 取逻辑/可视父级。永远不抛异常：遇到既不是 Visual 也不是内容元素的节点返回 null。
    /// </summary>
    public static DependencyObject? GetParent(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Visual || node is Visual3D)
                return VisualTreeHelper.GetParent(node);

            if (node is ContentElement content)
            {
                var parent = ContentOperations.GetParent(content);
                if (parent != null)
                    return parent;
                // 内容元素的根（不在任何内容宿主里）时，框架级内容元素仍能给出 Parent
                return content is FrameworkContentElement fce ? fce.Parent : null;
            }

            if (node is FrameworkContentElement frameworkContent)
                return frameworkContent.Parent;

            return null;
        }
        return null;
    }

    /// <summary>
    /// 从 <paramref name="node"/> 起向上找第一个类型为 <typeparamref name="T"/> 的祖先（含自身）。
    /// 找不到返回 null；任何节点类型都不会抛异常。
    /// </summary>
    public static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
                return match;
            node = GetParent(node);
        }
        return null;
    }
}
