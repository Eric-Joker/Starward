using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.StarRail.ApocalypticShadow;
using Starward.Helpers;
using Starward.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Starward.Services.Cache;
using Windows.Storage;
using System.Drawing;
using System.IO;
using System.ComponentModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Starward.Pages.HoyolabToolbox;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
[INotifyPropertyChanged]
public sealed partial class ApocalypticShadowPage : PageBase
{


    private readonly ILogger<ApocalypticShadowPage> _logger = AppConfig.GetLogger<ApocalypticShadowPage>();

    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();



    public ApocalypticShadowPage()
    {
        this.InitializeComponent();
    }



    private GameRecordRole gameRole;


    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is GameRecordRole role)
        {
            gameRole = role;
        }
    }



    protected override async void OnLoaded()
    {
        await Task.Delay(16);
        InitializeApocalypticShadowInfoData();
    }




    [ObservableProperty]
    private List<ApocalypticShadowInfo> apocalypticShadowList;


    [ObservableProperty]
    private ApocalypticShadowInfo? currentApocalypticShadow;



    private void InitializeApocalypticShadowInfoData()
    {
        try
        {
            CurrentApocalypticShadow = null;
            var list = _gameRecordService.GetApocalypticShadowInfoList(gameRole);
            if (list.Count != 0)
            {
                ApocalypticShadowList = list;
                ListView_ForgottenHall.SelectedIndex = 0;
            }
            else
            {
                Image_Emoji.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Init apocalyptic shadow data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
        }
    }




    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        try
        {
            if (gameRole is null)
            {
                return;
            }
            await _gameRecordService.RefreshApocalypticShadowInfoAsync(gameRole, 1);
            await _gameRecordService.RefreshApocalypticShadowInfoAsync(gameRole, 2);
            InitializeApocalypticShadowInfoData();
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogError(ex, "Refresh apocalyptic shadow data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
            HoyolabToolboxPage.HandleMiHoYoApiException(ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Refresh apocalyptic shadow data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
            NotificationBehavior.Instance.Warning(Lang.Common_NetworkError, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh apocalyptic shadow data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
            NotificationBehavior.Instance.Error(ex);
        }
    }



    private void ListView_ForgottenHall_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (e.AddedItems.FirstOrDefault() is ApocalypticShadowInfo info)
            {
                CurrentApocalypticShadow = _gameRecordService.GetApocalypticShadowInfo(gameRole, info.ScheduleId);
                Image_Emoji.Visibility = (CurrentApocalypticShadow?.HasData ?? false) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Selection changed ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
        }
    }

    private void TextBlock_Deepest_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        TextBlock_Deepest.SetValue(Grid.ColumnSpanProperty, 2);
        TextBlock_Battles.SetValue(Grid.RowProperty, 1);
        TextBlock_Battles.SetValue(Grid.ColumnProperty, 1);
        TextBlock_Battles.SetValue(Grid.ColumnSpanProperty, 2);
    }


    private static Dictionary<string, BitmapImage> convertedImages = new Dictionary<string, BitmapImage>();

    public static object BossIcon(string Icon, bool BossDefeated)
    {
        if (BossDefeated)
        {
            if (convertedImages.TryGetValue(Icon, out BitmapImage? grayIcon))
            {
                return grayIcon;
            }
            else
            {
                var file = Task.Run(async () =>
                {
                    return await FileCacheService.Instance.GetFromCacheAsync(new Uri(Icon));
                }).Result;
                if (file is StorageFile)
                {
                    var iconBitmap = new Bitmap(file.Path);
                    var grayBitmap = new Bitmap(iconBitmap.Width, iconBitmap.Height);
                    for (int i = 0; i < iconBitmap.Width; i++)
                    {
                        for (int j = 0; j < iconBitmap.Height; j++)
                        {
                            var originalColor = iconBitmap.GetPixel(i, j);
                            int grayScale = (int)((originalColor.R * 0.3) + (originalColor.G * 0.59) + (originalColor.B * 0.11));
                            var newColor = Color.FromArgb(originalColor.A, grayScale, grayScale, grayScale);
                            grayBitmap.SetPixel(i, j, newColor);
                        }
                    }
                    grayIcon = new BitmapImage();
                    using (MemoryStream stream = new MemoryStream())
                    {
                        grayBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                        stream.Position = 0;
                        grayIcon.SetSource(stream.AsRandomAccessStream());
                    }
                    convertedImages[Icon] = grayIcon;
                    return grayIcon;
                }
            }
        }
        return Icon;
    }
}
