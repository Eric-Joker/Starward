using Starward.Core.HoYoPlay;
using System;

namespace Starward.Features.ViewHost;

/// <summary>
/// 请求主视图导航到某个页面。
/// <para>
/// <see cref="Parameter"/> 是页面自己的导航参数（例如设置页的深链锚点），
/// 为 null 时沿用原来的行为：把 <see cref="GameId"/>（或当前游戏）传给页面。
/// </para>
/// </summary>
internal record class MainViewNavigateMessage(Type Page, GameId? GameId = null, object? Parameter = null);
