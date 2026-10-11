namespace Starward.Features.Account;

/// <summary>
/// 通行证账号状态（登录 / 注销 / 令牌刷新）发生变化。
/// </summary>
public class PassportAccountChangedMessage
{

    public PassportAccountChangedMessage(bool isLoggedIn)
    {
        IsLoggedIn = isLoggedIn;
    }


    public bool IsLoggedIn { get; }

}
