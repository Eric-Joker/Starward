using System.Text.Json;

namespace Starward.Core.Passport;

/// <summary>
/// 从一段文本里解析出的通行证信息。
/// </summary>
public class PassportCookieParseResult
{
    /// <summary>SToken（V1 或 V2）。</summary>
    public string? Stoken { get; init; }

    /// <summary>米游社 mid。</summary>
    public string? Mid { get; init; }

    /// <summary>通行证账号 ID（stuid / ltuid / account_id），0 表示没解析到。</summary>
    public long Aid { get; init; }

    /// <summary>解析到的全部键值，键名不区分大小写。</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}


/// <summary>
/// 把用户粘贴的文本解析成 SToken / mid。
/// <para>
/// 支持的写法（键名不区分大小写，分隔符可以是分号、&amp;、逗号、换行）：
/// </para>
/// <list type="bullet">
/// <item><c>stoken=v2_xxx; mid=yyy</c>（浏览器里复制出来的 Cookie 头）</item>
/// <item><c>account_id_v2=123; ltuid_v2=123; ltoken_v2=xxx; account_mid_v2=yyy; stoken=v2_xxx</c>（米游社 V2 Cookie）</item>
/// <item><c>stoken=v1_xxx; stuid=123</c>（V1 的 SToken 配 stuid）</item>
/// <item><c>{"stoken":"v2_xxx","mid":"yyy"}</c>（JSON）</item>
/// <item>直接是一段 <c>v2_</c> 开头的 SToken</item>
/// </list>
/// </summary>
public static class PassportCookieParser
{

    /// <summary>SToken 可能的键名。</summary>
    private static readonly string[] StokenKeys = ["stoken", "stoken_v2", "stokenv2", "v2_stoken"];

    /// <summary>mid 可能的键名。</summary>
    private static readonly string[] MidKeys = ["mid", "mid_v2", "account_mid_v2", "ltmid_v2", "accountmidv2", "ltmidv2"];

    /// <summary>账号 ID（stuid / ltuid / account_id）可能的键名。</summary>
    private static readonly string[] AidKeys = ["stuid", "uid", "account_id", "ltuid", "account_id_v2", "ltuid_v2", "stuid_v2", "accountid", "ltuidv2"];




    public static PassportCookieParseResult Parse(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? single = null;
        text = text?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.StartsWith('{'))
            {
                ParseJson(text, values);
            }
            if (values.Count == 0)
            {
                foreach (string part in text.Split([';', '&', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int index = part.IndexOf('=');
                    if (index > 0)
                    {
                        string key = part[..index].Trim().Trim('"');
                        string value = part[(index + 1)..].Trim().Trim('"');
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                        {
                            values[key] = value;
                        }
                    }
                    else
                    {
                        single ??= part.Trim().Trim('"');
                    }
                }
            }
        }
        string? stoken = GetFirst(values, StokenKeys);
        string? mid = GetFirst(values, MidKeys);
        long aid = ParseAid(GetFirst(values, AidKeys));
        // 只粘贴了一段裸令牌：只有 v2_ 开头的才敢确认是 SToken。
        if (string.IsNullOrWhiteSpace(stoken) && single is not null && single.StartsWith("v2_", StringComparison.OrdinalIgnoreCase))
        {
            stoken = single;
        }
        // mid 缺失时，有些 Cookie 只给了 stuid，此时不能拿 stuid 当 mid，留给调用方判断。
        return new PassportCookieParseResult
        {
            Stoken = stoken,
            Mid = mid,
            Aid = aid,
            Values = values,
        };
    }




    private static void ParseJson(string text, Dictionary<string, string> values)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind is JsonValueKind.Object)
            {
                CollectJson(document.RootElement, values, 0);
            }
        }
        catch (JsonException) { }
    }




    /// <summary>
    /// 收集 JSON 里的字符串与数字字段。嵌套的导出格式（例如 <c>{"data":{...}}</c>）也一并展开。
    /// </summary>
    private static void CollectJson(JsonElement element, Dictionary<string, string> values, int depth)
    {
        if (depth > 3)
        {
            return;
        }
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    string? value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values[property.Name] = value;
                    }
                    break;
                case JsonValueKind.Number:
                    values[property.Name] = property.Value.ToString();
                    break;
                case JsonValueKind.Object:
                    CollectJson(property.Value, values, depth + 1);
                    break;
            }
        }
    }




    private static string? GetFirst(Dictionary<string, string> values, string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }




    private static long ParseAid(string? value)
    {
        return long.TryParse(value, out long aid) ? aid : 0;
    }


}
