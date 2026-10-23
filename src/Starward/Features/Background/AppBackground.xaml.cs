using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starward.Core.HoYoPlay;
using Starward.Features.Codec;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI;
using WinRT;
using DeviceInformation = Windows.Devices.Enumeration.DeviceInformation;


namespace Starward.Features.Background;

[INotifyPropertyChanged]
public sealed partial class AppBackground : UserControl
{

    public static AppBackground Current { get; private set; }


    private readonly ILogger<AppBackground> _logger = AppConfig.GetLogger<AppBackground>();

    private readonly BackgroundService _backgroundService = AppConfig.GetService<BackgroundService>();

    private readonly FavorWallpaperService _favorWallpaperService = AppConfig.GetService<FavorWallpaperService>();


    public AppBackground()
    {
        Current = this;
        this.InitializeComponent();
        WeakReferenceMessenger.Default.Register<BackgroundChangedMessage>(this, OnBackgroundChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<VideoBgVolumeChangedMessage>(this, OnVideoBgVolumeChanged);
        this.Loaded += AppBackground_Loaded;
        this.Unloaded += AppBackground_Unloaded;
    }


    private void AppBackground_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        this.XamlRoot.Changed -= XamlRoot_Changed;
        this.XamlRoot.Changed += XamlRoot_Changed;
        MediaDevice.DefaultAudioRenderDeviceChanged -= MediaDevice_DefaultAudioRenderDeviceChanged;
        MediaDevice.DefaultAudioRenderDeviceChanged += MediaDevice_DefaultAudioRenderDeviceChanged;
    }


    private void AppBackground_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        DisposeVideoResource();
        this.XamlRoot?.Changed -= XamlRoot_Changed;
        // 静态事件会一直拿着本实例，必须退订
        MediaDevice.DefaultAudioRenderDeviceChanged -= MediaDevice_DefaultAudioRenderDeviceChanged;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private void XamlRoot_Changed(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args)
    {
        if (_lastScale != sender.RasterizationScale)
        {
            _ = UpdateBackgroundAsync();
        }
    }



    public GameId CurrentGameId
    {
        get; set
        {
            // 换了游戏：开了随机壁纸的话先换一张（未开启时这个方法什么都不做）。
            if (value is not null && field?.GameBiz != value.GameBiz)
            {
                _favorWallpaperService.TryShuffleWallpaper(value.GameBiz);
            }
            if (field is null)
            {
                field = value;
                InitializeBackgroundImage();
            }
            field = value;
            _ = UpdateBackgroundAsync();
        }
    }


    public ImageSource? PlacehoderImageSource { get; set => SetProperty(ref field, value); }

    public ImageSource? BackgroundImageSource
    {
        get; set
        {
            if (value is null && field is not null)
            {
                PlacehoderImageSource = field;
            }
            SetProperty(ref field, value);
        }
    }

    public bool IsUpdateBackgroundRunning { get; set => SetProperty(ref field, value); }

    public GameBackground? CurrentGameBackground { get; private set; }

    private string? _lastBackgroundFile;

    private double _lastScale = 1;


