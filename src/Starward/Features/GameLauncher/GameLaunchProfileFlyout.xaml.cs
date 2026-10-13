using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace Starward.Features.GameLauncher;


/// <summary>
/// 启动预设选择面板。同时用于两个地方：
/// <list type="bullet">
/// <item>启动器页面「当前预设名」按钮的弹出浮窗（<see cref="DisplayMode.Flyout"/>）；</item>
/// <item>游戏选择器里悬停游戏图标 / 区服项时弹出的浮窗（<see cref="DisplayMode.Panel"/>）。</item>
/// </list>
/// 内容为一个 <see cref="ListView"/>：整行的悬停高亮和当前预设的选中高亮都由列表项自己画，
/// 点整行即切换当前预设。面板下面只有一个按钮：列表显示时是「编辑预设」，
/// 打开 <see cref="GameLaunchProfileWindow"/> 增删改名和排序；只有一个预设时是「新增预设」，
/// 直接加一个新的。拖动排序和改名都不在面板里做了。
/// </summary>
[INotifyPropertyChanged]
public sealed partial class GameLaunchProfileFlyout : UserControl
{


    /// <summary>
    /// 面板的呈现方式。
    /// </summary>
    public enum DisplayMode
    {
        /// <summary>作为 Flyout 的内容，自带外框和圆角。</summary>
        Flyout,
        /// <summary>内嵌在游戏选择器里的浮层，不带外框（外层容器已有背景）。</summary>
        Panel,
    }


    public GameLaunchProfileFlyout()
    {
        this.InitializeComponent();
    }



    public DisplayMode Mode { get; set; } = DisplayMode.Flyout;


    /// <summary>
    /// 预设列表。<see cref="Refresh"/> 每次都会换成一个新的集合实例（并通知本属性），
    /// 两个列表模式的 <c>ItemsSource</c> 都跟着换人、重新生成行；顺序由
    /// <see cref="GameLaunchProfileWindow"/> 拖动后写回存储，这里不再改顺序。
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<GameLaunchProfile> Profiles { get; set; } = new();


