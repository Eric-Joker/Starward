using Starward.Core;
using Starward.Core.Passport;
using System;

namespace Starward.Features.Account;

/// <summary>
/// 数据库里保存的一条通行证账号记录。
/// <para>
/// 存储方式与游戏账号（<c>GameAccount</c>）一致：账号本身只存在数据库里，
/// 需要用到哪一个就把它写到“当前账号”的位置上。
/// </para>
/// <para>
/// 本分支保留主分支（main）的账号池模型，同时吸收上游 0.19 的区域概念：
/// <see cref="Region"/> 区分国服（<see cref="ServerRegion.miHoYo"/>）与国际服（<see cref="ServerRegion.HoYoverse"/>）。
/// 主分支原来只支持国服通行证，所以升级上来的账号一律按miHoYo处理。
/// </para>
/// </summary>
internal class PassportAccountRecord
{

    /// <summary>
    /// 账号的稳定标识。账号自身不含密码，用 mid（通行证 ID）或 aid（账号 ID）即可唯一区分，
    /// 两者都缺失时退化成令牌的哈希值，不会把令牌本身写进数据库。
    /// </summary>
    public string Key { get; set; } = "";


    /// <summary>
    /// 服务器区域，见 <see cref="ServerRegion"/>。
    /// 数据库里存的是枚举的整数值（1 = 国服 miHoYo，0 = 国际服 HoYoverse），与上游 <c>PassportUserInfo</c> 表一致。
    /// </summary>
    public ServerRegion Region { get; set; } = ServerRegion.miHoYo;


    public long Aid { get; set; }


    public string? Mid { get; set; }


    public string? Nickname { get; set; }


    public string? AccountName { get; set; }


    public string? LoginMethod { get; set; }


    public DateTimeOffset LoginTime { get; set; }


    /// <summary>用 DPAPI 加密后的令牌集合（JSON）。</summary>
    public byte[] Token { get; set; } = [];


    public DateTimeOffset Time { get; set; }



    public static string GetKey(PassportTokenSet tokenSet)
    {
        if (!string.IsNullOrWhiteSpace(tokenSet.Mid))
        {
            return $"mid:{tokenSet.Mid}";
        }
        if (tokenSet.Aid > 0)
        {
            return $"aid:{tokenSet.Aid}";
        }
        return $"stoken:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tokenSet.Stoken ?? "")))[..16]}";
    }


}
