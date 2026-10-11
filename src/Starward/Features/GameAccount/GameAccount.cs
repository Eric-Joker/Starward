using CommunityToolkit.Mvvm.ComponentModel;
using Starward.Core;
using System;

namespace Starward.Features.GameAccount;

public class GameAccount : ObservableObject
{

    public string SHA256 { get; set; }

    public GameBiz GameBiz { get; set; }

    public long Uid { get; set => SetProperty(ref field, value); }

    public string Name { get; set => SetProperty(ref field, value); }

    public byte[] Value { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;

    public bool IsSaved { get; set => SetProperty(ref field, value); }


    /// <summary>注册表里当前这个账号，仅用于界面展示（列表里给它打个勾）。</summary>
    public bool IsCurrent { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 这条记录对应的米游社账号（账号池里的 Key）。只切米游社账号时用它，
    /// 这种记录不来自注册表，也不会写回注册表。
    /// </summary>
    public string? PassportAccountKey { get; set; }


    /// <summary>游戏内角色的头像，来自米游社工具箱里同一个角色的资料，仅用于界面展示。</summary>
    public string? HeadIcon
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(AvatarUrl));
            }
        }
    }


    /// <summary>没有角色头像时退回到通用图标。</summary>
    public string AvatarUrl => string.IsNullOrWhiteSpace(HeadIcon) ? "ms-appx:///Assets/Image/icon_hyperion.png" : HeadIcon;

}