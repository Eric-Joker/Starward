using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Frameworks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Graphics;


namespace Starward.Features.GameLauncher;


/// <summary>
/// 预设编辑窗口。从启动预设浮窗（<see cref="GameLaunchProfileFlyout"/>）底部那个按钮打开，
/// 一个区服的预设在这里改名、删除、新增、拖动排序。
/// <para>
/// 外观：亚克力（<c>DesktopAcrylicBackdrop</c>）模糊背景 + <c>ExtendsContentIntoTitleBar</c> 的自绘标题栏，
/// 只留标题和系统那颗关闭按钮（最小化 / 最大化 / 拉伸都在 <see cref="InitializeWindow"/> 里关掉），
/// 和 <c>ImageBatchConvertWindow</c> 是同一套做法。
/// </para>
/// <para>
/// 交互：改名 / 删除 / 新增 / 排序都是立刻写 <see cref="GameLaunchProfileStore"/> 的，
/// 没有「确定 / 取消」两段式——关闭窗口就是完成。删掉当前预设时，
/// <see cref="GameLaunchProfileStore.RemoveProfile"/> 会把当前预设挪到剩下的第一个。
/// </para>
/// </summary>
[INotifyPropertyChanged]
public sealed partial class GameLaunchProfileWindow : WindowEx
{


    private readonly ILogger<GameLaunchProfileWindow> _logger = AppConfig.GetLogger<GameLaunchProfileWindow>();


    /// <summary>窗口初始尺寸（逻辑像素）。列表再长也在这个框里滚。</summary>
    private const int WindowWidth = 420;

    private const int WindowHeight = 520;


    /// <summary>标题栏高度，和自绘标题栏那一行一致。</summary>
    private const int TitleBarHeight = 32;


    /// <summary>行模板里承载预设名的 TextBlock 的 <c>x:Name</c>，见 GameLaunchProfileWindow.xaml。</summary>
    private const string ProfileNameTextBlockName = "TextBlock_ProfileName";


    /// <summary>行模板里承载预设名的 TextBlock 上的 <c>Tag</c>；不依赖命名范围，图标上也没有。</summary>
    private const string ProfileNameTag = "ProfileName";


    public GameLaunchProfileWindow()
    {
        InitializeComponent();
        InitializeWindow();
    }




