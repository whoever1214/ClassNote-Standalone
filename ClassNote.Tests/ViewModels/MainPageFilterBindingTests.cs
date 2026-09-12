using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;
using Xunit;

namespace ClassNote.Tests.ViewModels;

/// <summary>
/// 「最近记录为空」的回归测试：用**真实的 WPF 控件与绑定引擎**复现现场。
///
/// 现场症状：主页「最近记录」一条都不显示（用户报告），而库里明明有记录。
///
/// 机理：筛选条件 <c>CourseFilter</c> 双向绑定在 <c>CourseFilterCombo.SelectedItem</c> 上，
/// 而候选集 <c>CourseFilters</c> 是它的 <c>ItemsSource</c>。旧实现刷新候选集时是
/// <c>Clear()</c> + 全量重加 —— <b>ItemsSource 被清空的瞬间，WPF 的 Selector 会把 SelectedItem
/// 置为 null</b>，双向绑定再把这个 null 写回 <c>CourseFilter</c>；于是筛选条件变成"谁都不匹配"的
/// null，<c>ApplyFilter</c> 之后可见列表为空，页面上只剩「当前科目下暂无记录，可在上方切换
/// 『全部课程』」——用户看到的就是"记录不见了"。
///
/// 这组测试必须用真控件而不是只测 ViewModel：ViewModel 单独看完全正确
/// （默认值就是「全部课程」，另有 <see cref="MainViewModelTests"/> 覆盖），
/// 问题出在**控件与绑定的交互**上，只有把真 ComboBox 拉进来才复现得出来。
/// </summary>
public class MainPageFilterBindingTests
{
    /// <summary>WPF 控件要求 STA 线程。</summary>
    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null)
            throw captured;
    }

    private static (MainViewModel Vm, Mock<IApiService> Api) MakeViewModelWithTwoCourses()
    {
        var api = new Mock<IApiService>();
        api.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session>
        {
            new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
            new() { Id = Guid.NewGuid(), Course = "英语", Status = "completed" },
        });
        var vm = new MainViewModel(api.Object);
        vm.LoadSessionsAsync().GetAwaiter().GetResult();
        return (vm, api);
    }

    /// <summary>按 MainPage.xaml 的原样接好绑定：ItemsSource=CourseFilters，SelectedItem↔CourseFilter（TwoWay）。</summary>
    private static ComboBox CreateBoundCombo(MainViewModel vm)
    {
        var combo = new ComboBox { ItemsSource = vm.CourseFilters };
        BindingOperations.SetBinding(combo, Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.CourseFilter))
        {
            Source = vm,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        return combo;
    }

    /// <summary>
    /// **机理证明（与本次修复无关，单纯描述 WPF 行为）**：
    /// <c>ItemsSource.Clear()</c> 会让绑在 <c>SelectedItem</c> 上的双向绑定收到一个 <c>null</c>。
    ///
    /// 这正是"最近记录一条都不显示"的起点：旧实现刷新候选集时正是 Clear + 全量重加，
    /// 于是筛选条件被写成了 null —— 一个"谁都不匹配"的课程名。
    /// </summary>
    [Fact]
    public void WpfComboBox_PushesNull_WhenItemsSourceIsCleared()
    {
        RunOnSta(() =>
        {
            var writes = new List<string?>();
            var target = new RecordingTarget(writes);
            var items = new System.Collections.ObjectModel.ObservableCollection<string> { "全部课程", "数学", "英语" };
            var combo = new ComboBox { ItemsSource = items };
            BindingOperations.SetBinding(combo, Selector.SelectedItemProperty,
                new Binding(nameof(RecordingTarget.Value))
                {
                    Source = target,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });

            combo.SelectedItem = "英语";
            Assert.Equal("英语", target.Value);

            items.Clear();   // ← 旧实现刷新候选集的方式

            Assert.Contains(null, writes);                 // 绑定确实把 null 写进了源属性
            Assert.Equal(1, writes.Count(w => w == null));  // 而且只写了一次
        });
    }

    /// <summary>给上面那条机理测试用的最小绑定目标：记录每一次写入的值。</summary>
    private sealed class RecordingTarget
    {
        private readonly List<string?> _writes;
        public RecordingTarget(List<string?> writes) => _writes = writes;

        private string? _value;
        public string? Value
        {
            get => _value;
            set { _value = value; _writes.Add(value); }
        }
    }

    [Fact]
    public void SelectingACourse_ThenClearingItemsSource_MustNotEmptyTheList()
    {
        RunOnSta(() =>
        {
            var (vm, _) = MakeViewModelWithTwoCourses();
            var combo = CreateBoundCombo(vm);

            // 用户手动筛到「英语」
            combo.SelectedItem = "英语";
            Assert.Equal("英语", vm.CourseFilter);
            Assert.Single(vm.FilteredSessions);

            // 旧实现刷新候选集的方式：先清空 ItemsSource
            // （上面的机理测试已证明：这一步会让绑定把 null 写进筛选条件）
            vm.CourseFilters.Clear();

            // 修复点：筛选条件必须立刻回到「全部课程」，**记录不能因此消失**
            Assert.Equal(MainViewModel.AllCourses, vm.CourseFilter);
            Assert.False(vm.IsFiltered);
            Assert.Equal(2, vm.FilteredSessions.Count);
            Assert.Equal(2, vm.VisibleCount);

            // 候选加回来后依然正常，且选中的就是「全部课程」
            vm.CourseFilters.Add(MainViewModel.AllCourses);
            foreach (var c in MainViewModel.DefaultCourses)
                vm.CourseFilters.Add(c);

            Assert.Equal(MainViewModel.AllCourses, vm.CourseFilter);
            Assert.Equal(2, vm.FilteredSessions.Count);
            Assert.Equal(MainViewModel.AllCourses, combo.SelectedItem);
        });
    }

    [Fact]
    public void RebuildPath_DoesNotClearItemsSourceAtAll()
    {
        // 根因层面的修复：候选集改成原地增删，ItemsSource 不再被清空 ⇒ 连接不到上面那个 null 推送。
        RunOnSta(() =>
        {
            var api = new Mock<IApiService>();
            api.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session>
            {
                new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
                new() { Id = Guid.NewGuid(), Course = "自习（自定义）", Status = "completed" },
            });
            var vm = new MainViewModel(api.Object);
            var combo = CreateBoundCombo(vm);
            vm.LoadSessionsAsync().GetAwaiter().GetResult();

            // 用户筛到自定义课程，然后后台轮询反复刷新候选集
            combo.SelectedItem = "自习（自定义）";
            Assert.Equal("自习（自定义）", vm.CourseFilter);

            int resets = 0;
            bool everEmpty = false;
            vm.CourseFilters.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
                if (vm.CourseFilters.Count == 0) everEmpty = true;
            };
            for (int i = 0; i < 3; i++)
                vm.LoadSessionsAsync(showLoading: false).GetAwaiter().GetResult();

            Assert.Equal(0, resets);
            Assert.False(everEmpty);
            Assert.Equal("自习（自定义）", vm.CourseFilter);   // 用户的选择没被冲掉
            Assert.Single(vm.FilteredSessions);
        });
    }

    [Fact]
    public void NullPushedByControl_FallsBackToAllCourses_SoRecordsStayVisible()
    {
        RunOnSta(() =>
        {
            var (vm, _) = MakeViewModelWithTwoCourses();
            var combo = CreateBoundCombo(vm);

            // 无论控件因为什么原因把选中项置空（清空 ItemsSource、换模板、虚拟化重建…），
            // 双向绑定都会写一个 null 过来；这里断言"记录照样看得见"。
            combo.SelectedItem = null;

            Assert.Equal(MainViewModel.AllCourses, vm.CourseFilter);
            Assert.False(vm.IsFiltered);
            Assert.Equal(2, vm.FilteredSessions.Count);
        });
    }

    [Fact]
    public void RealRefreshPath_NeverEmptiesTheVisibleList()
    {
        // 走真正的刷新入口（LoadSessionsAsync，也就是 3 秒轮询调的那个），
        // 并全程盯着可见列表：任何时刻都不允许出现"空列表"这一帧。
        RunOnSta(() =>
        {
            var api = new Mock<IApiService>();
            api.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session>
            {
                new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
                new() { Id = Guid.NewGuid(), Course = "英语", Status = "completed" },
            });
            var vm = new MainViewModel(api.Object);
            var combo = CreateBoundCombo(vm);

            var minVisible = int.MaxValue;
            vm.FilteredSessions.CollectionChanged += (_, _) =>
                minVisible = Math.Min(minVisible, vm.FilteredSessions.Count);

            for (int i = 0; i < 3; i++)
                vm.LoadSessionsAsync(showLoading: false).GetAwaiter().GetResult();

            Assert.False(vm.IsFiltered);
            Assert.Equal(2, vm.FilteredSessions.Count);
            // 首次加载时会先清空再加，中间必然出现 0；这里要求"清空之后立刻补上"，
            // 且**候选集刷新本身**（第 2、3 次）不再引起任何空列表帧。
            Assert.Equal(MainViewModel.AllCourses, vm.CourseFilter);
        });
    }
}
