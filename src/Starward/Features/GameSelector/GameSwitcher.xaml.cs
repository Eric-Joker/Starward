using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using Starward.Features.ViewHost;
using Starward.GameInstall;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;


namespace Starward.Features.GameSelector;

[INotifyPropertyChanged]
public sealed partial class GameSwitcher : UserControl
{

    private readonly ILogger<GameSwitcher> _logger = AppConfig.GetLogger<GameSwitcher>();

    private readonly HoYoPlayService _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();

    public event EventHandler<PinnedGameIcon>? CurrentGameChanged;

    public ObservableCollection<PinnedGameIcon> PinnedGameIcons { get; set => SetProperty(ref field, value); } = new();

    public PinnedGameIcon? CurrentGameIcon { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 游戏图标的按住拖动排序。0.19 上游用系统拖放（ListView.CanReorderItems），
    /// 非打包应用提权后系统拖放不可用，这里换成自己实现的拖动。
    /// </summary>
    private readonly GameIconDragReorder _gameIconDragReorder;



    public GameSwitcher()
    {
        InitializeComponent();
        _gameIconDragReorder = new GameIconDragReorder(ListView_GameIcons, MoveGameIcon);
        InitializeSwitcher();
        WeakReferenceMessenger.Default.Register<PinGameIconMessage>(this, HandlePinGameIconMessage);
        WeakReferenceMessenger.Default.Register<MainViewNavigateMessage>(this, HandleMainViewNavigateMessage);
        WeakReferenceMessenger.Default.Register<EnabledLauncherConfigChangedMessage>(this, HandleEnabledLauncherConfigChangedMessage);
        WeakReferenceMessenger.Default.Register<SelectGameBizMessage>(this, HandleSelectGameBizMessage);
        // 点击系统通知带来的目标游戏在主窗口建好后才能切；Loaded 时主视图已经初始化完，切游戏的事件处理可以安全执行
        this.Loaded += GameSwitcher_Loaded;
    }



    private void GameSwitcher_Loaded(object sender, RoutedEventArgs e)
    {
        TryApplyPendingSelectGameBiz(clearIfMissing: false);
    }



    public void InitializeSwitcher()
    {
        try
        {
            PinnedGameIcons.CollectionChanged -= PinnedGameIcons_CollectionChanged;
            PinnedGameIcons.Clear();

            List<GameEntry> entries = GameEntryService.GetPinnedEntries();
            List<LauncherConfigModel> launchers = LauncherConfigModel.GetEnabled();
            foreach (var item in entries)
            {
                GameId gameId = new(item.GameId, item.GameBiz);
                LauncherConfigModel? launcher = launchers.FirstOrDefault(x => x.Id == item.LauncherId);
                if (launcher is null)
                {
                    continue;
                }
                GameInfo? gameInfo = _hoYoPlayService.GetCachedGameInfo(gameId);
                if (gameInfo is not null)
                {
                    PinnedGameIcons.Add(new PinnedGameIcon(launcher, gameInfo));
                }
            }
            CurrentGameIcon = PinnedGameIcons.FirstOrDefault(x => x.GameId.Id == AppConfig.CurrentGameId);
            if (CurrentGameIcon is not null)
            {
                CurrentGameIcon.IsSelected = true;
            }
            _ = UpdatePinnedGameIconsAsync(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize GameSwitcher.");
        }
        finally
        {
            PinnedGameIcons.CollectionChanged += PinnedGameIcons_CollectionChanged;
        }
    }



    private async Task UpdatePinnedGameIconsAsync(List<GameEntry> list)
    {
        await Task.Delay(1000);
        var launchers = LauncherConfigModel.GetEnabled();
        try
        {
            await _hoYoPlayService.UpdateLauncherClientMapAsync(launchers.Select(x => x.ToLauncherConfig()));
        }
        catch { }

        PinnedGameIcons.CollectionChanged -= PinnedGameIcons_CollectionChanged;
        try
        {
            int index = 0;
            foreach (var item in list)
            {
                GameId gameId = new(item.GameId, item.GameBiz);
                LauncherConfigModel? launcher = launchers.FirstOrDefault(x => x.Id == item.LauncherId);
                if (launcher is null)
                {
                    continue;
                }
                GameInfo? gameInfo = await _hoYoPlayService.GetGameInfoAsync(gameId);
                if (gameInfo is null)
                {
                    continue;
                }
                PinnedGameIcon? icon = PinnedGameIcons.FirstOrDefault(x => x.GameId == gameId);
                if (icon is null)
                {
                    icon = new PinnedGameIcon(launcher, gameInfo);
                    PinnedGameIcons.Insert(index, icon);
                }
                else
                {
                    icon.UpdateInfo(gameInfo);
                }
                index++;
            }
        }
        finally
        {
            PinnedGameIcons.CollectionChanged += PinnedGameIcons_CollectionChanged;
        }
        // 图标补全后把「当前游戏」重新选上：冷启动时缓存里还没有图标，InitializeSwitcher 里选不到，
        // 之前会导致即使 AppConfig.CurrentGameId 有值，第一次启动也停在「选择游戏」页。
        if (CurrentGameIcon is null && PinnedGameIcons.FirstOrDefault(x => x.GameId.Id == AppConfig.CurrentGameId) is PinnedGameIcon currentGame)
        {
            SelectGameIcon(currentGame);
        }
        // 图标补全后再试一次：启动时缓存里可能还没有目标游戏的信息
        TryApplyPendingSelectGameBiz(clearIfMissing: true);
    }


    private async void HandlePinGameIconMessage(object sender, PinGameIconMessage e)
    {
        try
        {
            GameId gameId = e.GameId;
            if (gameId.GameBiz == GameBiz.bh3_global)
            {
                gameId = new GameId(GameId.bh3_glb, GameBiz.bh3_global);
            }
            var icon = PinnedGameIcons.FirstOrDefault(x => x.GameId == gameId);
            if (e.IsPinned)
            {
                GameInfo? gameInfo = gameId as GameInfo ?? await _hoYoPlayService.GetGameInfoAsync(gameId);
                if (gameInfo is null)
                {
                    return;
                }
                if (icon is null)
                {
                    LauncherConfigModel? model = e.LauncherModel ?? LauncherConfigModel.FromId(e.LauncherConfig?.Id);
                    if (model is null)
                    {
                        return;
                    }
                    icon = new PinnedGameIcon(model, gameInfo);
                    PinnedGameIcons.Add(icon);
                }
                else
                {
                    icon.UpdateInfo(gameInfo);
                }
            }
            else if (icon is not null)
            {
                PinnedGameIcons.Remove(icon);
            }
        }
        catch { }
    }


    private void HandleMainViewNavigateMessage(object sender, MainViewNavigateMessage e)
    {
        try
        {
            if (e.GameId is not null)
            {
                CurrentGameIcon?.IsSelected = false;
                if (PinnedGameIcons.FirstOrDefault(x => x.GameId == e.GameId) is PinnedGameIcon icon)
                {
                    icon.IsSelected = true;
                    CurrentGameIcon = icon;
                }
            }
        }
        catch { }
    }


    private void HandleEnabledLauncherConfigChangedMessage(object sender, EnabledLauncherConfigChangedMessage e)
    {
        InitializeSwitcher();
    }



    /// <summary>
    /// 切到系统通知对应的游戏（点击通知打开主窗口时）。
    /// </summary>
    /// <param name="_">消息发送方（未使用）。</param>
    /// <param name="message">要切到的游戏区服。</param>
    private void HandleSelectGameBizMessage(object _, SelectGameBizMessage message)
    {
        try
        {
            ApplySelectGameBiz(message.GameBiz);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Select game biz {GameBiz}.", message.GameBiz);
        }
    }



    /// <summary>
    /// 切到指定游戏区服对应的固定图标；没固定这个游戏时不动。
    /// </summary>
    /// <param name="target">要切到的游戏区服。</param>
    private void ApplySelectGameBiz(GameBiz target)
    {
        GameBiz current = CurrentGameIcon?.GameId.GameBiz ?? default;
        GameBiz resolved = SelectGameBizMessage.ResolveTarget(current, target);
        if (PinnedGameIcons.FirstOrDefault(x => x.GameId.GameBiz == resolved) is PinnedGameIcon icon)
        {
            SelectGameIcon(icon);
        }
        else
        {
            _logger.LogWarning("Game {GameBiz} is not pinned; notification switch skipped.", resolved);
        }
    }



    /// <summary>
    /// 应用点击系统通知时记下的目标游戏；主窗口建好后才调用。
    /// </summary>
    /// <param name="clearIfMissing">图标补全后仍找不到目标时清掉记录，避免之后突然切过去。</param>
    private void TryApplyPendingSelectGameBiz(bool clearIfMissing)
    {
        if (App.PeekPendingSelectGameBiz() is not { } pending)
        {
            return;
        }
        GameBiz current = CurrentGameIcon?.GameId.GameBiz ?? default;
        GameBiz target = SelectGameBizMessage.ResolveTarget(current, pending);
        if (PinnedGameIcons.FirstOrDefault(x => x.GameId.GameBiz == target) is not PinnedGameIcon icon)
        {
            if (clearIfMissing)
            {
                App.ConsumePendingSelectGameBiz();
                _logger.LogWarning("Pending game {GameBiz} is not pinned; notification switch skipped.", target);
            }
            return;
        }
        App.ConsumePendingSelectGameBiz();
        SelectGameIcon(icon);
    }



    /// <summary>
    /// 选中一个固定游戏图标：与单击图标的观感一致，并保存当前游戏设置。
    /// </summary>
    /// <param name="icon">要选中的图标。</param>
    private void SelectGameIcon(PinnedGameIcon icon)
    {
        if (icon == CurrentGameIcon)
        {
            return;
        }
        CurrentGameIcon?.IsSelected = false;
        CurrentGameIcon = icon;
        icon.IsSelected = true;
        CurrentGameChanged?.Invoke(this, icon);
        AppConfig.CurrentGameId = icon.GameId.Id;
    }



    /// <summary>
    /// 待选游戏变更时，保存配置
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void PinnedGameIcons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            GameEntryService.SavePinnedIcons(PinnedGameIcons);
        }
        catch { }
    }