    private void InitializeWindow()
    {
        try
        {
            Title = Lang.GameLauncherSettingDialog_LaunchProfile;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.IconShowOptions = IconShowOptions.ShowIconAndSystemMenu;
            // 拖动区域只覆盖标题栏左边那一段（标题），右边留给系统画的关闭按钮，
            // 不然点关闭会变成拖窗口。宽度按标题文字算，文字再长也不会盖到按钮上。
            AppWindow.TitleBar.SetDragRectangles([new RectInt32(0, 0, 200, (int)(TitleBarHeight * UIScale))]);
            // 亚克力背景：和浮窗面板（CustomAcrylicBrush）同一套观感，整个窗口共用一层系统模糊。
            // 必须在窗口显示之前设好，窗口内容里不铺任何画刷（见 .xaml 的根元素注释）。
            SystemBackdrop = new DesktopAcrylicBackdrop();
            AdaptTitleBarButtonColorToActuallTheme();
            SetIcon();
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // 编辑预设是个小对话框，不要最小化 / 最大化，也不给拉伸
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }
            Closed += GameLaunchProfileWindow_Closed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize launch profile window");
        }
    }




    /// <summary>
    /// 该窗口编辑哪个区服的预设。
    /// </summary>
    public GameBiz CurrentGameBiz { get; private set; }


    /// <summary>
    /// 窗口里显示的预设列表。顺序就是存储里的顺序，拖动排序由列表直接改这个集合，
    /// 拖完由 <see cref="ListView_Profiles_DragItemsCompleted"/> 按屏幕上的顺序写回存储。
    /// <para>
    /// 改名 / 新增 / 删除 / 拖动排序都会把整个属性换成按存储排好的新实例（见 <see cref="ReloadProfiles"/>），
    /// 行全部重新生成、按各自的名字重画——拖动只换行的先后，不改任何预设的名字。
    /// </para>
    /// </summary>
    public ObservableCollection<GameLaunchProfile> Profiles { get; private set; } = new();


    /// <summary>
    /// 某一行的删除按钮是否可用：只剩一个预设时不允许删，和存储里的规则一致。
    /// </summary>
    public bool CanDelete => Profiles.Count > 1;




    /// <summary>
    /// 按区服打开窗口。显示位置跟着主窗口居中，不抢焦点时也留在主窗口附近。
    /// </summary>
    public void Show(GameBiz gameBiz)
    {
        try
        {
            CurrentGameBiz = gameBiz;
            ReloadProfiles();
            // 和 MainWindow 一样按屏幕工作区居中：编辑窗口属于「当前窗口的附属窗口」，
            // 跟主窗口同一个屏，用户不用去别的屏找它。
            CenterInScreen(WindowWidth, WindowHeight);
            base.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show launch profile window {GameBiz}", gameBiz);
        }
    }




    /// <summary>
    /// 从存储里读一遍预设，整体换成一个新集合，每一行都会重新生成、按模型的当前值重画。
    /// <see cref="GameLaunchProfileStore.GetProfiles"/> 返回的就是存储缓存里的活对象，
    /// 所以这里不做拷贝：只保证窗口每一次都按这些对象的当前值把行重画一遍。
    /// <para>
    /// 打开窗口、改名 / 新增 / 删除、以及拖动排序之后都走这里（和启动预设浮窗同一套刷新策略）。
    /// 拖动时落盘已经先做完，所以重建出来的顺序就是用户拖完的顺序，名字则跟着各自的预设走。
    /// </para>
    /// </summary>
    private void ReloadProfiles()
    {
        try
        {
            List<GameLaunchProfile> profiles = GameLaunchProfileStore.GetProfiles(CurrentGameBiz);
            var rows = new ObservableCollection<GameLaunchProfile>();
            foreach (GameLaunchProfile profile in profiles)
            {
                // 只剩一个预设时行内的删除按钮要置灰，所以把这个状态同步到每一项上
                profile.CanDelete = profiles.Count > 1;
                rows.Add(profile);
            }
            Profiles = rows;
            OnPropertyChanged(nameof(Profiles));
            OnPropertyChanged(nameof(CanDelete));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reload launch profiles {GameBiz}", CurrentGameBiz);
        }
    }




    private void Button_AddProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // AddProfile 把新预设加在末尾，并把它设成当前预设。
            GameLaunchProfileStore.AddProfile(CurrentGameBiz);
            ReloadProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add launch profile {GameBiz}", CurrentGameBiz);
        }
    }




    private async void Button_RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not GameLaunchProfile profile)
            {
                return;
            }
            var textBox = new TextBox
            {
                Text = profile.DisplayName,
                MaxLength = 32,
                SelectionStart = profile.DisplayName.Length,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = Lang.GameLauncherSettingDialog_ProfileName,
                Content = textBox,
                PrimaryButtonText = Lang.Common_Confirm,
                CloseButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() is ContentDialogResult.Primary)
            {
                // 存储里就地改了同一个对象的 Name（名字属于预设本身，跟位置无关），
                // 改完按存储重读一遍列表，这一行就会显示新名字。
                GameLaunchProfileStore.RenameProfile(CurrentGameBiz, profile.Id, textBox.Text);
                ReloadProfiles();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rename launch profile {GameBiz}", CurrentGameBiz);
        }
    }




    private async void Button_DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not GameLaunchProfile profile)
            {
                return;
            }
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = Lang.Common_Delete,
                Content = profile.DisplayName,
                PrimaryButtonText = Lang.Common_Delete,
                CloseButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
            {
                return;
            }
            // RemoveProfile 在只剩下最后一个预设时返回 null，界面按钮也已经置灰，这里再兜一层。
            if (GameLaunchProfileStore.RemoveProfile(CurrentGameBiz, profile.Id) is null)
            {
                return;
            }
            ReloadProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete launch profile {GameBiz}", CurrentGameBiz);
        }
    }




    /// <summary>
    /// 行内按钮自己吃掉按下事件：不然整行会把这一下当成拖动排序的开始。
    /// </summary>
    private void Button_RowAction_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }




    private void Button_Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }




    #region 拖动排序


    private void ListView_Profiles_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // 排序过程中不要因为失焦之类的意外关掉窗口，这里不做别的，只占一个处理函数。
    }



    /// <summary>
    /// 拖动结束：按用户拖完之后的**显示顺序**落盘，然后**按存储重新渲染一遍行**（和启动预设浮窗同一套做法）。
    /// <para>
    /// 顺序取 <see cref="GetDisplayedProfileOrder"/>（屏幕上的先后），不假设框架一定把
    /// <see cref="Profiles"/> 回写成同一个顺序。落盘同步做完，和账号列表
    /// （<c>AccountCenterPanel.ListView_Accounts_DragItemsCompleted</c>）一样不留延迟。
    /// </para>
    /// <para>
    /// 行里的文字只靠通知 / 绑定在这张表里不可靠（长生命周期的行容器实测不重读），
    /// 所以先做一次确定性赋值（<see cref="WriteProfileRowTexts"/>，按 Tag 找名字元素），
    /// 再照浮窗的做法 <see cref="ReloadProfiles"/> 整体重读：换一个新集合、行全部重建。
    /// 落盘在前面已经完成，所以重建出来的顺序一定就是用户拖完的顺序，不会回退。
    /// 这里全部同步执行，不排任何延迟任务。
    /// </para>
    /// </summary>
    private void ListView_Profiles_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            List<GameLaunchProfile> order = GetDisplayedProfileOrder() ?? Profiles.ToList();
            SyncProfileOrderToStore(order);
            // 先把当前顺序下每行的名字直接写一遍（同帧兜底，按 Tag 找名字元素）。
            WriteProfileRowTexts();
            // 再按存储整表重读（和启动预设浮窗同一套做法）：名字属于预设本身，
            // 重排只换行的先后，行按各自的名字重画——两行互换后第 1 行显示的就是原来第 2 行的名字。
            ReloadProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reorder launch profiles {GameBiz}", CurrentGameBiz);
        }
    }



    /// <summary>
    /// 把当前屏幕顺序下每一行的名字直接写进行里的预设名 <see cref="TextBlock"/>。
    /// <para>
    /// 名字元素按 <c>Tag="ProfileName"</c> 找（见 <see cref="FindProfileNameTextBlock"/>），
    /// **不找"第一个 TextBlock"**：行首的拖动手柄是 <see cref="FontIcon"/>，它的控件模板里也是用
    /// TextBlock 画字形（<c>App.xaml</c> 给 FontIcon 设的 Fluent Icons 字体就是靠它继承下去的），
    /// 按"第一个"取会把预设名写进图标里——图标字体的 TextBlock 显示中文就是乱码，名字那一行反而没改。
    /// 找不到目标时什么都不做，绝不写别的元素。
    /// </para>
    /// <para>
    /// 只改文字和无障碍名称，不动 <c>ItemsSource</c>、不重建容器、不留延迟任务。
    /// </para>
    /// </summary>
    private void WriteProfileRowTexts()
    {
        if (ListView_Profiles.ItemsPanelRoot is not Panel panel)
        {
            return;
        }
        foreach (UIElement child in panel.Children)
        {
            if (child is not ListViewItem container || container.Content is not GameLaunchProfile profile)
            {
                continue;
            }
            TextBlock? textBlock = FindProfileNameTextBlock(container);
            if (textBlock is not null)
            {
                textBlock.Text = profile.DisplayName;
                AutomationProperties.SetName(textBlock, profile.DisplayName);
            }
            // 整行的无障碍名称也刷成当前名字。
            AutomationProperties.SetName(container, profile.DisplayName);
        }
    }



    /// <summary>
    /// 行里承载预设名的那个 <see cref="TextBlock"/>。按顺序试三条路，**每一条都不会命中图标**：
    /// <list type="number">
    /// <item><c>Tag="ProfileName"</c>：不依赖命名范围，图标和按钮上都没有这个标记；</item>
    /// <item><c>x:Name</c>：在行模板根（<c>ContentTemplateRoot</c>）的命名范围里找（模板内的 x:Name 属于该模板的命名范围）；</item>
    /// <item>结构兜底：整棵子树里第一个 TextBlock，但**不下钻 <see cref="FontIcon"/>**（图标字形就是它模板里的 TextBlock）。</item>
    /// </list>
    /// 三条都找不到时返回 null，调用方什么都不做。
    /// </summary>
    private static TextBlock? FindProfileNameTextBlock(DependencyObject container)
    {
        if (FindTextBlockByTag(container, ProfileNameTag) is TextBlock byTag)
        {
            return byTag;
        }
        if (container is ContentControl contentControl && contentControl.ContentTemplateRoot is FrameworkElement contentRoot)
        {
            if (contentRoot.FindName(ProfileNameTextBlockName) is TextBlock fromTemplateRoot)
            {
                return fromTemplateRoot;
            }
        }
        return FindTextBlockOutsideFontIcon(container);
    }



    private static TextBlock? FindTextBlockByTag(DependencyObject? node, string tag)
    {
        if (node is null)
        {
            return null;
        }
        if (node is TextBlock textBlock && textBlock.Tag is string value && string.Equals(value, tag, StringComparison.Ordinal))
        {
            return textBlock;
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            if (FindTextBlockByTag(VisualTreeHelper.GetChild(node, index), tag) is TextBlock found)
            {
                return found;
            }
        }
        return null;
    }



    /// <summary>
    /// 子树里第一个 TextBlock，但跳过 <see cref="FontIcon"/> 整棵子树：图标是用它模板里的 TextBlock 画字形的，
    /// 不能当预设名写。
    /// </summary>
    private static TextBlock? FindTextBlockOutsideFontIcon(DependencyObject? node)
    {
        if (node is null || node is FontIcon)
        {
            return null;
        }
        if (node is TextBlock textBlock)
        {
            return textBlock;
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            if (FindTextBlockOutsideFontIcon(VisualTreeHelper.GetChild(node, index)) is TextBlock found)
            {
                return found;
            }
        }
        return null;
    }



    /// <summary>
    /// 用户拖完之后的显示顺序：列表面板的子元素顺序就是屏幕上的先后，每个容器上挂着它正在显示的预设。
    /// 容器没生成全（虚拟化）或者和集合对不上时返回 null，调用方就按集合自己的顺序来，
    /// 宁可不改也不猜一个错的顺序。
    /// </summary>
    private List<GameLaunchProfile>? GetDisplayedProfileOrder()
    {
        Panel? panel = ListView_Profiles.ItemsPanelRoot;
        if (panel is null || panel.Children.Count != Profiles.Count)
        {
            return null;
        }
        var displayed = new List<GameLaunchProfile>(Profiles.Count);
        foreach (UIElement child in panel.Children)
        {
            GameLaunchProfile? profile = (child as ListViewItem)?.Content as GameLaunchProfile
                                         ?? (child as FrameworkElement)?.DataContext as GameLaunchProfile;
            // 同一个预设出现两次、或者出现集合里没有的项，说明这一屏的顺序不可信。
            if (profile is null || !Profiles.Contains(profile) || displayed.Contains(profile))
            {
                return null;
            }
            displayed.Add(profile);
        }
        return displayed;
    }



    /// <summary>
    /// 按给定的顺序，把存储里的那一项挪到对应下标。挪过的位置已经固定，
    /// 所以碰到第一个对不上的位置就说明后面的必然也不对（项数相同、内部名唯一），
    /// 此时把剩下的移完，最后统一保存一次。<see cref="GameLaunchProfileStore.MoveProfile"/>
    /// 内部会重排序号（Normalize），行里显示的「预设 N」跟着这个顺序走。
    /// </summary>
    private void SyncProfileOrderToStore(List<GameLaunchProfile> order)
    {
        List<GameLaunchProfile> stored = GameLaunchProfileStore.GetProfiles(CurrentGameBiz);
        bool changed = false;
        for (int index = 0; index < order.Count; index++)
        {
            GameLaunchProfile profile = order[index];
            int current = IndexOfProfile(stored, profile);
            if (current < 0)
            {
                // 顺序里出现了存储里没有的项：对不上，宁可不写，也不写一个错顺序进去。
                return;
            }
            if (current != index)
            {
                GameLaunchProfileStore.MoveProfile(CurrentGameBiz, current, index);
                changed = true;
            }
        }
        if (changed)
        {
            // MoveProfile 每次都自己存了，这里只是再确认一次（顺序没动时不必写）。
            GameLaunchProfileStore.Save(CurrentGameBiz);
        }
    }



    /// <summary>
    /// 按引用取下标，取不到时按内部名再找一次（防止调用方给来的对象不是存储里的那一份）。
    /// </summary>
    private static int IndexOfProfile(List<GameLaunchProfile> list, GameLaunchProfile profile)
    {
        int index = list.IndexOf(profile);
        return index >= 0 ? index : list.FindIndex(x => string.Equals(x.Id, profile.Id, StringComparison.Ordinal));
    }


    #endregion




    /// <summary>
    /// 关闭窗口：改名 / 删除 / 新增 / 排序都已经写进存储了，这里只把结果广播出去，
    /// 让启动器页面和游戏选择器刷新自己那一份显示（当前预设可能已经换人了）。
    /// </summary>
    private void GameLaunchProfileWindow_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            Closed -= GameLaunchProfileWindow_Closed;
            WeakReferenceMessenger.Default.Send(new GameLaunchProfileChangedMessage());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Close launch profile window");
        }
    }



}


/// <summary>
/// 预设被改过（改名 / 删除 / 新增 / 排序）之后广播一次，
/// <see cref="GameLauncherPage"/> 收到后重新读一遍当前预设名。
/// </summary>
internal sealed class GameLaunchProfileChangedMessage;
