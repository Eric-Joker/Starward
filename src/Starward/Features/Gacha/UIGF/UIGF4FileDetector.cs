using System;
using System.IO;
using System.Text.Json;

namespace Starward.Features.Gacha.UIGF;

/// <summary>
/// 判断一个 JSON 文件是不是 UIGF v4.x 结构。
/// <para>
/// "从旧版格式导入"走的是 UIGF v3.0 / SRGF v1.0 模型（<c>info</c> + <c>list</c>），
/// 直接喂 v4 文件会反序列化出 null 然后抛 NullReferenceException，
/// 所以先识别结构，再引导用户走 UIGF v4.x 的导入入口。
/// </para>
/// </summary>
internal static class UIGF4FileDetector
{

    /// <summary>
    /// UIGF v4.x 的每游戏档案字段，出现在根对象里。
    /// </summary>
    private static readonly string[] GameArchiveProperties = ["hk4e", "hkrpg", "nap", "hk4e_ugc"];


    /// <summary>
    /// 文件是 UIGF v4.x 结构时返回 true。读不动或不是 JSON 时返回 false，交由原有逻辑处理。
    /// </summary>
    public static bool IsUIGF4File(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(fs);
            JsonElement root = doc.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }
            if (!root.TryGetProperty("info", out JsonElement info) || info.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }
            // info.version 是 v4 的版本字段；v3 用的是 uigf_version，SRGF 用的是 srgf_version。
            if (!info.TryGetProperty("version", out _))
            {
                return false;
            }
            foreach (string property in GameArchiveProperties)
            {
                if (root.TryGetProperty(property, out _))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }


}
