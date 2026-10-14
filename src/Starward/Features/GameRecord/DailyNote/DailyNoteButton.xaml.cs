using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.BH3.DailyNote;
using Starward.Core.GameRecord.Genshin.DailyNote;
using Starward.Core.GameRecord.StarRail.DailyNote;
using Starward.Core.GameRecord.ZZZ.DailyNote;
using Starward.Core.HoYoPlay;
using System;
using System.Threading.Tasks;


namespace Starward.Features.GameRecord.DailyNote;

[INotifyPropertyChanged]
public sealed partial class DailyNoteButton : UserControl
{

    private readonly ILogger<DailyNoteButton> _logger = AppConfig.GetLogger<DailyNoteButton>();


    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();


    public DailyNoteButton()
    {
        this.InitializeComponent();
        this.Visibility = Visibility.Collapsed;
        // 登录 / 新增角色后 GameRecordRoleChangedMessage 会到达，此时必须重新解析角色并重算可见性：
        // 本控件只在 Loaded 时刷新一次，而登录完成可能发生在它已经加载之后（那时它还是 Collapsed）。
        WeakReferenceMessenger.Default.Register<GameRecordRoleChangedMessage>(this, (_, _) => OnGameRecordRoleChanged());
    }



    /// <summary>
    /// 账号角色列表变化后重新解析当前角色，并在解析到时让按钮出现（不轮询、不改计时器）。
    /// </summary>
    private void OnGameRecordRoleChanged()
    {
        if (!IsLoaded || CurrentGameId is null)
        {
            return;
        }
        if (!GameFeatureConfig.FromGameId(CurrentGameId).SupportDailyNote)
        {
            return;
        }
        RefreshDailyNoteCommand.Execute(true);
    }



