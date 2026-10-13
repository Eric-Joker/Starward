using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.Genshin.SpiralAbyss;
using Starward.Features.GameRecord.Share;
using Starward.Frameworks;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;


namespace Starward.Features.GameRecord.Genshin;

public sealed partial class SpiralAbyssPage : PageBase
{


    private readonly ILogger<SpiralAbyssPage> _logger = AppConfig.GetLogger<SpiralAbyssPage>();

    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();



    public SpiralAbyssPage()
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
        await Task.Delay(160);
        InitializeAbyssData();
    }



    protected override void OnUnloaded()
    {
        CurrentAbyss = null;
        AbyssList = null!;
    }



    [ObservableProperty]
    private bool hasData;



    [ObservableProperty]
    private List<SpiralAbyssInfo> abyssList;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RevealRankInternalStar))]
    private SpiralAbyssInfo? currentAbyss;


    public List<int> RevealRankInternalStar => Enumerable.Range(0, Math.Clamp((CurrentAbyss?.RevealRank?.Count ?? 1) - 1, 0, int.MaxValue)).ToList();


    private void InitializeAbyssData()
    {
        try
        {
            CurrentAbyss = null;
            var list = _gameRecordService.GetSpiralAbyssInfoList(gameRole);
            if (list.Any())
            {
                AbyssList = list;
                ListView_AbyssList.SelectedIndex = 0;
            }
            else
            {
                Image_Emoji.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Init abyss data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
        }
    }




    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        // 用户点页面上的刷新按钮：这次算用户发起，允许弹人机验证（后台自动路径仍默认抑制）。
        using IDisposable captchaScope = GameRecordService.AllowInteractiveCaptcha();
        try
        {
            if (gameRole is null)
            {
                return;
            }
            await _gameRecordService.RefreshSpiralAbyssInfoAsync(gameRole, 1);
            await _gameRecordService.RefreshSpiralAbyssInfoAsync(gameRole, 2);
            InitializeAbyssData();
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogError(ex, "Refresh abyss data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
            GameRecordPage.HandleMiHoYoApiException(ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Refresh abyss data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
            InAppToast.MainWindow?.Warning(Lang.Common_NetworkError, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh abyss data ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
            InAppToast.MainWindow?.Error(ex);
        }
    }




    private void ListView_AbyssList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (e.AddedItems.FirstOrDefault() is SpiralAbyssInfo info)
            {
                CurrentAbyss = _gameRecordService.GetSpiralAbyssInfo(gameRole, info.ScheduleId);
                HasData = CurrentAbyss?.TotalBattleCount > 0;
                Image_Emoji.Visibility = HasData ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Selection changed ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
        }
    }


    private void UserControl_AbyssLevel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 720)
        {
            VisualStateManager.GoToState((Control)sender, "WideState", false);
        }
        else
        {
            VisualStateManager.GoToState((Control)sender, "NarrowState", false);
        }
    }



    public static string FloorX(int x)
    {
        return Lang.SpiralAbyssPage_FloorX.Replace("{X}", x.ToString());
    }



    public static string LevelX(int x)
    {
        return Lang.SpiralAbyssPage_ChamberX.Replace("{X}", x.ToString());
    }



    public bool IsSharingRecordImage { get; set => SetProperty(ref field, value); }



    private async void Button_ShareRecordImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentAbyss is not { } data)
            {
                InAppToast.MainWindow?.Warning(Lang.HoyolabToolboxPage_NoData);
                return;
            }
            await GameRecordShareHelper.ShareAsync(this, gameRole, _logger,
                busy => IsSharingRecordImage = busy,
                (bg, accent) => SpiralAbyssShareRenderer.RenderAndSaveAsync(data, gameRole.Uid, bg, accent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Share game record image");
            InAppToast.MainWindow?.Error(Lang.Common_Error);
        }
    }


}
