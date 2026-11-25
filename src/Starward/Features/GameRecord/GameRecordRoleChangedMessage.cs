using Starward.Core.GameRecord;

namespace Starward.Features.GameRecord;

/// <summary>
/// 游戏角色（工具箱里的角色行）发生变化。
/// <para>
/// 通行证账号登录、刷新令牌、切换账号之后都会重写角色行与 Cookie，
/// 订阅方收到消息后重新读取角色、重算入口可见性。账号中心迁移阶段先提供消息类型，
/// 现有的 MainView / 便笺等入口在界面阶段接入订阅。
/// </para>
/// </summary>
internal class GameRecordRoleChangedMessage
{

    public GameRecordRole? GameRole { get; set; }


    public GameRecordRoleChangedMessage(GameRecordRole? gameRole)
    {
        GameRole = gameRole;
    }

}
