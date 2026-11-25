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
using Starward.Features.Account;
using Starward.Features.GameRecord.BH3;
using Starward.Features.GameRecord.DailyNote.Notify;
using Starward.Features.GameRecord.Genshin;
using Starward.Features.GameRecord.StarRail;
using Starward.Features.GameRecord.ZZZ;
using System;
using System.Threading.Tasks;


namespace Starward.Features.GameRecord.DailyNote;

[INotifyPropertyChanged]
public sealed partial class DailyNoteButton : UserControl
{

    private readonly ILogger<DailyNoteButton> _logger = AppConfig.GetLogger<DailyNoteButton>();


    private readonly GameRecordBH3Service _gameRecordBH3Service = AppConfig.GetService<GameRecordBH3Service>();

    private readonly GameRecordGenshinService _gameRecordGenshinService = AppConfig.GetService<GameRecordGenshinService>();

    private readonly GameRecordStarRailService _gameRecordStarRailService = AppConfig.GetService<GameRecordStarRailService>();

    private readonly GameRecordZZZService _gameRecordZZZService = AppConfig.GetService<GameRecordZZZService>();

    private readonly GameRecordRoleService _gameRecordRoleService = AppConfig.GetService<GameRecordRoleService>();


    private readonly DailyNoteNotifyService _dailyNoteNotifyService = AppConfig.GetService<DailyNoteNotifyService>();


    private readonly PassportAccountService _passportAccountService = AppConfig.GetService<PassportAccountService>();


    public DailyNoteButton()
    {
        this.InitializeComponent();
        this.Visibility = Visibility.Collapsed;
        // 登录 / 新增角色后 GameRecordRoleChangedMessage 会到达，此时必须重新解析角色并重算可见性：
        // 本控件只在 Loaded 时刷新一次，而登录完成可能发生在它已经加载之后（那时它还是 Collapsed）。
        WeakReferenceMessenger.Default.Register<GameRecordRoleChangedMessage>(this, (_, _) => RefreshForRoleChanged());
        // 账号选择器切号后跟上当前账号在这个游戏的角色；令牌刷新等其它账号消息不会引发请求。
        WeakReferenceMessenger.Default.Register<PassportAccountChangedMessage>(this, (_, _) => RefreshForRoleChanged());
    }



