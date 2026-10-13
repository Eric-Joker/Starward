using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace Starward.Features.GameLauncher;

/// <summary>
/// 启动预设：一个游戏区服可以有多个预设，每个预设保存一套启动配置
/// （名称、启动参数、自动登录、DX12、自定义背景）。
/// <para>
/// 每个区服至少有一个预设，其中第一个预设由旧版本的单一配置迁移而来，
/// 内部名固定为 <see cref="DefaultId"/>，其余预设的内部名是随机 GUID。
/// 预设的顺序即为界面上的显示顺序，可拖动排序并持久化。
/// </para>
/// </summary>
public sealed partial class GameLaunchProfile : ObservableObject
{

    /// <summary>
    /// 第一个预设的内部名。旧版本的配置（<c>start_argument_*</c> 等键）迁移到这个预设里。
    /// </summary>
    public const string DefaultId = "p1";


    /// <summary>
    /// 内部名，稳定不变，用于持久化和 URL 协议定位预设。
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
    /// 旧数据里没有这个字段，首次读取时按当时的列表顺序补 1..N 并立即写回，老用户的默认名保持不变。
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
    /// </summary>
    [JsonPropertyName("enable_auth_ticket_login")]
    public bool EnableAuthTicketLogin { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否附加 DX12 启动参数。
    /// </summary>
    [JsonPropertyName("enable_dx12")]
    public bool EnableDX12 { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 自定义背景文件名，文件位于 <c>UserDataFolder/bg</c>。
    /// </summary>
    [JsonPropertyName("custom_bg")]
    public string? CustomBg { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 当前正在使用的背景文件名（启动器底部那排指示器选中的那张，官方背景 / 版本海报 / 好感壁纸都在内），
    /// 文件位于 <c>UserDataFolder/bg</c>。
    /// <para>
    /// 以前这个值存在全局键 <c>bg_{biz}</c> 里，所以切换预设时背景不跟着换。
    /// 现在它属于启动预设：切换预设会恢复该预设保存的壁纸类型，见
    /// <see cref="AppConfig.GetBg"/> / <see cref="AppConfig.SetBg"/>。
    /// 旧数据没有这个字段时反序列化为 null，读取会回退到旧键，不会丢用户当前背景。
    /// </para>
    /// </summary>
    [JsonPropertyName("bg")]
    public string? Bg { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否启用自定义背景。
    /// </summary>
    [JsonPropertyName("enable_custom_bg")]
    public bool EnableCustomBg { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 在所属区服的预设列表中的**当前位置**（从 1 开始）。由
    /// <see cref="GameLaunchProfileStore"/> 在增删排序后刷新。
    /// <para>
    /// 只表示位置，**不参与显示**：早期版本拿它生成默认名，于是拖完排序"第 1 行"又被叫成「预设 1」，
    /// 看起来就是"实际顺序变了、名字没跟着变"。默认名现在用创建时分配的 <see cref="Number"/>。
    /// </para>
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
    /// 当前预设是否为区服的第一个预设（由旧版本的配置迁移而来）。
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
        CustomBg = source.CustomBg;
        EnableCustomBg = source.EnableCustomBg;
        Bg = source.Bg;
    }

}
