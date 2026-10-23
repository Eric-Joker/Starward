using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.Core.HoYoPlay;
using Starward.Features.HoYoPlay;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.System;


namespace Starward.Features.GameLauncher;

[INotifyPropertyChanged]
public sealed partial class GameBannerAndPost : UserControl
{


    private Microsoft.UI.Dispatching.DispatcherQueueTimer _bannerTimer;


    private readonly ILogger<GameBannerAndPost> _logger = AppConfig.GetLogger<GameBannerAndPost>();


    private readonly HoYoPlayService _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();


    private readonly GameNoticeService _gameNoticeService = AppConfig.GetService<GameNoticeService>();


    public GameId CurrentGameId { get; set; }



    public GameBannerAndPost()
    {
        this.InitializeComponent();
        this.Loaded += GameBannerAndPost_Loaded;
        this.Unloaded += GameBannerAndPost_Unloaded;
        _bannerTimer = DispatcherQueue.CreateTimer();
        _bannerTimer.Interval = TimeSpan.FromSeconds(5);
        _bannerTimer.IsRepeating = true;
        _bannerTimer.Tick += _bannerTimer_Tick;
    }






    private async void GameBannerAndPost_Loaded(object sender, RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<GameNoticeWindowClosedMessage>(this, OnGameNoticeWindowClosed);
        WeakReferenceMessenger.Default.Register<GameAnnouncementSettingChangedMessage>(this, OnGameAnnouncementSettingChanged);
        await UpdateGameContentAsync();
        await UpdateGameNoticeAlertAsync();
    }


    private void GameBannerAndPost_Unloaded(object sender, RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _bannerTimer.Stop();
        _bannerTimer.Tick -= _bannerTimer_Tick;
        Banners = null;
        PostGroups = null;
    }



    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Activate)
            {
                _bannerTimer.Start();
            }
            else if (message.Hide || message.SessionLock)
            {
                _bannerTimer.Stop();
            }
        }
        catch { }
    }



    private async void OnGameNoticeWindowClosed(object _, GameNoticeWindowClosedMessage message)
    {
        User32.SetForegroundWindow(XamlRoot.GetWindowHandle());
        await UpdateGameNoticeAlertAsync();
    }



    private async void OnGameAnnouncementSettingChanged(object _, GameAnnouncementSettingChangedMessage message)
    {
        // 没有设置取消，网络不好时可能会造成状态异常，懒得写了
        if (AppConfig.GetEnableBannerAndPost(CurrentGameId.GameBiz))
        {
            ShowBannerAndPost = true;
            await UpdateGameContentAsync();
            if (AppConfig.DisableGameNoticeRedHot)
            {
                IsGameNoticesAlert = false;
            }
            else
            {
                await UpdateGameNoticeAlertAsync();
            }
        }
        else
        {
            ShowBannerAndPost = false;
        }
    }



    public List<GameBanner>? Banners { get; set => SetProperty(ref field, value); }





    public List<GamePostGroup>? PostGroups { get; set => SetProperty(ref field, value); }





    public bool IsGameNoticesAlert { get; set => SetProperty(ref field, value); }




    public bool ShowBannerAndPost
    {
        get => this.Opacity == 1;
        set
        {
            if (value && Banners?.Count > 0 && PostGroups?.Count > 0)
            {
                // 内容可能在主窗口隐藏到托盘/最小化之后才加载完，这时不能当作窗口已激活去恢复轮播，
                // 否则轮播会在看不见的窗口里一直转，见 MainWindow 激活消息处的说明
                if (IsHostWindowShown())
                {
                    _bannerTimer.Start();
                }
                else
                {
                    _bannerTimer.Stop();
                }
                this.Opacity = 1;
                this.IsHitTestVisible = true;
            }
            else
            {
                _bannerTimer.Stop();
                this.Opacity = 0;
                this.IsHitTestVisible = false;
            }
        }
    }




    /// <summary>
    /// 宿主窗口当前是否显示在屏幕上（未隐藏到托盘、未最小化）。
    /// </summary>
    /// <returns>窗口可见且未最小化时为 <see langword="true"/>；尚未进入可视树或查询失败时也返回 <see langword="true"/>，保持原有行为。</returns>
    private bool IsHostWindowShown()
    {
        try
        {
            if (XamlRoot is null)
            {
                return true;
            }
            nint hwnd = XamlRoot.GetWindowHandle();
            return User32.IsWindowVisible(hwnd) && !User32.IsIconic(hwnd);
        }
        catch
        {
            return true;
        }
    }




    private async Task UpdateGameContentAsync()
    {
        try
        {
            var content = await _hoYoPlayService.GetGameContentAsync(CurrentGameId);
            // 显示游戏公告属于启动预设：当前预设关掉时连公告内容都不取。
            if (content is null || !AppConfig.GetEnableBannerAndPost(CurrentGameId.GameBiz))
            {
                ShowBannerAndPost = false;
                return;
            }
            Banners = content.Banners;
            PostGroups = GamePostGroup.FromGameContent(content);
            ShowBannerAndPost = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game launcher content ({CurrentGameId})", CurrentGameId);
        }
    }




    private async Task UpdateGameNoticeAlertAsync()
    {
        try
        {
            if (GameFeatureConfig.FromGameId(CurrentGameId).InGameNoticesWindow)
            {
                Button_InGameNotices.Visibility = Visibility.Visible;
            }
            else
            {
                Button_InGameNotices.Visibility = Visibility.Collapsed;
                return;
            }
            if (AppConfig.DisableGameNoticeRedHot)
            {
                IsGameNoticesAlert = false;
            }
            else
            {
                IsGameNoticesAlert = await _gameNoticeService.IsNoticeAlertAsync(CurrentGameId.GameBiz);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game launcher content ({CurrentGameId})", CurrentGameId);
        }
    }




    private void _bannerTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (Banners?.Count > 0)
            {
                FlipView_Banner.SelectedIndex = (FlipView_Banner.SelectedIndex + 1) % Banners.Count;
            }
        }
        catch { }
    }



    private async void Image_Banner_Tapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameBanner banner)
            {
                await Launcher.LaunchUriAsync(new Uri(banner.Image.Link));
            }
        }
        catch { }
    }



    /// <summary>
    /// 隐藏 FilpView 中自动出现的翻页按键
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void FlipView_Banner_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var grid = VisualTreeHelper.GetChild(FlipView_Banner, 0);
            if (grid != null)
            {
                var count = VisualTreeHelper.GetChildrenCount(grid);
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var child = VisualTreeHelper.GetChild(grid, i);
                        if (child is Button button)
                        {

                            button.IsHitTestVisible = false;
                            button.Opacity = 0;
                        }
                    }
                }
            }
        }
        catch { }
    }




    private void Grid_BannerContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _bannerTimer.Stop();
        Border_PipsPager.Visibility = Visibility.Visible;
    }



    private void Grid_BannerContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _bannerTimer.Start();
        Border_PipsPager.Visibility = Visibility.Collapsed;
    }






    [RelayCommand]
    private void OpenGameNoticeWindow()
    {
        try
        {
            new GameNoticeWindow
            {
                CurrentGameBiz = CurrentGameId.GameBiz,
                ParentWindowHandle = (nint)this.XamlRoot.ContentIslandEnvironment.AppWindowId.Value
            }.Activate();
        }
        catch { }
    }





    public static string AddOne(int number)
    {
        return (number + 1).ToString();
    }




}
