using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Starward.Helpers;
using Starward.Language;
using System;
using System.ComponentModel;
using System.IO;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Starward.Features.Background;

/// <summary>
/// 好感 / 满影画卡片：封面框按密友同行 245×153、下载 / 删除 / 点击设为背景。
/// 已下载的动态壁纸可悬停静音循环预览；满影画下载后封面即换成静态图。
/// </summary>
public sealed partial class FavorWallpaperCard : UserControl
{

    /// <summary>卡片内容区相对格子的内边距。</summary>
    public const double ContentInset = 4;

    /// <summary>密友同行百科图标像素尺寸（绝大多数为 245×153）。</summary>
    public const double CoverImageWidth = 245;

    /// <summary>密友同行百科图标像素尺寸（绝大多数为 245×153）。</summary>
    public const double CoverImageHeight = 153;

    /// <summary>封面内容区高宽比，与密友同行图标一致。</summary>
    public const double CellAspect = CoverImageHeight / CoverImageWidth;

    /// <summary>悬停后启动预览的延迟，避免扫过画廊时误触发。</summary>
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(280);

    /// <summary>同一画廊同时只播一张，避免多路解码占满 CPU。</summary>
    private static FavorWallpaperCard? s_activePreview;

    /// <summary>「视频解码失败」本次运行是否提示过；悬停不是用户主动操作，只提示一次。</summary>
    private static bool s_decodeFailedToastShown;

    private static readonly ILogger<FavorWallpaperCard> s_logger = AppConfig.GetLogger<FavorWallpaperCard>();


    private readonly DispatcherQueueTimer _hoverTimer;

    private MediaPlayer? _player;
    private MediaSource? _mediaSource;

    /// <summary>包住 <see cref="_mediaSource"/> 的播放项，用来监听视频轨打开失败。</summary>
    private MediaPlaybackItem? _playbackItem;

    private bool _pointerInside;
    private bool _previewing;
    private bool _videoRevealed;
    private bool _handlersHooked;

    /// <summary>
    /// 这次悬停已经判定预览播不了（视频轨解不了），封面保持不动；
    /// 鼠标移出卡片前不再尝试，否则每次移动鼠标都会重新走一遍。
    /// </summary>
    private bool _previewUnavailable;


    public FavorWallpaperCard()
    {
        this.InitializeComponent();
        this.Loaded += FavorWallpaperCard_Loaded;
        this.Unloaded += FavorWallpaperCard_Unloaded;
        AddHandler(TappedEvent, new TappedEventHandler(OnTapped), handledEventsToo: true);

        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Interval = HoverDelay;
        HookHandlers();
    }


    /// <summary>
    /// 成对挂接本卡片用到的全部事件。
    /// 这些事件原先写在 XAML 里，声明式订阅没有退订入口，卸载后仍留着本机侧注册，
    /// 实测每开一次壁纸对话框就把整批卡片（含 MediaPlayerElement）永久留在内存里。
    /// </summary>
    private void HookHandlers()
    {
        if (_handlersHooked)
        {
            return;
        }
        _handlersHooked = true;
        RootVisual.PointerCanceled += Root_PointerCanceled;
        RootVisual.PointerCaptureLost += Root_PointerCaptureLost;
        RootVisual.PointerEntered += Root_PointerEntered;
        RootVisual.PointerExited += Root_PointerExited;
        RootVisual.PointerMoved += Root_PointerMoved;
        Button_Action.Click += Action_Click;
        _hoverTimer.Tick += HoverTimer_Tick;
    }


    /// <summary>退订 <see cref="HookHandlers"/> 挂接的全部事件；与之严格成对。</summary>
    private void UnhookHandlers()
    {
        if (!_handlersHooked)
        {
            return;
        }
        _handlersHooked = false;
        RootVisual.PointerCanceled -= Root_PointerCanceled;
        RootVisual.PointerCaptureLost -= Root_PointerCaptureLost;
        RootVisual.PointerEntered -= Root_PointerEntered;
        RootVisual.PointerExited -= Root_PointerExited;
        RootVisual.PointerMoved -= Root_PointerMoved;
        Button_Action.Click -= Action_Click;
        _hoverTimer.Tick -= HoverTimer_Tick;
    }


    /// <summary>
    /// realize 延迟创建的 <see cref="Player_Preview"/>（XAML 上是 <c>x:Load="False"</c>）。
    /// 只有真正开始悬停预览时才付出 MediaPlayerElement 的构造开销。
    /// </summary>
    /// <returns>已 realize 的预览元素；名称在 XAML 里定义了，取不到说明资源损坏。</returns>
    private MediaPlayerElement EnsurePlayerElement()
    {
        if (Player_Preview is null)
        {
            FindName(nameof(Player_Preview));
        }
        // FindName 之后字段才被赋值，编译器推不出来，这里显式保证非 null
        return Player_Preview ?? throw new InvalidOperationException($"Failed to realize {nameof(Player_Preview)}.");
    }