    public GameId CurrentGameId
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsBH3Enabled));
                OnPropertyChanged(nameof(IsGenshinEnabled));
                OnPropertyChanged(nameof(IsStarRailEnabled));
                OnPropertyChanged(nameof(IsZZZEnabled));
            }
        }
    }



    public bool IsBH3Enabled => CurrentGameId?.GameBiz.Game is GameBiz.bh3;

    public bool IsGenshinEnabled => CurrentGameId?.GameBiz.Game is GameBiz.hk4e;

    public bool IsStarRailEnabled => CurrentGameId?.GameBiz.Game is GameBiz.hkrpg;

    public bool IsZZZEnabled => CurrentGameId?.GameBiz.Game is GameBiz.nap;



    private GameRecordRole? GameRecordRole { get; set => SetProperty(ref field, value); }


    public BH3DailyNote BH3DailyNote { get; set => SetProperty(ref field, value); }

    public GenshinDailyNote GenshinDailyNote { get; set => SetProperty(ref field, value); }

    public StarRailDailyNote StarRailDailyNote { get; set => SetProperty(ref field, value); }

    public ZZZDailyNote ZZZDailyNote { get; set => SetProperty(ref field, value); }


    public string? ErrorMessage { get; set => SetProperty(ref field, value); }



    private void Button_DailyNote_Loaded(object sender, RoutedEventArgs e)
    {
        if (CurrentGameId is null)
        {
            return;
        }
        if (!GameFeatureConfig.FromGameId(CurrentGameId).SupportDailyNote)
        {
            return;
        }
        RefreshDailyNoteCommand.Execute(false);
    }



    private void Button_DailyNote_Unloaded(object sender, RoutedEventArgs e)
    {
        GameRecordRole = null;
        BH3DailyNote = null!;
        GenshinDailyNote = null!;
        StarRailDailyNote = null!;
        ZZZDailyNote = null!;
    }



    [RelayCommand]
    private async Task RefreshDailyNoteAsync(bool? forceUpdate)
    {
        // 只有浮窗上的刷新按钮是不带参数调用（命令无参数 → forceUpdate 为 null）：那次算用户发起，允许弹人机验证。
        // 页面 Loaded（false）与角色变化（true）那两次自动调用继续抑制，不加载极验脚本。
        using IDisposable captchaScope = forceUpdate is null
            ? GameRecordService.AllowInteractiveCaptcha()
            : GameRecordService.SuppressInteractiveCaptcha();
        try
        {
            if (!forceUpdate.HasValue)
            {
                forceUpdate = true;
            }
            GameBiz gameBiz = CurrentGameId.GameBiz;
            if (gameBiz.Server is "bilibili")
            {
                gameBiz = $"{gameBiz.Game}_cn";
            }
            _gameRecordService.IsHoyolab = gameBiz.Server is "global";
            GameRecordRole = _gameRecordService.GetLastSelectGameRecordRoleOrTheFirstOne(gameBiz);
            if (GameRecordRole is not null)
            {
                this.Visibility = Visibility.Visible;
                ErrorMessage = null;
                await _gameRecordService.UpdateDeviceFpAsync();
                if (IsBH3Enabled)
                {
                    BH3DailyNote = await _gameRecordService.GetBH3DailyNoteAsync(GameRecordRole, forceUpdate.Value);
                }
                if (IsGenshinEnabled)
                {
                    GenshinDailyNote = await _gameRecordService.GetGenshinDailyNoteAsync(GameRecordRole, forceUpdate.Value);
                }
                if (IsStarRailEnabled)
                {
                    StarRailDailyNote = await _gameRecordService.GetStarRailDailyNoteAsync(GameRecordRole, forceUpdate.Value);
                }
                if (IsZZZEnabled)
                {
                    ZZZDailyNote = await _gameRecordService.GetZZZDailyNoteAsync(GameRecordRole, forceUpdate.Value);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is miHoYoApiException)
            {
                ErrorMessage = $"Error Code: {ex.Message}";
            }
            _logger.LogError(ex, "Refresh daily note failed (Biz: {GameBiz}, Server: {GameServer}, Uid: {Uid})", CurrentGameId?.GameBiz, GameRecordRole?.Region, GameRecordRole?.Uid);
        }
    }





    public static string ZZZMemberCardRemainingDaysToString(int remainingDays)
    {
        return string.Format(Lang.DailyNoteButton_0DaySRemaining, remainingDays);
    }




    /// <summary>
    /// 参量质变仪整行是否显示。
    /// </summary>
    /// <param name="transformer">参量质变仪，数据缺失时为 null。</param>
    /// <returns>已获得时为 <see cref="Visibility.Visible"/>。</returns>
    /// <remarks>未获得时接口仍返回 reached=true 的空冷却时间，必须判 <see cref="Transformer.Obtained"/>，否则低等级账号会只剩一行没有图标和名称的「可使用」。</remarks>
    public static Visibility GenshinTransformerVisibility(Transformer? transformer)
    {
        return transformer is { Obtained: true } ? Visibility.Visible : Visibility.Collapsed;
    }


    /// <summary>
    /// 参量质变仪已冷却完毕，可再次使用。
    /// </summary>
    /// <param name="transformer">参量质变仪，未拥有时为 null。</param>
    /// <returns>可使用时为 <see cref="Visibility.Visible"/>。</returns>
    /// <remarks>x:Bind 函数绑定返回 bool 再隐式转 Visibility 会生成无法编译的代码，只能直接返回 Visibility。</remarks>
    public static Visibility GenshinTransformerReadyVisibility(Transformer? transformer)
    {
        // 未解锁时接口仍返回 reached=true 的空冷却时间，须先判 Obtained，否则低等级账号会只剩一行没有图标和名称的「可使用」
        return transformer is { Obtained: true, RecoveryTime.Reached: true } ? Visibility.Visible : Visibility.Collapsed;
    }


    /// <summary>
    /// 参量质变仪冷却中。
    /// </summary>
    /// <param name="transformer">参量质变仪，未拥有时为 null。</param>
    /// <returns>冷却中时为 <see cref="Visibility.Visible"/>。</returns>
    public static Visibility GenshinTransformerCoolingDownVisibility(Transformer? transformer)
    {
        return transformer is { Obtained: true, RecoveryTime.Reached: false } ? Visibility.Visible : Visibility.Collapsed;
    }


    /// <summary>
    /// 参量质变仪剩余冷却时间的显示文案。
    /// </summary>
    /// <param name="transformer">参量质变仪，未拥有时为 null。</param>
    /// <returns>形如「还剩 6 天」的文案；已冷却完毕或数据缺失时为空字符串。</returns>
    public static string GenshinTransformerRecoveryToString(Transformer? transformer)
    {
        // 接口只在天/时/分中填一个非零值，按粒度从大到小取
        if (transformer?.RecoveryTime is not { Reached: false } time)
        {
            return string.Empty;
        }
        if (time.Day > 0)
        {
            return string.Format(Lang.TimeNode_RemainingDays, time.Day);
        }
        if (time.Hour > 0)
        {
            return string.Format(Lang.TimeNode_RemainingHours, time.Hour);
        }
        return string.Format(Lang.TimeNode_RemainingMinutes, time.Minute);
    }





}
