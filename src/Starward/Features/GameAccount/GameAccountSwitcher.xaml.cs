using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.Account;
using Starward.Features.GameRecord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace Starward.Features.GameAccount;

/// <summary>
/// 启动页的游戏账号切换器，按两个完全分开的机制工作：
/// <list type="bullet">
/// <item>基于注册表（设置里的开关打开时，国际服只能这样）：切换游戏客户端自己记住的那个账号，
/// 写的是注册表里那份数据，国际服的登录本来也只能靠它。</item>
/// <item>只切米游社账号（开关关闭时的国服）：列表是账号池里的米游社账号，
/// 选中只换通行证会话，登录数据通过启动参数传给游戏，一个字都不写注册表。</item>
/// </list>
/// 两种模式都显示游戏内的昵称、UID 和头像，**选择即切换**，没有单独的切换按钮。
/// </summary>
[INotifyPropertyChanged]
public sealed partial class GameAccountSwitcher : UserControl
{

    private readonly ILogger<GameAccountService> _logger = AppConfig.GetLogger<GameAccountService>();


    private readonly GameAccountService _gameAccountService = AppConfig.GetService<GameAccountService>();


    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();


    private readonly PassportAccountService _passportAccountService = AppConfig.GetService<PassportAccountService>();


    public GameAccountSwitcher()
    {
        this.InitializeComponent();
        this.Loaded += GameAccountSwitcher_Loaded;
        this.Unloaded += GameAccountSwitcher_Unloaded;
    }




    private void GameAccountSwitcher_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGameAccount();
        // 账号池在别处变了（账号中心增删账号、切米游社账号），这边跟着刷新。
        WeakReferenceMessenger.Default.Register<PassportAccountChangedMessage>(this, (_, _) => Refresh());
        // 工具箱里的角色资料变了，能列出这个游戏的账号也会跟着变（可能从无到有，也可能反过来）。
        WeakReferenceMessenger.Default.Register<GameRecordRoleChangedMessage>(this, (_, _) => Refresh());
        WeakReferenceMessenger.Default.Register<GameRecordToolboxRefreshedMessage>(this, (_, _) => Refresh());
    }




    private void GameAccountSwitcher_Unloaded(object sender, RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        SelectGameAccount = null;
        GameAccountList = null!;
    }




    /// <summary>消息可能从后台线程发出来，统一回到 UI 线程刷新。</summary>
    private void Refresh()
    {
        DispatcherQueue.TryEnqueue(UpdateGameAccount);
    }




    public GameBiz CurrentGameBiz { get; set; }




    /// <summary>
    /// 是不是「基于注册表」那套：国际服和 b 服只能用这个（它们没有米游社账号），
    /// 国服还要看设置里的开关。关掉时国服就只切米游社账号，连注册表都不读。
    /// </summary>
    private bool UseRegistry => !CurrentGameBiz.IsChinaServer() || AppConfig.EnableRegistryGameAccountSwitcher;




    public List<GameAccount> GameAccountList { get; set => SetProperty(ref field, value); }




    /// <summary>当前这个游戏用的账号，显示在按钮上。</summary>
    public GameAccount? SelectGameAccount { get; set => SetProperty(ref field, value); }




    public void UpdateGameAccount()
    {
        try
        {
            List<GameAccount> list;
            if (UseRegistry)
            {
                list = _gameAccountService.GetGameAccounts(CurrentGameBiz);
                for (int i = 0; i < list.Count; i++)
                {
                    // 服务会把注册表里当前那个账号排到最前面，它就是现在用的账号。
                    list[i].IsCurrent = i == 0;
                    FillRoleInfo(list[i]);
                }
            }
            else
            {
                // 只切米游社账号：资料已经在服务里按这个游戏的角色填好了。
                list = _passportAccountService.GetPassportGameAccounts(CurrentGameBiz);
            }
            GameAccountList = list;
            SelectGameAccount = list.FirstOrDefault(x => x.IsCurrent) ?? list.FirstOrDefault();
            // 没有可切换的账号时不要留一个空按钮：
            // 基于注册表时列表里有记录就说明游戏客户端记着账号；
            // 只切米游社账号时，登录的账号一个都没拿到这个游戏的角色（没有游戏内 UID），
            // 说明它们在这个游戏里都没有账号，同样隐藏。
            bool hasGameAccount = UseRegistry ? list.Count > 0 : list.Exists(x => x.Uid > 0);
            this.Visibility = hasGameAccount ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot get game account ({biz})", CurrentGameBiz);
        }
    }




    /// <summary>
    /// 用米游社工具箱里的角色资料补上这个游戏账号的昵称和头像：
    /// 通行证 ID 和游戏内 UID 是两套编号，uid 相同就是同一个游戏账号。
    /// <para>
    /// 绝区零不会把 uid 写进注册表，那里读出来是 0，只能按当前通行证账号在这个游戏里的角色显示。
    /// </para>
    /// </summary>
    private void FillRoleInfo(GameAccount account)
    {
        try
        {
            GameRecordRole? role;
            if (account.Uid > 0)
            {
                role = _gameRecordService.GetGameRoles(CurrentGameBiz).FirstOrDefault(x => x.Uid == account.Uid);
            }
            else
            {
                role = _passportAccountService.GetCurrentGameRole(CurrentGameBiz);
                if (role is not null)
                {
                    account.Uid = role.Uid;
                }
            }
            if (role is null)
            {
                return;
            }
            account.HeadIcon = role.HeadIcon;
            // 这里显示的是游戏内的昵称和 UID，注册表里那份记录也跟着用同一个名字。
            if (!string.IsNullOrWhiteSpace(role.Nickname))
            {
                account.Name = role.Nickname;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fill game account role info ({biz}, {uid})", CurrentGameBiz, account.Uid);
        }
    }




    /// <summary>
    /// 选择即切换。只切米游社账号时换的是通行证会话，启动游戏时通过启动参数生效；
    /// 基于注册表时先把当前账号存下来，再把它写回注册表让游戏客户端记住。
    /// </summary>
    private async void ListView_GameAccount_ItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            Flyout_GameAccount.Hide();
            if (e.ClickedItem is not GameAccount account)
            {
                return;
            }
            if (account.PassportAccountKey is not null)
            {
                // 只切米游社账号：登录数据走启动参数，注册表一个字节都不动。
                await _passportAccountService.UsePassportAccountAsync(CurrentGameBiz, account.PassportAccountKey);
                UpdateGameAccount();
                return;
            }
            // 注册表里那份数据是会被覆盖的，切换前先把它存下来，否则就找不回来了。
            if (GameAccountList?.FirstOrDefault(x => !x.IsSaved) is GameAccount current)
            {
                _gameAccountService.SaveCurrentGameAccount(CurrentGameBiz, current.Name);
            }
            _gameAccountService.ChangeGameAccount(account);
            UpdateGameAccount();
            // 界面上的选择和自动登录用的通行证要对得上，所以会话也跟着走。
            await _passportAccountService.UsePassportAccountOfGameUidAsync(CurrentGameBiz, account.Uid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot change game {biz} account", CurrentGameBiz);
        }
    }




}