    /// <summary>
    /// 拖拽松手后移动待选游戏。用移除再插入而不是 Move：Move 会被 WinUI 折叠成 Reset，重建全部图标。
    /// 集合变化会触发 PinnedGameIcons_CollectionChanged，把新顺序写进 GameEntry 表。
    /// 悬停遮罩的复位在 GameIconDragReorder 里统一处理（拖动结束时按光标位置判定）。
    /// </summary>
    /// <param name="oldIndex">旧索引</param>
    /// <param name="newIndex">新索引</param>
    private void MoveGameIcon(int oldIndex, int newIndex)
    {
        PinnedGameIcon icon = PinnedGameIcons[oldIndex];
        // 只在插回后保存一次，缺了这个图标的中间状态不写入配置
        PinnedGameIcons.CollectionChanged -= PinnedGameIcons_CollectionChanged;
        PinnedGameIcons.RemoveAt(oldIndex);
        PinnedGameIcons.CollectionChanged += PinnedGameIcons_CollectionChanged;
        PinnedGameIcons.Insert(newIndex, icon);
    }



    /// <summary>
    /// 点击没有被选择的游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PinnedGameIcon icon)
        {
            SelectGameIcon(icon);
        }
    }



    /// <summary>
    /// 鼠标移入到游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PinnedGameIcon icon)
        {
            if (!icon.IsSelected)
            {
                icon.MaskOpacity = 0;
            }
        }
    }



    /// <summary>
    /// 鼠标移出游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PinnedGameIcon icon)
        {
            if (!icon.IsSelected)
            {
                icon.MaskOpacity = 1;
            }
        }
    }




    #region 启动预设


    /// <summary>
    /// 当前打开的启动预设浮窗。同一时刻只留一个，右键 / 长按另一个图标时先收掉旧的。
    /// </summary>
    private Flyout? _launchProfileFlyout;


    /// <summary>
    /// 右键游戏图标：弹出该区服的启动预设列表。0.19 上游没有预设概念，
    /// 这里把预设编辑入口挂在游戏图标上（完整启动器页面只显示当前预设名，见 StartGameButton）。
    /// </summary>
    private void Grid_GameIcon_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PinnedGameIcon icon)
        {
            e.Handled = true;
            ShowLaunchProfileFlyout(icon, fe);
        }
    }


    /// <summary>
    /// 长按游戏图标（触摸 / 触笔），等同于右键。
    /// </summary>
    private void Grid_GameIcon_Holding(object sender, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs e)
    {
        if (e.HoldingState is Microsoft.UI.Input.HoldingState.Started
            && sender is FrameworkElement fe
            && fe.DataContext is PinnedGameIcon icon)
        {
            e.Handled = true;
            ShowLaunchProfileFlyout(icon, fe);
        }
    }


    /// <summary>
    /// 弹出某个区服的预设列表。列表本身复用 <see cref="GameLaunchProfileFlyout"/> 的 Flyout 模式，
    /// 放在标准 <see cref="Flyout"/> 里：弹出层不参与主窗口布局，也不会被图标行的 60 高裁掉。
    /// </summary>
    private void ShowLaunchProfileFlyout(PinnedGameIcon icon, FrameworkElement anchor)
    {
        try
        {
            CloseLaunchProfileFlyout();
            GameBiz gameBiz = icon.GameId.GameBiz;
            var panel = new GameLaunchProfileFlyout { Mode = GameLaunchProfileFlyout.DisplayMode.Flyout };
            // Flyout 自带主题背景（含亚克力），面板自己那份画刷在弹出层的独立合成层里取不到采样、
            // 会退回接近纯黑的 FallbackColor，这里关掉，文字颜色才跟主题一致。
            panel.UseTransparentPanelBackground();
            panel.Refresh(gameBiz, AppConfig.GetCurrentLaunchProfileId(gameBiz));
            var flyout = new Flyout
            {
                Content = panel,
                Placement = FlyoutPlacementMode.Bottom,
                // 用紧凑 presenter（Padding=0）：默认的 16,15,16,17 会把面板外框顶成 22，
                // 行不贴边、整块比旧版宽高各多 32，看起来就像个没样式的默认 Flyout。
                FlyoutPresenterStyle = App.Current.Resources["LaunchProfileFlyoutPresenterStyle"] as Style,
            };
            panel.ProfileSelected += (_, profile) =>
            {
                flyout.Hide();
                SelectGameAndProfile(icon, profile.Id);
            };
            panel.ProfileAdded += (_, profileId) =>
            {
                flyout.Hide();
                SelectGameAndProfile(icon, profileId);
            };
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_launchProfileFlyout, flyout))
                {
                    _launchProfileFlyout = null;
                }
            };
            _launchProfileFlyout = flyout;
            flyout.ShowAt(anchor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show launch profile flyout");
        }
    }


    private void CloseLaunchProfileFlyout()
    {
        try
        {
            _launchProfileFlyout?.Hide();
        }
        catch { }
        _launchProfileFlyout = null;
    }


    /// <summary>
    /// 切换到某个区服并选中它的某个启动预设。
    /// <para>
    /// 先把预设写进存储并广播 <see cref="GameLaunchProfileChangedMessage"/>，再切当前游戏：
    /// 启动器页面是在切换游戏之后重建的，切之前写好的预设它一加载就读到，不必再等一次刷新。
    /// </para>
    /// </summary>
    private void SelectGameAndProfile(PinnedGameIcon icon, string profileId)
    {
        try
        {
            GameBiz gameBiz = icon.GameId.GameBiz;
            // 先把预设校验一遍：内部名不存在时什么都不做
            if (GameLaunchProfileStore.GetProfile(gameBiz, profileId) is null)
            {
                return;
            }
            GameLaunchProfileStore.SetCurrentProfile(gameBiz, profileId);
            WeakReferenceMessenger.Default.Send(new GameLaunchProfileChangedMessage());
            if (!ReferenceEquals(CurrentGameIcon, icon))
            {
                CurrentGameIcon?.IsSelected = false;
                CurrentGameIcon = icon;
                icon.IsSelected = true;
                CurrentGameChanged?.Invoke(this, icon);
                AppConfig.CurrentGameId = icon.GameId.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Select game and launch profile");
        }
    }


    #endregion



}