    /// <summary>
    /// 预设列表是否显示，同时也是分隔线的显示条件。区服只有一个预设时整份列表都收起来，
    /// 面板里只剩下面那一个按钮。数量变化由 <see cref="UpdateProfileListVisibility"/> 通知。
    /// </summary>
    public bool IsProfileListVisible
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                // 按钮的文字和图标跟着列表的显隐换，见 AddOrEditProfileText / AddOrEditProfileGlyph。
                OnPropertyChanged(nameof(AddOrEditProfileText));
                OnPropertyChanged(nameof(AddOrEditProfileGlyph));
            }
        }
    }


    /// <summary>
    /// 底部按钮的文字：有列表时进编辑窗口（「编辑预设」），没列表时直接新增（「新增预设」）。
    /// 按钮的提示文字也绑在这个属性上，两者永远是同一句话。
    /// </summary>
    public string AddOrEditProfileText => IsProfileListVisible
        ? Lang.GameLauncherSettingDialog_EditProfile
        : Lang.GameLauncherSettingDialog_NewProfile;


    /// <summary>
    /// 底部按钮的图标：编辑 <c>E70F</c>，新增 <c>E710</c>。
    /// </summary>
    public string AddOrEditProfileGlyph => IsProfileListVisible ? "\uE70F" : "\uE710";


    /// <summary>
    /// 当前选中的预设内部名，用于让列表选中那一行。没有记录时为 null。
    /// </summary>
    public string? ActiveProfileId { get; set; }


    /// <summary>
    /// 面板所属的游戏区服。
    /// </summary>
    public GameBiz CurrentGameBiz { get; set; }


    /// <summary>
    /// 用户选中了某个预设。
    /// </summary>
    public event EventHandler<GameLaunchProfile>? ProfileSelected;


    /// <summary>
    /// 新增了一个预设，参数是预设的内部名。
    /// </summary>
    public event EventHandler<string>? ProfileAdded;


    /// <summary>
    /// 开始调整预设顺序。排序现在在 <see cref="GameLaunchProfileWindow"/> 里做，
    /// 窗口打开时由面板发这个事件，宿主据此在编辑期间不要自动收起面板。
    /// </summary>
    public event EventHandler? DragStarted;


    /// <summary>
    /// 调整预设顺序结束。宿主据此恢复「鼠标移开就收起」的计时。
    /// </summary>
    public event EventHandler? DragCompleted;



    /// <summary>
    /// 按当前区服重新载入预设列表。应在弹出面板之前调用。
    /// <para>
    /// 当前预设以存储里的记录为准：宿主传来的内部名失效（或为 null）时回落到
    /// <see cref="GameLaunchProfileStore.GetCurrentProfile"/>，保证列表里选中的那一行
    /// 永远是存储的当前预设。
    /// </para>
    /// </summary>
    public void Refresh(GameBiz gameBiz, string? activeProfileId)
    {
        CurrentGameBiz = gameBiz;
        ActiveProfileId = GameLaunchProfileStore.GetProfile(gameBiz, activeProfileId)?.Id
                          ?? GameLaunchProfileStore.GetCurrentProfile(gameBiz).Id;
        var profiles = GameLaunchProfileStore.GetProfiles(gameBiz);
        // 列表在重建期间会自己清掉选中项并触发一次 SelectionChanged，先摘掉处理函数。
        ListView_Profiles_FlyoutMode.SelectionChanged -= ListView_Profiles_SelectionChanged;
        ListView_Profiles_PanelMode.SelectionChanged -= ListView_Profiles_SelectionChanged;
        try
        {
            // 换一个新的集合实例（而不是 Clear 之后重加同一批对象）：两个列表的 ItemsSource
            // 都会接到新实例，行会被重新生成，预设名按模型当前值重画，不会留在上一次打开时的旧名字上。
            var rows = new System.Collections.ObjectModel.ObservableCollection<GameLaunchProfile>();
            foreach (GameLaunchProfile profile in profiles)
            {
                // 高亮跟着内部名走，与显示序号（Index）无关。
                profile.IsActive = string.Equals(profile.Id, ActiveProfileId, StringComparison.Ordinal);
                rows.Add(profile);
            }
            Profiles = rows;
            OnPropertyChanged(nameof(Profiles));
            UpdateProfileListVisibility();
            UpdateModeVisibility();
            // 选中项由列表自己画高亮，这里只在重建之后把选中项摆到当前预设上。
            SynchronizeSelection();
        }
        finally
        {
            ListView_Profiles_FlyoutMode.SelectionChanged += ListView_Profiles_SelectionChanged;
            ListView_Profiles_PanelMode.SelectionChanged += ListView_Profiles_SelectionChanged;
        }
    }



    private void UpdateModeVisibility()
    {
        Content_FlyoutMode.Visibility = Mode is DisplayMode.Flyout ? Visibility.Visible : Visibility.Collapsed;
        Content_PanelMode.Visibility = Mode is DisplayMode.Panel ? Visibility.Visible : Visibility.Collapsed;
    }



    /// <summary>
    /// 预设只有一个时收起整份列表（连同分隔线），只留底部那一个按钮。
    /// <see cref="Profiles"/> 是普通属性，列表的显示与否要自己发属性通知才会刷新。
    /// </summary>
    private void UpdateProfileListVisibility()
    {
        IsProfileListVisible = Profiles.Count > 1;
    }



    /// <summary>
    /// 两种模式各有一份列表实例，只有可见的那一份需要跟着 <see cref="ActiveProfileId"/> 选中。
    /// </summary>
    private void SynchronizeSelection()
    {
        ListView_Profiles_FlyoutMode.SelectedItem = Profiles.FirstOrDefault(x => x.IsActive);
        ListView_Profiles_PanelMode.SelectedItem = Profiles.FirstOrDefault(x => x.IsActive);
    }



    /// <summary>
    /// 列表选中项变化：跟着更新当前预设内部名。这里不弹事件、不写存储，
    /// 切换预设由 <see cref="ListView_Profiles_ItemClick"/> 负责，两边不会重复触发。
    /// 重建列表期间处理函数被摘掉，所以这里不会和 <see cref="Refresh"/> 打架。
    /// </summary>
    private void ListView_Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is GameLaunchProfile profile
            && !string.Equals(profile.Id, ActiveProfileId, StringComparison.Ordinal))
        {
            ActiveProfileId = profile.Id;
        }
    }



    /// <summary>
    /// 点击整行即切换到该预设。选中高亮由列表自己画，这里只发事件。
    /// </summary>
    private void ListView_Profiles_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameLaunchProfile profile)
        {
            ActiveProfileId = profile.Id;
            ProfileSelected?.Invoke(this, profile);
        }
    }



    /// <summary>
    /// 面板下面那一个按钮：
    /// 列表显示时打开 <see cref="GameLaunchProfileWindow"/> 编辑预设（增删改名、拖动排序）；
    /// 只有一个预设时直接新增一个，并把新预设切成当前预设。
    /// </summary>
    private async void Button_AddOrEditProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (IsProfileListVisible)
            {
                await OpenEditorAsync();
            }
            else
            {
                AddProfile();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }



    /// <summary>
    /// 新增一个预设并把它切成当前预设，见 <see cref="GameLaunchProfileStore.AddProfile"/>。
    /// </summary>
    private void AddProfile()
    {
        GameLaunchProfile profile = GameLaunchProfileStore.AddProfile(CurrentGameBiz);
        Refresh(CurrentGameBiz, profile.Id);
        ProfileAdded?.Invoke(this, profile.Id);
    }



    /// <summary>
    /// 打开预设编辑窗口。窗口是独立的普通窗口（非模态），打开期间面板不收起：
    /// 用 <see cref="DragStarted"/> / <see cref="DragCompleted"/> 告诉宿主「正在编辑」。
    /// 窗口关掉之后重新读一次存储，当前预设的显隐和名字都可能变了。
    /// </summary>
    private async Task OpenEditorAsync()
    {
        // 同一时刻只留一个编辑窗口：宿主（游戏选择器）里可能已经开着一个。
        if (_editorWindow is not null)
        {
            _editorWindow.Activate();
            return;
        }
        try
        {
            DragStarted?.Invoke(this, EventArgs.Empty);
            _editorWindow = new GameLaunchProfileWindow();
            _editorWindow.Closed += EditorWindow_Closed;
            _editorWindow.Show(CurrentGameBiz);
            // 等窗口真的显示出来再返回，宿主收起面板的计时在这期间不会跑。
            await Task.Delay(1);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            OnEditorWindowClosed();
        }
    }



    private GameLaunchProfileWindow? _editorWindow;


    private void EditorWindow_Closed(object? sender, WindowEventArgs args) => OnEditorWindowClosed();



    /// <summary>
    /// 编辑窗口关闭后的收尾：事件回调和异常路径的手动调用共用这一段。
    /// </summary>
    private void OnEditorWindowClosed()
    {
        try
        {
            if (_editorWindow is GameLaunchProfileWindow window)
            {
                window.Closed -= EditorWindow_Closed;
            }
            _editorWindow = null;
            // 编辑窗口可能删掉了当前预设：存储那边已经把当前预设挪到剩下的第一个，
            // 这里按存储的记录重新刷一遍就行。
            Refresh(CurrentGameBiz, GameLaunchProfileStore.GetCurrentProfile(CurrentGameBiz).Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }



}
