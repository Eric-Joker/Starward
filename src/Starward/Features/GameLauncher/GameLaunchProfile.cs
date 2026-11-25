using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace Starward.Features.GameLauncher;

/// <summary>
/// 启动预设：一个游戏区服可以有多个预设，每个预设保存一套启动配置
/// （名称、启动参数、自动登录、DX12、自定义启动程序、自定义背景、是否显示游戏公告）。
/// <para>
/// 每个区服至少有一个预设，其中第一个预设由 0.19 上游的单一配置迁移而来，
/// 内部名固定为 <see cref="DefaultId"/>，其余预设的内部名是随机 GUID。
/// 预设的顺序即为界面上的显示顺序，可拖动排序并持久化。
/// </para>
/// </summary>
public sealed partial class GameLaunchProfile : ObservableObject
{

    /// <summary>
    /// 第一个预设的内部名。0.19 上游的配置（<c>StartArgument</c> 等按 GameId 存的键）迁移到这个预设里。
    /// </summary>
    public const string DefaultId = "p1";


    /// <summary>
    /// 内部名，稳定不变，用于持久化。
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";


    /// <summary>
    /// 用户自定义的显示名称。为空时用创建时分配的默认名「预设 N」（见 <see cref="Number"/>）。
    /// <para>
    /// 只有用户改名（<see cref="GameLaunchProfileStore.RenameProfile"/>）会动它；
    /// 拖动排序不改任何预设的名字——名字属于预设本身，不跟位置走。
    /// </para>
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }


    /// <summary>
    /// 默认名用的序号（从 1 开始），**创建/首次读取时分配一次**，之后只用来拼「预设 N」。
    /// <para>
    /// 拖动排序、增删都不会改它（<see cref="GameLaunchProfileStore"/> 只在缺失或重复时补一个空闲号），
    /// 所以两行互换之后，第 1 行显示的就是原来第 2 行那条预设的名字。
    /// </para>
    /// </summary>
    [JsonPropertyName("number")]
    public int Number
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }


    /// <summary>
    /// 命令行启动参数。允许为空，表示不附加任何启动参数。
    /// </summary>
    [JsonPropertyName("argument")]
    public string? Argument { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否在启动时自动登录（用通行证令牌申请授权票据，仅国服游戏可用）。
    /// <para>
    /// 0.19 上游的全局开关 <see cref="AppConfig.AuthLoginGameByPassportAccount"/> 仍作为总闸，
    /// 这里记录的是「这个预设要不要自动登录」，给后续通行证模块接入预设级账号时使用。
    /// </para>
    /// </summary>
    [JsonPropertyName("enable_auth_ticket_login")]
    public bool EnableAuthTicketLogin { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否附加 DX12 启动参数。null 表示跟随游戏本身的默认值（0.19 上游的三态语义）。
    /// </summary>
    [JsonPropertyName("enable_dx12")]
    public bool? EnableDX12 { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否显示这个游戏的公告（启动器上的公告栏和游戏公告入口）。
    /// <para>
    /// 属于启动预设。旧数据、以及还没改过这一项的新预设读出 null，
    /// 读取时回退到全局键 <see cref="AppConfig.EnableBannerAndPost"/>（默认开启），
    /// 所以老用户升级后公告不会莫名其妙消失。
    /// </para>
    /// </summary>
    [JsonPropertyName("enable_banner_and_post")]
    public bool? EnableBannerAndPost { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否启用自定义启动程序：游戏本体不存在时改用它启动，见
    /// <see cref="GameLauncherService.StartGameAsync"/>。
    /// </summary>
    [JsonPropertyName("enable_third_party_tool")]
    public bool EnableThirdPartyTool { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 自定义启动程序的路径。可移动存储设备里存的是相对路径（和上游旧键一致），
    /// 读取时由 <see cref="GameLauncherService.GetThirdPartyToolPath"/> 还原成绝对路径。
    /// </summary>
    [JsonPropertyName("third_party_tool_path")]
    public string? ThirdPartyToolPath { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 用这个预设启动时使用的米游社账号（账号列表里那条记录的内部 key）。
    /// 为空表示跟随账号选择器里当前选中的账号。
    /// <para>
    /// 0.19 上游的账号体系（<c>PassportUserInfo</c> 按区域 + aid 存）与主分支的账号池不同，
    /// 本分支只保留这个数据字段与界面位置，**运行时换号逻辑由通行证模块后续接入**：
    /// 在 <see cref="GameLauncherService.StartGameAsync"/> 申请授权票据之前读取这里并切号。
    /// </para>
    /// </summary>
    [JsonPropertyName("account_key")]
    public string? AccountKey { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 自定义背景文件名，文件位于 <c>UserDataFolder/bg</c>。
    /// </summary>
    [JsonPropertyName("custom_bg")]
    public string? CustomBg { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 当前正在使用的背景文件名（启动器底部那排指示器选中的那张），文件位于 <c>UserDataFolder/bg</c>。
    /// <para>
    /// 0.19 上游把这个值存在按 GameId 的 <c>Bg</c> 键里。现在它属于启动预设：切换预设会恢复该预设保存的背景。
    /// 旧数据没有这个字段时反序列化为 null，读取会回退到旧键，不会丢用户当前背景。
    /// </para>
    /// </summary>
    [JsonPropertyName("bg")]
    public string? Bg { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否启用自定义背景。0.19 上游没有独立开关（有文件即视为启用），这里保留字段给界面用。
    /// </summary>
    [JsonPropertyName("enable_custom_bg")]
    public bool EnableCustomBg { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 在所属区服的预设列表中的**当前位置**（从 1 开始）。由
    /// <see cref="GameLaunchProfileStore"/> 在增删排序后刷新。只表示位置，**不参与显示**。
    /// </summary>
    [JsonIgnore]
    public int Index { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 界面上显示的预设名称：有自定义名用自定义名，否则用创建时分配的默认名「预设 N」。
    /// 只跟 <see cref="Name"/> / <see cref="Number"/> 走，**与位置（<see cref="Index"/>）无关**。
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name.Trim();
            }
            return Number > 0
                ? string.Format(Lang.GameLauncherSettingDialog_ProfileNameFormat, Number)
                : Lang.GameLauncherSettingDialog_DefaultProfileName;
        }
    }


    /// <summary>
    /// 当前预设是否为区服的第一个预设（由 0.19 上游的配置迁移而来）。
    /// </summary>
    [JsonIgnore]
    public bool IsDefault => Id == DefaultId;


    /// <summary>
    /// 是否为当前选中的预设。仅用于界面上的整行高亮，不持久化。
    /// </summary>
    [JsonIgnore]
    public bool IsActive { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 界面上是否允许删除这一项：一个区服至少要留一个预设。由编辑窗口按当前数量同步，不持久化。
    /// </summary>
    [JsonIgnore]
    public bool CanDelete { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 复制一份预设的全部配置，用于「新增预设时继承当前预设的配置」。
    /// <para>
    /// 只复制配置和用户自定义名；<see cref="Number"/> / <see cref="Index"/> 不复制，
    /// 新预设由 <see cref="GameLaunchProfileStore"/> 分配自己的默认名序号。
    /// </para>
    /// </summary>
    public void CopyFrom(GameLaunchProfile source)
    {
        Name = source.Name;
        Argument = source.Argument;
        EnableAuthTicketLogin = source.EnableAuthTicketLogin;
        EnableDX12 = source.EnableDX12;
        EnableBannerAndPost = source.EnableBannerAndPost;
        EnableThirdPartyTool = source.EnableThirdPartyTool;
        ThirdPartyToolPath = source.ThirdPartyToolPath;
        AccountKey = source.AccountKey;
        CustomBg = source.CustomBg;
        EnableCustomBg = source.EnableCustomBg;
        Bg = source.Bg;
    }

}
