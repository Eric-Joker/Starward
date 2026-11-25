using Starward.Core;
using Starward.Core.HoYoPlay;
using System;


namespace Starward.Features.PlayTime;

/// <summary>
/// 游戏时长统计对话框里切换游戏用的图标模型：游戏图标、名称与区服名。
/// 只在统计对话框内使用，避免与其它模块的游戏选择器图标模型互相耦合。
/// </summary>
public sealed class PlayTimeGameIcon : IEquatable<PlayTimeGameIcon>
{

    public GameId GameId { get; }

    public GameBiz GameBiz { get; }

    public string GameIcon { get; }

    public string GameName { get; }

    public string ServerName { get; }


    public PlayTimeGameIcon(GameBiz gameBiz)
    {
        GameBiz = gameBiz;
        GameId = GameId.FromGameBiz(gameBiz)!;
        GameIcon = GameBizToIcon(gameBiz);
        GameName = gameBiz.ToGameName();
        ServerName = gameBiz.ToGameServerName();
    }


    private static string GameBizToIcon(GameBiz gameBiz)
    {
        return gameBiz.Game switch
        {
            GameBiz.bh3 => "ms-appx:///Assets/Image/icon_bh3.jpg",
            GameBiz.hk4e => "ms-appx:///Assets/Image/icon_ys.jpg",
            GameBiz.hkrpg => "ms-appx:///Assets/Image/icon_sr.jpg",
            GameBiz.nap => "ms-appx:///Assets/Image/icon_zzz.jpg",
            _ => "ms-appx:///Assets/Image/Transparent.png",
        };
    }


    public bool Equals(PlayTimeGameIcon? other)
    {
        return ReferenceEquals(this, other) || GameBiz == other?.GameBiz;
    }


    public override bool Equals(object? obj) => Equals(obj as PlayTimeGameIcon);


    public override int GetHashCode() => GameBiz.GetHashCode();

}
