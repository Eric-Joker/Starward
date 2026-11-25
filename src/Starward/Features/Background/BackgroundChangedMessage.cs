using Starward.Core.HoYoPlay;

namespace Starward.Features.Background;

/// <summary>
/// 背景已变化的消息：通知 <see cref="AppBackground"/> 重新读取当前背景设置。
/// <para>从主分支迁移；0.19 的 <see cref="AppBackground"/> 以无参方式重读背景，因此载荷只作保留。</para>
/// </summary>
internal class BackgroundChangedMessage
{

    public GameBackground? GameBackground { get; set; }


    public BackgroundChangedMessage(GameBackground? gameBackground = null)
    {
        GameBackground = gameBackground;
    }

}