    private void InitializeBackgroundImage()
    {
        try
        {
            var file = BackgroundService.GetCachedBackgroundFile(CurrentGameId);
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



    private CancellationTokenSource? updateBackgroundCts;


    public async Task UpdateBackgroundAsync(GameBackground? background = null)
    {
        string? imageFilePath = null;
        try
        {
            IsUpdateBackgroundRunning = true;

            updateBackgroundCts?.Cancel();
            updateBackgroundCts = new();
            CancellationToken cancellationToken = updateBackgroundCts.Token;

            if (CurrentGameId is null)
            {
                DisposeVideoResource();
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                bool apiCancelled = false;
                string? filePath = null;
                GameBackground? gameBackground = null;
                try
                {
                    bool timeout = i == 0 && background is null;
                    CancellationToken apiCancellationToken = timeout ? new CancellationTokenSource(1000).Token : CancellationToken.None;
                    CancellationToken downloadCancellationToken = timeout ? new CancellationTokenSource(3000).Token : CancellationToken.None;
                    gameBackground = background ?? await _backgroundService.GetSuggestedGameBackgroundAsync(CurrentGameId, apiCancellationToken);
                    if (gameBackground is null)
                    {
                        filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                    }
                    else if (gameBackground.Type is GameBackground.BACKGROUND_TYPE_CUSTOM)
                    {
                        filePath = gameBackground.Background.Url;
                    }
                    else if (gameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO && !gameBackground.StopVideo)
                    {
                        filePath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Video.Url, downloadCancellationToken);
                    }
                    else
                    {
                        filePath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Background.Url, downloadCancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    apiCancelled = true;
                    filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                }
                catch (Exception ex)
                {
                    filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                    _logger.LogError(ex, "Update background image");
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (filePath == _lastBackgroundFile)
                {
                    if (BackgroundService.FileIsSupportedVideo(filePath))
                    {
                        continue;
                    }
                    if (_lastScale == this.XamlRoot.GetUIScaleFactor())
                    {
                        continue;
                    }
                }
                DisposeVideoResource();
                BackgroundImageSource = null;
                if (filePath != null)
                {
                    if (gameBackground?.Type is GameBackground.BACKGROUND_TYPE_VIDEO)
                    {
                        await SetVideoBackgroundAsync(gameBackground, filePath, cancellationToken);
                    }
                    else if (BackgroundService.FileIsSupportedVideo(filePath))
                    {
                        _ = StartMediaPlayerAsync(filePath);
                    }
                    else
                    {
                        imageFilePath = filePath;
                        await ChangeBackgroundImageAsync(filePath, cancellationToken);
                    }
                    _lastBackgroundFile = filePath;
                    _lastScale = this.XamlRoot.GetUIScaleFactor();
                    CurrentGameBackground = gameBackground;
                    if (!apiCancelled && gameBackground?.Type is not GameBackground.BACKGROUND_TYPE_CUSTOM)
                    {
                        AppConfig.SetBg(CurrentGameId.GameBiz, Path.GetFileName(filePath));
                        var list = await _backgroundService.GetGameBackgroundsAsync(CurrentGameId);
                        AppConfig.SetGameBackgroundIds(CurrentGameId.GameBiz, string.Join(',', list.Select(x => x.Id)));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (COMException ex) when (ex.HResult == -2003292277)
        {
            // 0x88982F8B
            if (Path.GetExtension(imageFilePath)?.Equals(".webp", StringComparison.OrdinalIgnoreCase) ?? false)
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
            _logger.LogError(ex, "Cannot decode image: '{path}'", imageFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update background image");
        }
        finally
        {
            IsUpdateBackgroundRunning = false;
        }
    }


    private async Task ChangeBackgroundImageAsync(string file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var fs = File.OpenRead(file);
        var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());

        double scale = this.XamlRoot.GetUIScaleFactor();
        int decodeWidth = 0, decodeHeight = 0;
        double windowWidth = ActualWidth * scale, windowHeight = ActualHeight * scale;

        if (decoder.PixelWidth <= windowWidth || decoder.PixelHeight <= windowHeight)
        {
            decodeWidth = (int)decoder.PixelWidth;
            decodeHeight = (int)decoder.PixelHeight;
            var writeableBitmap = new WriteableBitmap(decodeWidth, decodeHeight);
            fs.Position = 0;
            await writeableBitmap.SetSourceAsync(fs.AsRandomAccessStream());
            cancellationToken.ThrowIfCancellationRequested();
            Color? color = AccentColorHelper.GetAccentColor(writeableBitmap.PixelBuffer, decodeWidth, decodeHeight);
            AccentColorHelper.ChangeAppAccentColor(color);
            AppConfig.AccentColor = color?.ToHex() ?? null;
            BackgroundImageSource = writeableBitmap;
        }
        else
        {
            if (windowWidth * decoder.PixelHeight > windowHeight * decoder.PixelWidth)
            {
                decodeWidth = (int)windowWidth;
                decodeHeight = (int)(windowWidth * decoder.PixelHeight / decoder.PixelWidth);
            }
            else
            {
                decodeHeight = (int)windowHeight;
                decodeWidth = (int)(windowHeight * decoder.PixelWidth / decoder.PixelHeight);
            }
            using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                                                                  BitmapAlphaMode.Premultiplied,
                                                                  new BitmapTransform
                                                                  {
                                                                      ScaledWidth = (uint)decodeWidth,
                                                                      ScaledHeight = (uint)decodeHeight,
                                                                      InterpolationMode = BitmapInterpolationMode.Fant
                                                                  },
                                                                  ExifOrientationMode.IgnoreExifOrientation,
                                                                  ColorManagementMode.DoNotColorManage);
            var softwareBitmapSource = new SoftwareBitmapSource();
            await softwareBitmapSource.SetBitmapAsync(soft);

            cancellationToken.ThrowIfCancellationRequested();

            using BitmapBuffer bitmapBuffer = soft.LockBuffer(BitmapBufferAccessMode.Read);
            using IMemoryBufferReference memoryBufferReference = bitmapBuffer.CreateReference();
            memoryBufferReference.As<AccentColorHelper.IMemoryBufferByteAccess>().GetBuffer(out nint bufferPtr, out uint capacity);
            Color? color = AccentColorHelper.GetAccentColor(bufferPtr, capacity, decodeWidth, decodeHeight);
            AccentColorHelper.ChangeAppAccentColor(color);
            AppConfig.AccentColor = color?.ToHex() ?? null;
            BackgroundImageSource = softwareBitmapSource;
        }
    }



    #region Video



    private MediaPlayer? _mediaPlayer;

    private CanvasRenderTarget? _videoSurface;

    private CanvasBitmap? _videoOverlayImage;

    private CanvasImageSource? _videoImageSource;

    private int videoBgVolume = AppConfig.VideoBgVolume;

    private SemaphoreSlim _videoSemaphore = new SemaphoreSlim(1, 1);

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


    /// <summary>
    /// 为指定的视频文件启动 MediaPlayer（使用帧服务器模式）。
    /// .webm 文件会根据检测结果注册 VP9/Vorbis 本地解码器。
    /// 默认音频输出设备已知建不了流时改用备用设备（音量 0），一个都不行就显示静态画面，见 <see cref="MediaAudioOutputHelper"/>。
    /// 熄屏期间不创建播放器（帧服务器会空转吃满一个核心），等亮屏再建。
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

            DisposeMediaPlayer();
            _audioUnavailableVideoFile = null;
            _videoAudioDevice = audioDevice;
            _videoAudioDeviceId = audioDevice?.Id ?? MediaAudioOutputHelper.GetDefaultRenderDeviceId();

            _mediaPlayer = new MediaPlayer
            {
                IsLoopingEnabled = true,
                // 备用设备上一律音量 0：用户设的音量只对默认设备有意义，别让声音从意料之外的设备里出来
                Volume = audioDevice is null ? videoBgVolume / 100.0 : 0,
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
    /// 释放播放器及其音频设备选择，不碰 Win2D 表面、叠加图和解码器。
    /// 同时让仍在挑选音频设备的启动过期，熄屏释放、换设备重建时用。
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
    }


    private async Task SetVideoBackgroundAsync(GameBackground gameBackground, string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BackgroundService.FileIsSupportedVideo(filePath))
        {
            _ = StartMediaPlayerAsync(filePath);
            _ = PrepareVideoOverlayImageAsync(gameBackground.Theme.Url, cancellationToken);
            _ = ChangeAccentColorToImageFileAsync(gameBackground.Background.Url, cancellationToken);
        }
        else
        {
            string overlayPath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Theme.Url, cancellationToken);
            using var fs1 = File.OpenRead(filePath);
            using var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs1.AsRandomAccessStream(), 96);
            using var fs2 = File.OpenRead(overlayPath);
            using var overlay = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs2.AsRandomAccessStream(), 96);
            var imageSource = new CanvasImageSource(CanvasDevice.GetSharedDevice(), bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
            using (var ds = imageSource.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                ds.DrawImage(bitmap);
                Rect source = new Rect(0, 0, overlay.SizeInPixels.Width, overlay.SizeInPixels.Height);
                Rect dest = new Rect(0, 0, imageSource.SizeInPixels.Width, imageSource.SizeInPixels.Height);
                ds.DrawImage(overlay, dest, source, 1, CanvasImageInterpolation.HighQualityCubic);
            }
            BackgroundImageSource = imageSource;
            if (bitmap.Format is Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized)
            {
                try
                {
                    Color? color = await Task.Run(() =>
                    {
                        Color? color = AccentColorHelper.GetAccentColor(bitmap.GetPixelBytes(), (int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
                        return color;
                    });
                    if (color is not null)
                    {
                        AccentColorHelper.ChangeAppAccentColor(color);
                        AppConfig.AccentColor = color?.ToHex() ?? null;
                    }
                }
                catch { }
            }

        }
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
    /// <para>
    /// 实测故障里坏掉的耳机一直是默认设备、默认设备变化事件不会来，所以窗口激活时也要查。查法是 AudioGraph 试建流，不出声，
    /// 不会像直接重建播放器那样每次激活都把视频重启一遍。
    /// </para>
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
    /// 把当前视频帧（含 overlay）读回 CPU，做成 <see cref="WriteableBitmap"/>。
    /// 熄屏释放播放器、音频输出设备都不可用时给界面留住最后一帧。
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
            using CanvasRenderTarget composed = new(CanvasDevice.GetSharedDevice(), width, height, 96);
            using (CanvasDrawingSession ds = composed.CreateDrawingSession())
            {
                ds.DrawImage(_videoSurface);
                if (_videoOverlayImage is not null)
                {
                    Rect source = new Rect(0, 0, _videoOverlayImage.SizeInPixels.Width, _videoOverlayImage.SizeInPixels.Height);
                    Rect dest = new Rect(0, 0, width, height);
                    ds.DrawImage(_videoOverlayImage, dest, source, 1, CanvasImageInterpolation.HighQualityCubic);
                }
            }
            byte[] pixelBytes = composed.GetPixelBytes();
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
        PlacehoderImageSource = still;
        BackgroundImageSource = still;
        _videoDisplayFrozen = true;
        return true;
    }


    private void MediaPlayer_VideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (_videoSemaphore.CurrentCount == 0)
        {
            return;
        }
        _videoSemaphore.Wait();
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
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
                if (_videoOverlayImage is not null)
                {
                    Rect source = new Rect(0, 0, _videoOverlayImage.SizeInPixels.Width, _videoOverlayImage.SizeInPixels.Height);
                    Rect dest = new Rect(0, 0, _videoImageSource.SizeInPixels.Width, _videoImageSource.SizeInPixels.Height);
                    ds.DrawImage(_videoOverlayImage, dest, source, 1, CanvasImageInterpolation.HighQualityCubic);
                }
                if (_videoDisplayFrozen)
                {
                    // 熄屏 / 音频设备不可用时冻住的画面正顶在上面：新播放器画出第一帧后切回视频表面
                    BackgroundImageSource = _videoImageSource;
                    _videoDisplayFrozen = false;
                }
            }
            catch { }
            finally
            {
                _videoSemaphore.Release();
            }
        });
    }


    /// <summary>
    /// 把正在播放的视频背景的<b>当前帧</b>存成一张 PNG 并返回文件路径；
    /// 没有在放视频背景、当前显示的不是 <paramref name="gameId"/> 的背景、
    /// 或者视频还没出帧时返回 null。
    /// <para>
    /// 视频壁纸的帧只存在于 <see cref="_mediaPlayer"/> 里，磁盘上没有对应的图片文件
    /// （<see cref="BackgroundService.GetCachedBackgroundFile"/> 给出的是视频文件本身），
    /// 分享图需要一张静态背景时就现抓这一帧。
    /// </para>
    /// <para>必须在 UI 线程调用：<see cref="MediaPlayer.CopyFrameToVideoSurface"/> 要用到播放器的 DispatcherQueue。</para>
    /// </summary>
    public async Task<string?> CaptureCurrentVideoFrameAsync(GameId gameId)
    {
        try
        {
            if (gameId is null || _mediaPlayer is null || CurrentGameId?.GameBiz != gameId.GameBiz)
            {
                return null;
            }
            int width = (int)_mediaPlayer.PlaybackSession.NaturalVideoWidth;
            int height = (int)_mediaPlayer.PlaybackSession.NaturalVideoHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }
            // 先在 UI 线程把这一帧拷进一块新表面：拷完就和播放器无关了，
            // 之后异步落盘期间即使背景被换掉也不会拿到半张图。
            using var frame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            _mediaPlayer.CopyFrameToVideoSurface(frame);
            string folder = Path.Join(AppConfig.CacheFolder, "bgframe");
            Directory.CreateDirectory(folder);
            string file = Path.Join(folder, $"{gameId.GameBiz}.png");
            await frame.SaveAsync(file, CanvasBitmapFileFormat.Png);
            return file;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture current video background frame ({gameId})", gameId);
            return null;
        }
    }


    private async Task PrepareVideoOverlayImageAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            string filePath = await _backgroundService.GetBackgroundFileAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            _videoOverlayImage?.Dispose();
            using var fs = File.OpenRead(filePath);
            _videoOverlayImage = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs.AsRandomAccessStream(), 96);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare video overlay image");
        }
    }


    private async Task ChangeAccentColorToImageFileAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            Color? color = await Task.Run(async () =>
            {
                string filePath = await _backgroundService.GetBackgroundFileAsync(url, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                using var fs = File.OpenRead(filePath);
                var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
                var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                Color? color = AccentColorHelper.GetAccentColor(pixelData.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
                return color;
            });
            if (color is not null)
            {
                AccentColorHelper.ChangeAppAccentColor(color);
                AppConfig.AccentColor = color?.ToHex() ?? null;
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
        _videoOverlayImage?.Dispose();
        _videoOverlayImage = null;
        VP9Helper.UnregisterVP9Decoder(true);
        VP9Helper.UnregisterVorbisDecoder();
    }







    #endregion



    private void OnBackgroundChanged(object _, BackgroundChangedMessage message)
    {
        _ = UpdateBackgroundAsync(message.GameBackground);
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
                // 要等 await ChangeBackgroundImageAsync 完成后才更新为新图片；此空窗期内 _lastBackgroundFile 仍指向
                // 旧视频。若此时收到窗口激活消息（如从「图库」窗口把图片拖到首页，落点激活主窗口），
                // 兜底分支会用这个过期路径把刚释放的视频重新拉起，其帧回调再覆盖掉刚设好的图片，表现为「更换失败」。
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
            }
        }
        catch { }
    }


    private void OnVideoBgVolumeChanged(object _, VideoBgVolumeChangedMessage message)
    {
        try
        {
            videoBgVolume = message.Volume;
            // 备用音频设备上保持音量 0，回到默认设备时会按设置重新取音量
            if (_mediaPlayer is not null && _videoAudioDevice is null)
            {
                _mediaPlayer.Volume = message.Volume / 100d;
            }
        }
        catch { }
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
