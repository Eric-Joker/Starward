using System;

namespace Starward.Features.ViewHost;

/// <summary>
/// 主窗口所在会话的显示器状态变化消息
/// </summary>
internal class MainWindowDisplayStatusChangedMessage
{

    /// <summary>会话的显示器关闭（调暗不算）。</summary>
    public bool DisplayOff { get; set; }


    /// <summary>会话的显示器从关闭恢复为开启或调暗。</summary>
    public bool DisplayOn { get; set; }


    public DateTimeOffset CurrentTime { get; set; }


}
