using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starward.Core.HoYoPlay;
using Starward.Features.Codec;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using WinRT;


namespace Starward.Features.Background;

[INotifyPropertyChanged]
public sealed partial class AppBackground : UserControl
{

    public static AppBackground Current { get; private set; }


    private readonly ILogger<AppBackground> _logger = AppConfig.GetLogger<AppBackground>();

    private readonly BackgroundService _backgroundService = AppConfig.GetService<BackgroundService>();


    public AppBackground()
    {
        Current = this;
        this.InitializeComponent();
        this.Loaded += AppBackground_Loaded;
        this.Unloaded += AppBackground_Unloaded;
        WeakReferenceMessenger.Default.Register<BackgroundChangedMessage>(this, OnBackgroundChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<MainWindowDisplayStatusChangedMessage>(this, OnMainWindowDisplayStatusChanged);
    }


    private void AppBackground_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        MediaDevice.DefaultAudioRenderDeviceChanged -= MediaDevice_DefaultAudioRenderDeviceChanged;
        MediaDevice.DefaultAudioRenderDeviceChanged += MediaDevice_DefaultAudioRenderDeviceChanged;
    }


    private void AppBackground_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        this.Unloaded -= AppBackground_Unloaded;
        CanvasDevice.GetSharedDevice().DeviceLost -= CanvasDevice_DeviceLost;
        // 静态事件会一直拿着本实例，必须退订
        MediaDevice.DefaultAudioRenderDeviceChanged -= MediaDevice_DefaultAudioRenderDeviceChanged;
        DisposeVideoResource();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }



    public GameId CurrentGameId
    {
        get; set
        {
            if (field is null)
            {
                field = value;
                InitializeBackgroundImage();
            }
            field = value;
            _ = UpdateBackgroundAsync();
        }
    }

    public ImageSource? PlaceholderImageSource
    {
        get; set
        {
            if (field is SoftwareBitmapSource bitmapSource)
            {
                bitmapSource.Dispose();
            }
            SetProperty(ref field, value);
        }
    }

    public ImageSource? BackgroundImageSource
    {
        get; set
        {
            if (value is null && field is not null)
            {
                PlaceholderImageSource = field;
            }
            SetProperty(ref field, value);
        }
    }

    public string? ThemeImageUrl { get; set => SetProperty(ref field, value); }

    public bool IsUpdateBackgroundRunning { get; set => SetProperty(ref field, value); }

    public ObservableCollection<GameBackgroundModel>? GameBackgrounds { get; set => SetProperty(ref field, value); }

    public GameBackgroundModel? CurrentGameBackground { get; set => SetProperty(ref field, value); }


    private string? _lastBackgroundUrl;

    private string? _lastBackgroundFile;

    private CancellationTokenSource? _updateCancelTokenSource;


    private CancellationToken RefreshCancellationToken()
    {
        _updateCancelTokenSource?.Cancel();
        _updateCancelTokenSource = new();
        return _updateCancelTokenSource.Token;
    }



    private void InitializeBackgroundImage()
    {
        try
        {
            var file = BackgroundService.GetLastAppBackgroundFile(CurrentGameId);
            if (file != null)
            {
                if (!BackgroundService.FileIsSupportedVideo(file))
                {
                    BackgroundImageSource = new BitmapImage(new Uri(file));
                }
                try
                {
                    string? hex = AppConfig.AccentColor;
                    if (!string.IsNullOrWhiteSpace(hex))
                    {
                        Color color = ColorHelper.ToColor(hex);
                        AccentColorHelper.ChangeAppAccentColor(color);
                    }
                }
                catch { }
            }
            else
            {
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize background image");
        }

    }


    public async Task UpdateBackgroundAsync()
    {
        CanvasDevice.GetSharedDevice().DeviceLost -= CanvasDevice_DeviceLost;
        CanvasDevice.GetSharedDevice().DeviceLost += CanvasDevice_DeviceLost;
        CancellationToken cancellationToken = RefreshCancellationToken();
        try
        {
            IsUpdateBackgroundRunning = true;
            GameBackgrounds = null;
            CurrentGameBackground = null;
            ThemeImageUrl = null;

            if (CurrentGameId is null)
            {
                DisposeVideoResource();
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
                return;
            }

            try
            {
                if (BackgroundService.TryGetCustomBgFilePath(CurrentGameId, out string? path))
                {
                    if (BackgroundService.FileIsSupportedVideo(path))
                    {
                        DisposeVideoResource();
                        await StartMediaPlayerAsync(path);
                        _lastBackgroundFile = path;
                    }
                    else
                    {
                        await SetImageBackgroundAsync(path, cancellationToken);
                    }
                    return;
                }
            }
            catch { }

            var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(1000).Token).Token;
            try
            {
                await UpdateGameBackgroundAsync(false, timeoutToken);
                return;
            }
            catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                string? path = BackgroundService.GetLastAppBackgroundFile(CurrentGameId);
                if (path is not null)
                {
                    await SetImageBackgroundAsync(path, cancellationToken);
                }
            }

            await UpdateGameBackgroundAsync(true, cancellationToken);

        }
        catch (OperationCanceledException) { }
        catch (COMException ex) when (ex.HResult == -2003292277)
        {
            // 0x88982F8B
            if (Path.GetExtension(_lastBackgroundUrl)?.StartsWith(".webp", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                      Lang.AppBackground_ImageDecodingFailed,
                                                      Lang.AppBackground_PleaseInstallTheWebPImageExtension,
                                                      Lang.SettingPage_Download,
                                                      async () => await Launcher.LaunchUriAsync(new("https://apps.microsoft.com/detail/9pg2dk419drg")));
            }
            else
            {
                InAppToast.MainWindow?.Warning(Lang.AppBackground_ImageDecodingFailed);
            }
            _logger.LogError(ex, "Cannot decode image: '{path}'", _lastBackgroundUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update background image");
            try
            {
                if (BackgroundService.GetFallbackBackgroundImage() is string path)
                {
                    await SetImageBackgroundAsync(path, cancellationToken);
                }
                else
                {
                    BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
                }
            }
            catch (Exception)
            {
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsUpdateBackgroundRunning = false;
            }
        }
    }


    private async Task UpdateGameBackgroundAsync(bool imageBeforeVideo, CancellationToken cancellationToken)
    {
        (var models, var selected) = await _backgroundService.GetGameBackgroundsAsync(CurrentGameId, cancellationToken);
        GameBackgrounds = new(models);
        if (selected is not null)
        {
            if (selected.Type is 1 && selected.GameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO)
            {
                if (imageBeforeVideo)
                {
                    _lastBackgroundUrl = selected.GameBackground.Background.Url;
                    await SetImageBackgroundAsync(selected.GameBackground, cancellationToken);
                }
                _lastBackgroundUrl = selected.GameBackground.Video.Url;
                await SetVideoBackgroundAsync(selected.GameBackground, cancellationToken);
            }
            else
            {
                _lastBackgroundUrl = selected.GameBackground.Background.Url;
                await SetImageBackgroundAsync(selected.GameBackground, cancellationToken);
            }
        }
        else
        {
            string? fallback = BackgroundService.GetFallbackBackgroundImage();
            if (fallback is not null)
            {
                await SetImageBackgroundAsync(fallback, cancellationToken);
            }
        }
        CurrentGameBackground = selected;
        SaveGameBackgroundSetting();
    }


    private async Task SetImageBackgroundAsync(GameBackground gameBackground, CancellationToken cancellationToken = default)
    {
        CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : RefreshCancellationToken();
        try
        {
            if (!cancellationToken.CanBeCanceled)
            {
                IsUpdateBackgroundRunning = true;
            }
            string file = await _backgroundService.GetBackgroundFileAsync(gameBackground.Background.Url, token);
            await SetImageBackgroundAsync(file, token);
            ThemeImageUrl = string.IsNullOrWhiteSpace(gameBackground.Theme.Url) ? null : gameBackground.Theme.Url;
        }
        finally
        {
            if (!cancellationToken.CanBeCanceled)
            {
                IsUpdateBackgroundRunning = false;
            }
        }
    }


    private async Task SetImageBackgroundAsync(string file, CancellationToken cancellationToken)
    {
        if (_lastBackgroundFile == file)
        {
            return;
        }
        DisposeVideoResource();
        using var fs = File.OpenRead(file);
        var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var bitmapSource = new SoftwareBitmapSource();
        await bitmapSource.SetBitmapAsync(bitmap);
        if (cancellationToken.IsCancellationRequested)
        {
            bitmapSource.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        BackgroundImageSource = bitmapSource;

        using BitmapBuffer bitmapBuffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using IMemoryBufferReference memoryBufferReference = bitmapBuffer.CreateReference();
        memoryBufferReference.As<AccentColorHelper.IMemoryBufferByteAccess>().GetBuffer(out nint bufferPtr, out uint capacity);
        Color? color = AccentColorHelper.GetAccentColor(bufferPtr, capacity, bitmap.PixelWidth, bitmap.PixelHeight);
        AccentColorHelper.ChangeAppAccentColor(color);
        AppConfig.AccentColor = color?.ToHex();
        _lastBackgroundFile = file;
        AppConfig.SetBg(CurrentGameId, Path.GetFileName(file));
    }


    private async Task SetVideoBackgroundAsync(GameBackground gameBackground, CancellationToken cancellationToken = default)
    {
        CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : RefreshCancellationToken();
        try
        {
            if (!cancellationToken.CanBeCanceled)
            {
                IsUpdateBackgroundRunning = true;
            }
            string filePath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Video.Url, token);
            if (_lastBackgroundFile == filePath)
            {
                return;
            }
            DisposeVideoResource();
            await StartMediaPlayerAsync(filePath);
            await ChangeAccentColorToImageFileAsync(gameBackground.Background.Url, token);
            ThemeImageUrl = string.IsNullOrWhiteSpace(gameBackground.Theme.Url) ? null : gameBackground.Theme.Url;
            _lastBackgroundFile = filePath;
        }
        finally
        {
            if (!cancellationToken.CanBeCanceled)
            {
                IsUpdateBackgroundRunning = false;
            }
        }

    }


    private void SaveGameBackgroundSetting()
    {
        if (GameBackgrounds != null && CurrentGameBackground != null)
        {
            int index = GameBackgrounds.IndexOf(CurrentGameBackground);
            if (index >= 0)
            {
                string content = $"{index};{string.Join(';', GameBackgrounds.Select(x => $"{x.Id},{x.Type}"))}";
                AppConfig.SetGameBackground(CurrentGameId, content);
            }
        }
        else
        {
            AppConfig.SetGameBackground(CurrentGameId, null);
        }
    }


    private void CanvasDevice_DeviceLost(CanvasDevice sender, object args)
    {
        try
        {
            sender.DeviceLost -= CanvasDevice_DeviceLost;
            _logger.LogWarning("Canvas device lost ({reason})", sender.GetDeviceLostReason());
            DispatcherQueue?.TryEnqueue(() =>
            {
                _lastBackgroundUrl = null;
                _lastBackgroundFile = null;
                _ = UpdateBackgroundAsync();

            });
        }
        catch { }
    }


    #region Video



    private MediaPlayer? _mediaPlayer;

    private CanvasRenderTarget? _videoSurface;

    private CanvasImageSource? _videoImageSource;

    private SemaphoreSlim _videoSemaphore = new SemaphoreSlim(1, 1);

    /// <summary>
    /// 熄屏时释放了播放器（或熄屏期间跳过了创建），等亮屏再重建。
    /// 帧服务器模式的 MediaPlayer 在线程池线程上靠等垂直同步定节拍，熄屏后没有 vblank，等待立即返回，
    /// 空转吃满一个核心；Pause 也停不下这个循环，只能释放播放器。
    /// </summary>
    private bool _videoPlayerReleasedForDisplayOff;

    /// <summary>
    /// 当前播放器所用的备用音频输出设备；null 表示跟随系统默认设备。
    /// 默认设备建不了音频流时才会换到备用设备，见 <see cref="OnVideoAudioOutputFailed"/>。
    /// </summary>
    private DeviceInformation? _videoAudioDevice;

    /// <summary>当前播放器开播时所用音频输出设备的 ID（跟随默认设备时为开播那一刻的默认设备），播放失败时据此记下坏设备。</summary>
    private string? _videoAudioDeviceId;

    /// <summary>
    /// 默认设备和全部备用设备都建不了音频流、改显示静态画面的视频背景文件。
    /// 窗口激活时的兜底重启要跳过它，改为检查默认设备是否恢复。
    /// </summary>
    private string? _audioUnavailableVideoFile;

    /// <summary>「音频输出设备不可用」本次运行是否提示过。主窗口关到托盘后会重建本控件，所以用静态字段。</summary>
    private static bool _videoAudioUnavailableToastShown;

    /// <summary>窗口激活时检查默认音频设备是否恢复的基础间隔。</summary>
    private static readonly TimeSpan VideoAudioRecoveryCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>检查间隔的上限：探测说恢复了、真播起来却又失败时逐次加倍到这里为止。</summary>
    private static readonly TimeSpan MaxVideoAudioRecoveryCheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>当前的检查间隔；默认设备变化时重置为 <see cref="VideoAudioRecoveryCheckInterval"/>。</summary>
    private TimeSpan _videoAudioRecoveryCheckInterval = VideoAudioRecoveryCheckInterval;

    /// <summary>下次允许因窗口激活而检查的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private long _nextVideoAudioRecoveryCheck;

    /// <summary>是否正在检查默认音频设备，避免默认设备变化与窗口激活同时触发两次重建。</summary>
    private bool _videoAudioRecoveryRunning;

    /// <summary>上一次重建是否因探测到默认设备恢复而发起；之后默认设备上仍建不了流，说明探测不可信，要拉长检查间隔。</summary>
    private bool _videoAudioRecoveryRebuilt;

    /// <summary>冻结的画面是否正顶在视频表面上；新播放器画出第一帧后切回视频表面（见 <see cref="MediaPlayer_VideoFrameAvailable"/>）。</summary>
    private bool _videoDisplayFrozen;

    /// <summary>每启动一次播放器加一；挑选音频设备的 await 期间背景被换掉时，据此丢弃过期启动。</summary>
    private int _startMediaPlayerGeneration;

    /// <summary>是否有一次播放器启动仍在进行（挑选音频设备等）；窗口激活的兜底重启与恢复检查要避开它。</summary>
    private bool _startMediaPlayerRunning;


    public bool IsVideoPlaying { get; set => SetProperty(ref field, value); }

    public int VideoBgVolume
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                _mediaPlayer?.Volume = _videoAudioDevice is null ? Math.Clamp(value / 100.0, 0, 1) : 0;
                AppConfig.VideoBgVolume = value;
            }
        }
    } = Math.Clamp(AppConfig.VideoBgVolume, 0, 100);


    public async void PlayPauseVideo()
    {
        try
        {
            if (CurrentGameBackground is null)
            {
                return;
            }
            if (CurrentGameBackground.GameBackground.Type is not GameBackground.BACKGROUND_TYPE_VIDEO)
            {
                return;
            }
            CurrentGameBackground.Type = CurrentGameBackground.Type is 1 ? 0 : 1;
            SaveGameBackgroundSetting();
            if (CurrentGameBackground.Type is 1)
            {
                await SetVideoBackgroundAsync(CurrentGameBackground.GameBackground);
            }
            else
            {
                await SetImageBackgroundAsync(CurrentGameBackground.GameBackground);
            }
        }
        catch { }
    }


    /// <summary>
    /// 为指定的视频文件启动 MediaPlayer（使用帧服务器模式）。
    /// .webm 文件会根据检测结果注册 VP9/Vorbis 本地解码器。
    /// 默认音频输出设备已知建不了流时改用备用设备（音量 0），一个都不行就显示静态画面，见 <see cref="MediaAudioOutputHelper"/>。
    /// </summary>
    /// <param name="file">视频文件完整路径（支持 mp4/mkv/webm）。</param>
    private async Task StartMediaPlayerAsync(string file)
    {
        int generation = ++_startMediaPlayerGeneration;
        _startMediaPlayerRunning = true;
        try
        {
            if (Path.GetExtension(file).Equals(".webm", StringComparison.OrdinalIgnoreCase))
            {
                bool decoderInstalled = VP9Helper.IsVP9DecoderInstalled();
                bool vp8 = VP9Helper.IsVP8VideoFile(file);
                if (vp8)
                {
                    if (!decoderInstalled)
                    {
                        _needToInstallVp9VideoExtension = true;
                    }
                }
                else
                {
                    bool highProfileOrRgb = VP9Helper.IsVP9HighProfileOrRGB(file);
                    if (!decoderInstalled || highProfileOrRgb)
                    {
                        VP9Helper.RegisterVP9Decoder(true);
                    }
                    if (!decoderInstalled && !highProfileOrRgb)
                    {
                        SuggestToInstallVP9Decoder();
                    }
                }
            }
            VP9Helper.RegisterVorbisDecoder();

            // 默认设备已知建不了音频流时换备用设备；一个能用的都没有就不播，改显示静态画面
            (bool audioAvailable, DeviceInformation? audioDevice) = await MediaAudioOutputHelper.SelectRenderDeviceAsync();
            if (generation != _startMediaPlayerGeneration)
            {
                return;
            }
            if (!audioAvailable)
            {
                DisposeMediaPlayer();
                ShowVideoAudioUnavailableFallback(file);
                return;
            }
            if (IsDisplayOff())
            {
                // 熄屏期间不建播放器（建了就空转，见 _videoPlayerReleasedForDisplayOff），例如熄屏时音频设备变化触发的重建；亮屏再建
                DisposeMediaPlayer();
                _videoPlayerReleasedForDisplayOff = true;
                _logger.LogInformation("Display is off, defer starting video background player: '{File}'", file);
                return;
            }

            DisposeMediaPlayer();
            _audioUnavailableVideoFile = null;
            _videoAudioDevice = audioDevice;
            _videoAudioDeviceId = audioDevice?.Id ?? MediaAudioOutputHelper.GetDefaultRenderDeviceId();
            _videoPlayerReleasedForDisplayOff = false;

            _mediaPlayer = new MediaPlayer
            {
                IsLoopingEnabled = true,
                // 备用设备上一律音量 0：用户设的音量只对默认设备有意义，别让声音从意料之外的设备里出来
                Volume = audioDevice is null ? VideoBgVolume / 100.0 : 0,
                IsMuted = false,
                // 关键：启用帧服务器模式，后续通过 VideoFrameAvailable + CopyFrameToVideoSurface 手动获取帧
                IsVideoFrameServerEnabled = true,
            };
            if (audioDevice is not null)
            {
                _mediaPlayer.AudioDevice = audioDevice;
                _logger.LogInformation("Video background plays on fallback audio output device '{Device}' with volume 0: '{File}'", audioDevice.Name, file);
            }
            _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(file));
            _mediaPlayer.CommandManager.IsEnabled = false;
            _mediaPlayer.SystemMediaTransportControls.IsEnabled = false;
            _mediaPlayer.VideoFrameAvailable += MediaPlayer_VideoFrameAvailable;
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
            _mediaPlayer.Play();
            IsVideoPlaying = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start media player");
        }
        finally
        {
            if (generation == _startMediaPlayerGeneration)
            {
                _startMediaPlayerRunning = false;
            }
        }
    }


    /// <summary>
    /// 释放播放器及其音频设备选择，不碰 Win2D 表面和解码器。
    /// 同时让仍在挑选音频设备的启动过期，换设备重建时用。
    /// </summary>
    private void DisposeMediaPlayer()
    {
        _startMediaPlayerGeneration++;
        _startMediaPlayerRunning = false;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.VideoFrameAvailable -= MediaPlayer_VideoFrameAvailable;
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
            _mediaPlayer.Dispose();
        }
        _mediaPlayer = null;
        _videoAudioDevice = null;
        _videoAudioDeviceId = null;
        IsVideoPlaying = false;
    }


    private bool _needToInstallVp9VideoExtension;


    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Exception? error = args.ExtendedErrorCode;
        if (MediaAudioOutputHelper.IsAudioOutputError(error))
        {
            // 建不了音频流与视频能否解码无关，不提示「视频解码失败」：回 UI 线程换个输出设备重建
            DispatcherQueue?.TryEnqueue(() => OnVideoAudioOutputFailed(sender, error));
            return;
        }
        _logger.LogError(error, "Media player failed.");
        if (_needToInstallVp9VideoExtension)
        {
            InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning, null, Lang.AppBackground_VideoDecodingFailedPleaseInstallTheVP9VideoExtensions, Lang.SettingPage_Download, async () => await Launcher.LaunchUriAsync(new("https://apps.microsoft.com/detail/9n4d0msmp0pt")));
            _needToInstallVp9VideoExtension = false;
        }
        else
        {
            InAppToast.MainWindow?.Warning(Lang.AppBackground_VideoDecodingFailed);
        }

        DispatcherQueue?.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(sender, _mediaPlayer))
            {
                return;
            }
            try
            {
                if (CurrentGameBackground is not null)
                {
                    CurrentGameBackground.Type = 0;
                    SaveGameBackgroundSetting();
                    await SetImageBackgroundAsync(CurrentGameBackground.GameBackground);
                }
                else
                {
                    DisposeVideoResource();
                }
            }
            catch { }
        });
    }


    /// <summary>
    /// 当前播放器的音频输出设备建不了流（实测 0x8889000F）：记下这个设备，换下一个能用的设备重建播放器；
    /// 重试已没有进展（拿不到设备 ID 或它早就记过）时改显示静态画面，防止来回循环。
    /// 回到 UI 线程后先确认仍是当前播放器，已经换了背景就不管。
    /// </summary>
    /// <param name="player">报错的播放器。</param>
    /// <param name="error">MediaFailed 的 ExtendedErrorCode。</param>
    private void OnVideoAudioOutputFailed(MediaPlayer player, Exception? error)
    {
        if (!ReferenceEquals(player, _mediaPlayer) || _lastBackgroundFile is not string file || !BackgroundService.FileIsSupportedVideo(file))
        {
            return;
        }
        string? deviceId = _videoAudioDeviceId;
        _ = LogVideoAudioOutputFailedAsync(error, deviceId, _videoAudioDevice?.Name, file);
        if (_videoAudioDevice is null && _videoAudioRecoveryRebuilt)
        {
            // 探测说默认设备恢复了，真播起来却还是建不了流：拉长窗口激活时的检查间隔，免得每次激活都把视频重启一遍
            _videoAudioRecoveryCheckInterval = TimeSpan.FromTicks(Math.Min(_videoAudioRecoveryCheckInterval.Ticks * 2, MaxVideoAudioRecoveryCheckInterval.Ticks));
        }
        _videoAudioRecoveryRebuilt = false;
        bool progressed = MediaAudioOutputHelper.MarkBroken(deviceId);
        // 同步释放并接管：中间若留出空窗，窗口激活的兜底分支会对失败的播放器 Play() 或另起一次启动
        DisposeMediaPlayer();
        if (progressed)
        {
            _ = StartMediaPlayerAsync(file);
        }
        else
        {
            ShowVideoAudioUnavailableFallback(file);
        }
    }


    /// <summary>
    /// 记录音频输出设备不可用的日志，带上设备名。查设备名是异步的，所以单独拿出来，不阻塞换设备重建。
    /// </summary>
    private async Task LogVideoAudioOutputFailedAsync(Exception? error, string? deviceId, string? deviceName, string file)
    {
        try
        {
            deviceName ??= await MediaAudioOutputHelper.GetDeviceNameAsync(deviceId);
            _logger.LogWarning(error, "Audio output device unavailable, cannot create audio stream on '{Device}' ({DeviceId}) for video background: '{File}'", deviceName, deviceId, file);
        }
        catch { }
    }


    /// <summary>
    /// 默认设备和全部备用设备都建不了音频流时的退路：不播视频，显示静态画面——这个视频出过画面就定格在当前帧，否则显示默认背景图。
    /// 与视频能否解码无关，不提示「视频解码失败」；「音频输出设备不可用」每次运行只提示一次。
    /// 默认设备恢复后由 <see cref="TryRecoverVideoAudioOutputAsync"/> 重新播放。
    /// </summary>
    /// <param name="file">视频背景文件。</param>
    private void ShowVideoAudioUnavailableFallback(string file)
    {
        _audioUnavailableVideoFile = file;
        _logger.LogWarning("No audio output device can create audio stream, show still image instead of video background: '{File}'", file);
        if (!FreezeVideoDisplay())
        {
            // 没有可定格的画面：丢掉视频表面，恢复播放后第一帧才会把图源切回视频
            _videoSurface?.Dispose();
            _videoSurface = null;
            _videoImageSource = null;
            BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
        }
        if (!_videoAudioUnavailableToastShown)
        {
            _videoAudioUnavailableToastShown = true;
            InAppToast.MainWindow?.Warning(null, Lang.AppBackground_AudioOutputUnavailableVideoPaused);
        }
    }


    /// <summary>
    /// 默认播放设备变化（如拔插耳机、在系统里切换输出）。在备用设备上播放或显示着静态画面时，立即检查新的默认设备能否建流。
    /// </summary>
    private void MediaDevice_DefaultAudioRenderDeviceChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
    {
        if (args.Role is not AudioDeviceRole.Default)
        {
            return;
        }
        DispatcherQueue?.TryEnqueue(() => _ = TryRecoverVideoAudioOutputAsync(defaultDeviceChanged: true));
    }


    /// <summary>
    /// 视频背景正在备用音频设备上播放，或因音频设备都不可用而显示静态画面时，检查系统默认设备现在能否建流；
    /// 能的话用默认设备重建播放器（恢复用户音量）。默认设备变化时立即检查，窗口激活时按间隔检查。
    /// </summary>
    /// <param name="defaultDeviceChanged">是否由默认设备变化触发；是则不受检查间隔限制，并重置间隔。</param>
    private async Task TryRecoverVideoAudioOutputAsync(bool defaultDeviceChanged)
    {
        if (_videoAudioRecoveryRunning || (_videoAudioDevice is null && _audioUnavailableVideoFile is null))
        {
            return;
        }
        if (defaultDeviceChanged)
        {
            _videoAudioRecoveryCheckInterval = VideoAudioRecoveryCheckInterval;
            _videoAudioRecoveryRebuilt = false;
        }
        else if (Environment.TickCount64 < _nextVideoAudioRecoveryCheck)
        {
            return;
        }
        _videoAudioRecoveryRunning = true;
        try
        {
            bool available = await MediaAudioOutputHelper.ProbeDefaultRenderDeviceAsync();
            _nextVideoAudioRecoveryCheck = Environment.TickCount64 + (long)_videoAudioRecoveryCheckInterval.TotalMilliseconds;
            // 正在换背景或已有一次启动在进行：新的启动自己会挑设备，这里不插手
            if (!available || IsUpdateBackgroundRunning || _startMediaPlayerRunning)
            {
                return;
            }
            if (_audioUnavailableVideoFile is string still && still == _lastBackgroundFile)
            {
                _logger.LogInformation("Default audio output device is available again, restart video background: '{File}'", still);
                _videoAudioRecoveryRebuilt = true;
                _ = StartMediaPlayerAsync(still);
            }
            else if (_videoAudioDevice is not null && _lastBackgroundFile is string file && BackgroundService.FileIsSupportedVideo(file))
            {
                _logger.LogInformation("Default audio output device is available again, move video background back from '{Device}': '{File}'", _videoAudioDevice.Name, file);
                // 先把当前画面定格，等默认设备上的新播放器画出第一帧再切回视频表面，不会闪一下
                FreezeVideoDisplay();
                _videoAudioRecoveryRebuilt = true;
                _ = StartMediaPlayerAsync(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recover audio output device of video background");
        }
        finally
        {
            _videoAudioRecoveryRunning = false;
        }
    }


    /// <summary>
    /// 把当前视频帧读回 CPU，做成 <see cref="WriteableBitmap"/>。
    /// 音频输出设备都不可用时给界面留住最后一帧。
    /// </summary>
    /// <returns>冻结帧；当前不是视频背景或抓取失败时返回 null。</returns>
    private WriteableBitmap? TryCreateFrozenVideoBitmap()
    {
        try
        {
            if (_videoSurface is null)
            {
                return null;
            }
            int width = (int)_videoSurface.SizeInPixels.Width;
            int height = (int)_videoSurface.SizeInPixels.Height;
            if (width <= 0 || height <= 0)
            {
                return null;
            }
            byte[] pixelBytes = _videoSurface.GetPixelBytes();
            WriteableBitmap still = new(width, height);
            using (Stream stream = still.PixelBuffer.AsStream())
            {
                stream.Write(pixelBytes, 0, pixelBytes.Length);
            }
            still.Invalidate();
            return still;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Freeze video frame");
            return null;
        }
    }


    /// <summary>
    /// 把视频画面冻成静态图顶上，播放器不动。之后谁画出下一帧，谁就把图源切回视频表面（见 <see cref="MediaPlayer_VideoFrameAvailable"/>）。
    /// </summary>
    /// <returns>当前没有视频画面或抓取失败时返回 false。</returns>
    private bool FreezeVideoDisplay()
    {
        WriteableBitmap? still = TryCreateFrozenVideoBitmap();
        if (still is null)
        {
            return false;
        }
        BackgroundImageSource = still;
        _videoDisplayFrozen = true;
        return true;
    }


    private void MediaPlayer_VideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (!_videoSemaphore.Wait(0))
        {
            return;
        }
        bool queue = DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                if (!ReferenceEquals(sender, _mediaPlayer))
                {
                    return;
                }
                if (_videoSurface is null || _videoImageSource is null)
                {
                    _videoSurface?.Dispose();
                    int width = (int)sender.PlaybackSession.NaturalVideoWidth;
                    int height = (int)sender.PlaybackSession.NaturalVideoHeight;
                    _videoSurface = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
                    _videoImageSource = new CanvasImageSource(CanvasDevice.GetSharedDevice(), width, height, 96);
                    BackgroundImageSource = _videoImageSource;
                }
                sender.CopyFrameToVideoSurface(_videoSurface);
                using var ds = _videoImageSource.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
                ds.DrawImage(_videoSurface);
                if (_videoDisplayFrozen)
                {
                    // 冻结的画面正顶在上面：新播放器画出第一帧后切回视频表面
                    BackgroundImageSource = _videoImageSource;
                    _videoDisplayFrozen = false;
                }
            }
            catch { }
            finally
            {
                _videoSemaphore.Release();
            }
        }) ?? false;
        if (!queue)
        {
            _videoSemaphore.Release();
        }
    }


    private async Task ChangeAccentColorToImageFileAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            Color? color = await Task.Run(async () =>
            {
                string filePath = await _backgroundService.GetBackgroundFileAsync(url, cancellationToken);
                using var fs = File.OpenRead(filePath);
                var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
                var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                Color? color = AccentColorHelper.GetAccentColor(pixelData.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
                return color;
            });
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (color is not null)
            {
                AccentColorHelper.ChangeAppAccentColor(color);
                AppConfig.AccentColor = color?.ToHex();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare video overlay image");
        }
    }


    private void DisposeVideoResource()
    {
        DisposeMediaPlayer();
        _audioUnavailableVideoFile = null;
        _videoDisplayFrozen = false;
        _videoSurface?.Dispose();
        _videoSurface = null;
        _videoImageSource = null;
        VP9Helper.UnregisterVP9Decoder(true);
        VP9Helper.UnregisterVorbisDecoder();
    }


    /// <summary>
    /// 主窗口是否正摆在用户眼前：可见、未最小化、会话未锁屏。亮屏后据此决定立即重建视频播放器，还是留给窗口激活。
    /// </summary>
    private static bool IsMainWindowShowing()
    {
        try
        {
            MainWindow window = MainWindow.Current;
            return window.AppWindow.IsVisible
                && window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }
                && !window.IsSessionLocked;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// 当前会话的显示器是否已关闭。
    /// </summary>
    private static bool IsDisplayOff()
    {
        try
        {
            return MainWindow.Current?.IsDisplayOff ?? false;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// 熄屏：先把当前画面冻成 CPU 位图，再释放播放器。Win2D 表面和解码器注册都保留，亮屏后直接重建。
    /// 不能只 Pause：暂停的播放器熄屏后照样空转（见 <see cref="_videoPlayerReleasedForDisplayOff"/>）。
    /// </summary>
    private void ReleaseVideoPlayerForDisplayOff()
    {
        if (_mediaPlayer is null)
        {
            return;
        }
        FreezeVideoDisplay();
        DisposeMediaPlayer();
        _videoPlayerReleasedForDisplayOff = true;
        _logger.LogInformation("Display is off, release video background player: '{File}'", _lastBackgroundFile);
    }


    /// <summary>
    /// 亮屏：熄屏时释放过播放器，且主窗口正摆在眼前（未隐藏、未最小化、未锁屏）时立即重建续播。
    /// 其余情况交给窗口激活时的兜底重启（<see cref="OnMainWindowStateChanged"/> 的 Activate 分支），与锁屏后靠激活续播的行为一致。
    /// </summary>
    private void RestoreVideoPlayerAfterDisplayOn()
    {
        if (!_videoPlayerReleasedForDisplayOff)
        {
            return;
        }
        _videoPlayerReleasedForDisplayOff = false;
        if (_mediaPlayer is not null || IsUpdateBackgroundRunning || !IsMainWindowShowing() || !BackgroundService.FileIsSupportedVideo(_lastBackgroundFile))
        {
            return;
        }
        _logger.LogInformation("Display is on, restart video background player: '{File}'", _lastBackgroundFile);
        _ = StartMediaPlayerAsync(_lastBackgroundFile!);
    }


    #endregion


    public async Task ChangeCurrentBackgroundIndexAsync(int value)
    {
        try
        {
            if (GameBackgrounds is null)
            {
                return;
            }
            if (value < 0 || value >= GameBackgrounds.Count)
            {
                return;
            }
            GameBackgroundModel model = GameBackgrounds[value];
            if (CurrentGameBackground == model)
            {
                return;
            }
            if (model.Type is 1)
            {
                await SetVideoBackgroundAsync(model.GameBackground);
            }
            else
            {
                await SetImageBackgroundAsync(model.GameBackground);
            }
            CurrentGameBackground = model;
            SaveGameBackgroundSetting();
        }
        catch { }
    }



    private void OnBackgroundChanged(object _, BackgroundChangedMessage message)
    {
        _ = UpdateBackgroundAsync();
    }



    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Hide || message.SessionLock)
            {
                _mediaPlayer?.Pause();
            }
            else if (message.Activate)
            {
                // 关键：正在切换背景（UpdateBackgroundAsync 运行中）时不得在此兜底重启视频。
                // 从视频切换到图片时，DisposeVideoResource 已把 _mediaPlayer 置空，而 _lastBackgroundFile
                // 要等换图完成后才更新为新图片；此空窗期内 _lastBackgroundFile 仍指向旧视频。
                if (IsUpdateBackgroundRunning)
                {
                    return;
                }
                if (_audioUnavailableVideoFile is not null && _audioUnavailableVideoFile == _lastBackgroundFile)
                {
                    // 音频输出设备都建不了流、显示着静态画面：同样不走兜底重启（还会失败），只检查默认设备是否已恢复
                    _ = TryRecoverVideoAudioOutputAsync(defaultDeviceChanged: false);
                }
                else if (_videoAudioDevice is not null)
                {
                    // 在备用音频设备上播放：先续播，再看看默认设备是否已恢复，恢复了就搬回去
                    if (_mediaPlayer is not null && _mediaPlayer.PlaybackSession.PlaybackState is not MediaPlaybackState.Playing)
                    {
                        _mediaPlayer.Play();
                    }
                    _ = TryRecoverVideoAudioOutputAsync(defaultDeviceChanged: false);
                }
                else if (_mediaPlayer is not null)
                {
                    if (_mediaPlayer.PlaybackSession.PlaybackState is not MediaPlaybackState.Playing)
                    {
                        _mediaPlayer.Play();
                    }
                }
                else if (!_startMediaPlayerRunning && BackgroundService.FileIsSupportedVideo(_lastBackgroundFile))
                {
                    // 熄屏时释放了播放器，亮屏时窗口不可见 / 最小化 / 锁屏，等窗口激活再重建
                    _ = StartMediaPlayerAsync(_lastBackgroundFile!);
                }
            }
        }
        catch { }
    }


    private void OnMainWindowDisplayStatusChanged(object _, MainWindowDisplayStatusChangedMessage message)
    {
        try
        {
            if (message.DisplayOff)
            {
                ReleaseVideoPlayerForDisplayOff();
            }
            else if (message.DisplayOn)
            {
                RestoreVideoPlayerAfterDisplayOn();
            }
        }
        catch { }
    }



    public async Task CopyCurrentBackgroundAsync()
    {
        try
        {
            if (CurrentGameBackground is null)
            {
                return;
            }
            string path;
            if (CurrentGameBackground.GameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO && IsVideoPlaying)
            {
                path = await _backgroundService.GetBackgroundFileAsync(CurrentGameBackground.GameBackground.Video.Url);
            }
            else
            {
                path = await _backgroundService.GetBackgroundFileAsync(CurrentGameBackground.GameBackground.Background.Url);
            }
            var file = await StorageFile.GetFileFromPathAsync(path);
            ClipboardHelper.SetStorageItems(DataPackageOperation.Copy, file);
            InAppToast.MainWindow?.Information(Lang.Common_CopiedToClipboard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy current background image {GameId}", CurrentGameId);
        }
    }



    public async Task SaveCurrentBackgroundAsync()
    {
        try
        {
            if (CurrentGameBackground is null)
            {
                return;
            }
            string path;
            if (CurrentGameBackground.GameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO && IsVideoPlaying)
            {
                path = await _backgroundService.GetBackgroundFileAsync(CurrentGameBackground.GameBackground.Video.Url);
            }
            else
            {
                path = await _backgroundService.GetBackgroundFileAsync(CurrentGameBackground.GameBackground.Background.Url);
            }
            var savePath = await FileDialogHelper.OpenSaveFileDialogAsync(this.XamlRoot, Path.GetFileName(path));
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                File.Copy(path, savePath, true);
                var file = await StorageFile.GetFileFromPathAsync(savePath);
                var options = new FolderLauncherOptions();
                options.ItemsToSelect.Add(file);
                await Launcher.LaunchFolderAsync(await file.GetParentAsync(), options);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save as current background image {GameId}", CurrentGameId);
        }
    }



    private bool _vp9DecoderSuggested;


    private void SuggestToInstallVP9Decoder()
    {
        if (!_vp9DecoderSuggested)
        {
            InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning, null, Lang.ItIsRecommendedToInstallTheVP9VideoExtensionsToReduceCPUUsage, Lang.SettingPage_Download, async () => await Launcher.LaunchUriAsync(new("https://apps.microsoft.com/detail/9n4d0msmp0pt")));
            _vp9DecoderSuggested = true;
        }
    }


}