    /// <summary>
    /// 角色或当前账号变了：重新解析这个游戏应该用的角色，真的换了 UID 才刷新。
    /// 切号过程中会收到多条消息（账号变化 + 角色变化），这里按 UID 去重，不会连着发几次请求。
    /// </summary>
    private void RefreshForRoleChanged()
    {
        try
        {
            if (!IsLoaded || CurrentGameId is null)
            {
                return;
            }
            if (!GameFeatureConfig.FromGameId(CurrentGameId).SupportDailyNote)
            {
                return;
            }
            // 消息可能从后台线程发出来，解析角色、动界面都回到 UI 线程做。
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!IsLoaded || CurrentGameId is null)
                    {
                        return;
                    }
                    GameRecordRole? role = _passportAccountService.GetAccountCenterGameRole(CurrentGameId.GameBiz);
                    if (role?.Uid == GameRecordRole?.Uid)
                    {
                        return;
                    }
                    if (role is null)
                    {
                        // 当前账号在这个游戏没有角色：按钮收起，别继续显示上一个账号的数据。
                        ClearDailyNote();
                        return;
                    }
                    RefreshDailyNoteCommand.Execute(true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Refresh daily note from role change (Biz: {GameBiz})", CurrentGameId?.GameBiz);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handle daily note role change");
        }
    }



    /// <summary>收起按钮并清掉上一个账号留下的数据。</summary>
    private void ClearDailyNote()
    {
        GameRecordRole = null;
        BH3DailyNote = null!;
        GenshinDailyNote = null!;
        StarRailDailyNote = null!;
        ZZZDailyNote = null!;
        ErrorMessage = null;
        this.Visibility = Visibility.Collapsed;
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
        // 取消还没回来的请求：切页之后旧数据不能再盖回来
        _refreshVersion++;
        GameRecordRole = null;
        BH3DailyNote = null!;
        GenshinDailyNote = null!;
        StarRailDailyNote = null!;
        ZZZDailyNote = null!;
    }



    /// <summary>最近一次刷新请求的版本号：切号时旧请求晚回来，不能把新账号的数据盖掉。</summary>
    private int _refreshVersion;



    [RelayCommand]
    private async Task RefreshDailyNoteAsync(bool? forceUpdate)
    {
        int version = ++_refreshVersion;
        try
        {
            if (!forceUpdate.HasValue)
            {
                forceUpdate = true;
            }
            // 角色统一按账号解析：国服跟着账号中心选中的账号走，国际服 / B 服沿用最近选择的角色。
            GameRecordRole? role = _passportAccountService.GetAccountCenterGameRole(CurrentGameId.GameBiz);
            GameRecordRole = role;
            if (role is null)
            {
                // 当前账号在这个游戏里没有角色：入口收起。
                ClearDailyNote();
                return;
            }
            this.Visibility = Visibility.Visible;
            ErrorMessage = null;
            if (IsBH3Enabled)
            {
                var note = await GetDailyNoteWithRetryAsync(role, () => _gameRecordBH3Service.GetBH3DailyNoteAsync(role, forceUpdate.Value));
                if (version != _refreshVersion)
                {
                    return;
                }
                BH3DailyNote = note;
            }
            if (IsGenshinEnabled)
            {
                var note = await GetDailyNoteWithRetryAsync(role, () => _gameRecordGenshinService.GetGenshinDailyNoteAsync(role, forceUpdate.Value));
                if (version != _refreshVersion)
                {
                    return;
                }
                GenshinDailyNote = note;
            }
            if (IsStarRailEnabled)
            {
                var note = await GetDailyNoteWithRetryAsync(role, () => _gameRecordStarRailService.GetStarRailDailyNoteAsync(role, forceUpdate.Value));
                if (version != _refreshVersion)
                {
                    return;
                }
                StarRailDailyNote = note;
            }
            if (IsZZZEnabled)
            {
                var note = await GetDailyNoteWithRetryAsync(role, () => _gameRecordZZZService.GetZZZDailyNoteAsync(role, forceUpdate.Value));
                if (version != _refreshVersion)
                {
                    return;
                }
                ZZZDailyNote = note;
            }
            // 交给便笺提醒判定，免得后台为同一份数据再请求一次
            _dailyNoteNotifyService.ReportSnapshot(role, GetCurrentDailyNote());
        }
        catch (Exception ex)
        {
            if (version == _refreshVersion && ex is miHoYoApiException)
            {
                ErrorMessage = $"Error Code: {ex.Message}";
            }
            _logger.LogError(ex, "Refresh daily note failed (Biz: {GameBiz}, Server: {GameServer}, Uid: {Uid})", CurrentGameId?.GameBiz, GameRecordRole?.Region, GameRecordRole?.Uid);
        }
    }





    /// <summary>
    /// 打开「提醒设置」对话框，默认选中首页便笺正在显示的账号。先收起浮层，对话框与浮层不叠在一起。
    /// </summary>
    [RelayCommand]
    private async Task OpenNotifySettingsAsync()
    {
        XamlRoot xamlRoot = this.XamlRoot;
        Button_DailyNote.Flyout?.Hide();
        try
        {
            await new DailyNoteNotifyDialog { InitialRole = GameRecordRole, XamlRoot = xamlRoot }.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open daily note notify dialog failed.");
        }
    }


    /// <summary>
    /// 当前游戏已加载的便笺。
    /// </summary>
    /// <returns>便笺对象；未加载时为 null。</returns>
    private object? GetCurrentDailyNote()
    {
        if (IsGenshinEnabled)
        {
            return GenshinDailyNote;
        }
        if (IsStarRailEnabled)
        {
            return StarRailDailyNote;
        }
        if (IsZZZEnabled)
        {
            return ZZZDailyNote;
        }
        if (IsBH3Enabled)
        {
            return BH3DailyNote;
        }
        return null;
    }




    /// <summary>
    /// 获取每日便笺，登录失效时通过关联的通行证账号刷新 Cookie 后重试
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="role"></param>
    /// <param name="getDailyNote"></param>
    /// <returns></returns>
    private async Task<T> GetDailyNoteWithRetryAsync<T>(GameRecordRole role, Func<Task<T>> getDailyNote)
    {
        try
        {
            return await getDailyNote();
        }
        catch (miHoYoApiException ex) when (ex.ReturnCode is -100 or 10001)
        {
            await _gameRecordRoleService.UpdateGameRoleAsync(role);
            return await getDailyNote();
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
