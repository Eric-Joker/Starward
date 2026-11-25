using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.HoYoPlay;
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



    public GameSwitcher()
    {
        InitializeComponent();
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



}