    public FavorWallpaperView? View
    {
        get => (FavorWallpaperView?)GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }


    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(
        nameof(View),
        typeof(FavorWallpaperView),
        typeof(FavorWallpaperCard),
        new PropertyMetadata(null, OnViewPropertyChanged));


    private static void OnViewPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((FavorWallpaperCard)d).HandleViewChanged(e.OldValue as FavorWallpaperView, e.NewValue as FavorWallpaperView);
    }


    private void HandleViewChanged(FavorWallpaperView? oldView, FavorWallpaperView? newView)
    {
        if (oldView is not null)
        {
            oldView.PropertyChanged -= View_PropertyChanged;
        }
        StopPreview();
        _previewUnavailable = false;
        if (newView is not null)
        {
            newView.PropertyChanged += View_PropertyChanged;
        }
    }


    private async void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (View is null || View.IsDownloading)
        {
            return;
        }
        if (e.OriginalSource is DependencyObject source && IsInsideActionButton(source))
        {
            return;
        }
        StopPreview();
        if (View.UseAction is not null)
        {
            await View.UseAction(View);
        }
    }


    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }
        // 删除前先释放预览占用的文件句柄，否则本地缓存删不掉。
        StopPreview();
        if (View.IsDownloaded)
        {
            if (View.DeleteAction is not null)
            {
                await View.DeleteAction(View);
            }
        }
        else if (View.DownloadAction is not null)
        {
            await View.DownloadAction(View);
        }
    }


    private bool IsInsideActionButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, Button_Action))
            {
                return true;
            }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }


    private void FavorWallpaperCard_Loaded(object sender, RoutedEventArgs e)
    {
        HookHandlers();
        if (View is not null)
        {
            View.PropertyChanged -= View_PropertyChanged;
            View.PropertyChanged += View_PropertyChanged;
        }
    }


    private void FavorWallpaperCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (View is not null)
        {
            View.PropertyChanged -= View_PropertyChanged;
        }
        StopPreview();
        UnhookHandlers();
    }


    private void View_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(FavorWallpaperView.IsDownloaded) or nameof(FavorWallpaperView.IsDownloading)))
        {
            return;
        }
        if (!CanPreview())
        {
            StopPreview();
            return;
        }
        if (_pointerInside && !_previewing)
        {
            SchedulePreview();
        }
    }


    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        if (CanPreview() && !IsOverActionButton(e))
        {
            SchedulePreview();
        }
    }


    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerInside || _previewing || !CanPreview() || IsOverActionButton(e))
        {
            return;
        }
        if (!_hoverTimer.IsRunning)
        {
            SchedulePreview();
        }
    }


    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        _previewUnavailable = false;
        StopPreview();
    }


    private void Root_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        _previewUnavailable = false;
        StopPreview();
    }


    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerInside)
        {
            StopPreview();
        }
    }


    /// <summary>
    /// 已下载的动态壁纸才悬停预览；满影画是静态图，封面即预览。
    /// </summary>
    private bool CanPreview()
    {
        return View is { IsDownloaded: true, IsDownloading: false, IsStatic: false } && !_previewUnavailable && TryGetLocalVideoPath() is not null;
    }


    private string? TryGetLocalVideoPath()
    {
        if (View is null || View.IsStatic)
        {
            return null;
        }
        string? path = FavorWallpaperService.ResolveMediaPath(View.Record);
        return path is not null && File.Exists(path) ? path : null;
    }


    private bool IsOverActionButton(PointerRoutedEventArgs e)
    {
        return e.OriginalSource is DependencyObject source && IsInsideActionButton(source);
    }


    private void SchedulePreview()
    {
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }


    private void HoverTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_pointerInside && CanPreview() && !_previewing)
        {
            StartPreview();
        }
    }


    /// <summary>
    /// 打开本地视频并从头静音循环。
    /// </summary>
    private void StartPreview()
    {
        string? path = TryGetLocalVideoPath();
        if (path is null)
        {
            return;
        }

        if (s_activePreview is { } other && !ReferenceEquals(other, this))
        {
            other.StopPreview();
        }
        s_activePreview = this;

        StopPlaybackOnly();
        _previewing = true;

        try
        {
            var player = new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true,
                Volume = 0,
                AutoPlay = false,
            };
            player.CommandManager.IsEnabled = false;
            player.SystemMediaTransportControls.IsEnabled = false;
            player.MediaOpened += Player_MediaOpened;
            player.MediaFailed += Player_MediaFailed;
            player.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;

            _mediaSource = MediaSource.CreateFromUri(new Uri(path));
            // 套一层播放项才能监听视频轨打开失败：解不了时不会有 MediaFailed，见 VideoTrack_OpenFailed。
            _playbackItem = new MediaPlaybackItem(_mediaSource);
            _playbackItem.VideoTracksChanged += PlaybackItem_VideoTracksChanged;
            player.Source = _playbackItem;
            MediaPlayerElement preview = EnsurePlayerElement();
            preview.SetMediaPlayer(player);
            preview.Visibility = Visibility.Visible;
            _player = player;
        }
        catch
        {
            StopPreview();
        }
    }


    private void Player_MediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_previewing || !_pointerInside || !ReferenceEquals(sender, _player))
            {
                return;
            }
            sender.Play();
        });
    }


    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(StopPreview);
    }


    /// <summary>
    /// 给新加入的视频轨挂上 <see cref="VideoTrack_OpenFailed"/>。
    /// </summary>
    private void PlaybackItem_VideoTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
    {
        try
        {
            if (args.CollectionChange is CollectionChange.ItemInserted && args.Index < sender.VideoTracks.Count)
            {
                sender.VideoTracks[(int)args.Index].OpenFailed += VideoTrack_OpenFailed;
            }
        }
        catch { }
    }


    /// <summary>
    /// 视频轨打开失败（缺解码器时实测 0xC00D5212）。MediaPlayer 不会触发 MediaFailed，
    /// 所以停掉预览把封面放回来，写日志并提示一次。回到 UI 线程后先确认仍是当前预览，已经换了就不管。
    /// </summary>
    private void VideoTrack_OpenFailed(VideoTrack sender, VideoTrackOpenFailedEventArgs args)
    {
        MediaPlaybackItem? item = _playbackItem;
        Exception? error = args.ExtendedError;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (item is null || !ReferenceEquals(item, _playbackItem))
            {
                return;
            }
            s_logger.LogWarning(error, "Video track of favor wallpaper preview failed to open: '{File}'", TryGetLocalVideoPath());
            _previewUnavailable = true;
            StopPreview();
            if (!s_decodeFailedToastShown)
            {
                s_decodeFailedToastShown = true;
                InAppToast.MainWindow?.Warning(Lang.AppBackground_VideoDecodingFailed);
            }
        });
    }


    private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
    {
        if (_videoRevealed)
        {
            return;
        }
        DispatcherQueue.TryEnqueue(RevealVideoIfNeeded);
    }


    /// <summary>
    /// 等首帧真正出来再藏封面，避免 MediaOpened 后闪黑。
    /// </summary>
    private void RevealVideoIfNeeded()
    {
        if (_videoRevealed || !_previewing)
        {
            return;
        }
        _videoRevealed = true;
        SetPreviewVisuals(showVideo: true);
    }


    /// <summary>
    /// 离开卡片、换绑或卸载时释放播放器，封面回到静止图。
    /// </summary>
    private void StopPreview()
    {
        _hoverTimer.Stop();
        _previewing = false;
        _videoRevealed = false;
        SetPreviewVisuals(showVideo: false);
        StopPlaybackOnly();
        if (Player_Preview is not null)
        {
            Player_Preview.Visibility = Visibility.Collapsed;
        }
        if (ReferenceEquals(s_activePreview, this))
        {
            s_activePreview = null;
        }
    }


    /// <summary>
    /// 只拆播放器与源，不改悬停状态（用于同卡重新打开预览）。
    /// </summary>
    private void StopPlaybackOnly()
    {
        MediaPlayer? player = _player;
        MediaSource? source = _mediaSource;
        MediaPlaybackItem? item = _playbackItem;
        _player = null;
        _mediaSource = null;
        _playbackItem = null;
        if (item is not null)
        {
            item.VideoTracksChanged -= PlaybackItem_VideoTracksChanged;
            try
            {
                foreach (VideoTrack track in item.VideoTracks)
                {
                    track.OpenFailed -= VideoTrack_OpenFailed;
                }
            }
            catch { }
        }
        if (player is null)
        {
            Player_Preview?.SetMediaPlayer(null);
            return;
        }
        player.MediaOpened -= Player_MediaOpened;
        player.MediaFailed -= Player_MediaFailed;
        player.PlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
        try
        {
            player.Pause();
        }
        catch
        {
        }
        player.Source = null;
        Player_Preview?.SetMediaPlayer(null);
        source?.Dispose();
        player.Dispose();
    }


    /// <summary>
    /// 预览与封面互相切换：直接设置透明度，不做淡入淡出动画。
    /// </summary>
    private void SetPreviewVisuals(bool showVideo)
    {
        Image_Cover.Opacity = showVideo ? 0 : 1;
        // 没预览过就没有 Player_Preview（x:Load="False"），此时无需处理视频层
        if (Player_Preview is not null)
        {
            Player_Preview.Opacity = showVideo ? 1 : 0;
        }
    }

}
