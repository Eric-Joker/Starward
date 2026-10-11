using Starward.Core.Passport;
using System;

namespace Starward.Features.Account;

/// <summary>
/// 数据库里保存的一条通行证账号记录。
/// <para>
/// 存储方式与游戏账号（<c>GameAccount</c>）一致：账号本身只存在数据库里，
/// 需要用到哪一个就把它写到“当前账号”的位置上。
/// </para>
/// </summary>
internal class PassportAccountRecord
{

    /// <summary>
    /// 账号的稳定标识。账号自身不含密码，用 mid（通行证 ID）或 aid（账号 ID）即可唯一区分，
    /// 两者都缺失时退化成令牌的哈希值，不会把令牌本身写进数据库。
    /// </summary>
    public string Key { get; set; } = "";


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
