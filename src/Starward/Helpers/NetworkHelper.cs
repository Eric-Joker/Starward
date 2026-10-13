using System;
using Windows.Networking.Connectivity;

namespace Starward.Helpers;

/// <summary>
/// 网络状态判断。
/// <para>
/// 主要是为了不在「按流量计费」的网络上偷偷跑后台请求：更新检查、自动刷新这类
/// 用户没有主动触发的网络访问应当先看这里。用户主动点击发起的请求不受限制。
/// </para>
/// </summary>
internal static class NetworkHelper
{

    /// <summary>上次读取到的计量状态与读取时间，短时间内重复询问时复用。</summary>
    private static readonly object _lock = new();

    private static DateTimeOffset _lastCheckTime = DateTimeOffset.MinValue;

    private static bool _lastMetered;


    /// <summary>结果缓存时长。连接状态变化不像会在一秒内来回跳，没必要每次都问系统。</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);


    /// <summary>
    /// 当前网络是否被用户或运营商声明为按流量计费。
    /// <para>
    /// 判定依据是 Windows 的连接成本：<see cref="NetworkCostType.Fixed"/> / <see cref="NetworkCostType.Variable"/>
    ///（用户在 Wi-Fi 属性里打开「按流量计费」就是这两种），以及漫游。
    /// <see cref="NetworkCostType.Unknown"/> 不算计量——台式机有线网络常常拿不到成本信息，
    /// 把它当计量会让这些用户再也收不到更新提示。
    /// </para>
    /// </summary>
    /// <returns>按流量计费（或漫游）时返回 true；无法判断时返回 false。</returns>
    public static bool IsMeteredConnection()
    {
        lock (_lock)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (now - _lastCheckTime < CacheDuration)
            {
                return _lastMetered;
            }
            _lastMetered = ReadMetered();
            _lastCheckTime = now;
            return _lastMetered;
        }
    }


    /// <summary>当前是否有可用的网络连接。</summary>
    /// <returns>已连接到 Internet 时返回 true。</returns>
    public static bool IsInternetAvailable()
    {
        try
        {
            return NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel()
                is NetworkConnectivityLevel.InternetAccess;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>清除缓存，下一次询问时重新向系统取值。</summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            _lastCheckTime = DateTimeOffset.MinValue;
        }
    }


    private static bool ReadMetered()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return false;
            }
            ConnectionCost cost = profile.GetConnectionCost();
            if (cost.Roaming)
            {
                return true;
            }
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
        }
        catch
        {
            return false;
        }
    }

}
