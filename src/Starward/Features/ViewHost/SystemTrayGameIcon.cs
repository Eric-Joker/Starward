using Starward.Core.HoYoPlay;
using Starward.Features.GameSelector;

namespace Starward.Features.ViewHost;

/// <summary>
/// 托盘菜单里的一行 = 一个已安装的区服。
/// <para>
/// 复用 0.19 的 <see cref="PinnedGameIcon"/>（GameId / 图标 / 启动预设的取法完全不变），
/// 只补上托盘需要、而主界面游戏图标不显示的区服名：旧版托盘就是按区服名列条目的，
/// 同一个游戏装了多个区服时靠这行文字区分。
/// </para>
/// </summary>
public sealed class SystemTrayGameIcon : PinnedGameIcon
{

    public SystemTrayGameIcon(LauncherConfigModel launcher, GameInfo gameInfo) : base(launcher, gameInfo) { }


    /// <summary>区服名：中国服 / 国际服 / 哔哩哔哩。</summary>
    public string ServerName
    {
        get
        {
            string name = GameId.GameBiz.ToGameServerName();
            // 崩坏 3 的日 / 韩 / 台等子区服没有单独的中文名，退回启动器名，标题不会变成空行。
            return string.IsNullOrWhiteSpace(name) ? LauncherName ?? GameName ?? "" : name;
        }
    }


}
