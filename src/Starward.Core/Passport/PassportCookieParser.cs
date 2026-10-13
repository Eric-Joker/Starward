using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>设备指纹（<c>x-rpc-device_fp</c> 请求头或 <c>DEVICEFP</c> Cookie）。</summary>
    public string? DeviceFp { get; init; }

    /// <summary>设备名（<c>x-rpc-device_name</c> 请求头）。</summary>
    public string? DeviceName { get; init; }

    /// <summary>Android 版本（<c>x-rpc-sys_version</c> 请求头）。</summary>
    public string? SystemVersion { get; init; }

    /// <summary>米游社 App 版本（<c>x-rpc-app_version</c> 请求头）。</summary>
    public string? AppVersion { get; init; }

    /// <summary>User-Agent（设备名与 Android 版本的兜底来源）。</summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// 设备标识（<c>x-rpc-device_id</c> 请求头，或 Cookie 里的 <c>_MHYUUID</c>）。
    /// 两者同时存在时取请求头那一枚：服务端只认它。
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>解析到的全部键值，键名不区分大小写。</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}


/// <summary>
/// 从一段文本里解析出的设备信息（工具箱请求头里的那几项）。
/// </summary>
public class PassportDeviceInfo
{
    /// <summary>设备指纹（<c>x-rpc-device_fp</c> 或 <c>DEVICEFP</c>）。</summary>
    public string? DeviceFp { get; set; }

    /// <summary>设备名（<c>x-rpc-device_name</c>，可从 UA 里取）。</summary>
    public string? DeviceName { get; set; }

    /// <summary>Android 版本（<c>x-rpc-sys_version</c>，可从 UA 里取）。</summary>
    public string? SystemVersion { get; set; }

    /// <summary>米游社 App 版本（<c>x-rpc-app_version</c>）。</summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// 把界面上所有输入框里的文字合起来解析一遍。
    /// <para>
    /// 任何一栏粘贴整段请求头都能把其余几栏补全；已经单独填过的项不会被解析结果覆盖。
    /// </para>
    /// </summary>
    /// <param name="texts">各个输入框里的文字，顺序无所谓。</param>
    public PassportDeviceInfo Merge(params string?[] texts)
    {
        var current = new PassportDeviceInfo
        {
            DeviceFp = DeviceFp,
            DeviceName = DeviceName,
            SystemVersion = SystemVersion,
            AppVersion = AppVersion,
        };
        if (texts is null || texts.Length == 0)
        {
            return current;
        }
        var blob = new System.Text.StringBuilder();
        foreach (string? text in texts)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                blob.AppendLine(text);
            }
        }
        if (blob.Length == 0)
        {
            return current;
        }
        return PassportCookieParser.ParseDeviceInfo(blob.ToString(), current);
    }
}


/// <summary>
/// 把用户粘贴的文本解析成 SToken / mid / 设备信息。
/// <para>
/// 支持的写法（键名不区分大小写，分隔符可以是分号、&amp;、逗号、换行）：
/// </para>
/// <list type="bullet">
/// <item><c>stoken=v2_xxx; mid=yyy</c>（浏览器里复制出来的 Cookie 头）</item>
/// <item><c>account_id_v2=123; ltuid_v2=123; ltoken_v2=xxx; account_mid_v2=yyy; stoken=v2_xxx</c>（米游社 V2 Cookie）</item>
/// <item><c>stoken=v1_xxx; stuid=123</c>（V1 的 SToken 配 stuid）</item>
/// <item><c>{"stoken":"v2_xxx","mid":"yyy"}</c>（JSON）</item>
/// <item>直接是一段 <c>v2_</c> 开头的 SToken</item>
/// <item><c>x-rpc-device_fp=38d81d0fa2274</c> 或 <c>DEVICEFP=38d81d0fa2274</c>（设备指纹）</item>
/// <item>整个请求头区块，冒号分隔的 <c>名字: 值</c> 那种（含 User-Agent、Cookie 等）</item>
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

    /// <summary>设备指纹可能的键名（请求头 <c>x-rpc-device_fp</c> 与 Cookie <c>DEVICEFP</c>）。</summary>
    private static readonly string[] DeviceFpKeys = ["x-rpc-device_fp", "x_rpc_device_fp", "x-rpc-device-fp", "device_fp", "devicefp", "deviceFp"];

    /// <summary>
    /// 设备标识的<b>请求头</b>形态键名（<c>x-rpc-device_id: &lt;guid&gt;</c>，冒号分隔）。最高优先级。
    /// </summary>
    private static readonly string[] DeviceIdHeaderKeys = ["x-rpc-device_id", "x_rpc_device_id", "x-rpc-device-id", "device_id", "deviceid"];

    /// <summary>
    /// 设备标识的<b>等号</b>形态键名（<c>x-rpc-device_id=&lt;guid&gt;</c>，整段 Cookie / 抓包文本里很常见）。
    /// 产品方实测就踩在这里：只把它放进冒号表时，输入框不会自动填充。
    /// </summary>
    private static readonly string[] DeviceIdEqualsKeys = ["x-rpc-device_id", "x_rpc_device_id", "x-rpc-device-id", "device_id", "deviceid"];

    /// <summary>设备标识的 Cookie 兜底形态键名（<c>_MHYUUID</c>）。优先级最低。</summary>
    private static readonly string[] DeviceIdCookieKeys = ["_MHYUUID", "_mhyuuid", "mhyuuid", "_mhy_uuid"];

    /// <summary>设备名可能的键名。</summary>
    private static readonly string[] DeviceNameKeys = ["x-rpc-device_name", "x_rpc_device_name", "x-rpc-device-name", "device_name", "devicename"];

    /// <summary>Android 版本可能的键名。</summary>
    private static readonly string[] SystemVersionKeys = ["x-rpc-sys_version", "x_rpc_sys_version", "x-rpc-sys-version", "sys_version", "sysversion"];

    /// <summary>米游社 App 版本可能的键名。</summary>
    private static readonly string[] AppVersionKeys = ["x-rpc-app_version", "x_rpc_app_version", "x-rpc-app-version", "app_version", "appversion"];

    /// <summary>User-Agent 可能的键名。</summary>
    private static readonly string[] UserAgentKeys = ["user-agent", "useragent"];


    /// <summary>从 UA 里取 Android 版本：<c>Android 13; Pixel 5 Build/...</c>。</summary>
    private static readonly Regex UASystemVersionRegex = new(@"Android\s+([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从 UA 里取机型：<c>Android 13; Pixel 5 Build/...</c>。</summary>
    private static readonly Regex UADeviceNameRegex = new(@"Android\s+[\d.]+\s*;\s*([^;)]+?)\s+Build", RegexOptions.IgnoreCase | RegexOptions.Compiled);




    public static PassportCookieParseResult Parse(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 同时记下"冒号分隔的请求头"与"等号分隔的 Cookie"两种形态，供 DeviceId 判断来源优先级。
        var equalsFacts = new List<KeyValuePair<string, string>>();
        var colonFacts = new List<KeyValuePair<string, string>>();
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
                // 整段请求头先按冒号拆一遍：x-rpc-device_id: <guid> 这种写法只有这里能识别。
                foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }
                    string ck = line[..colon].Trim().Trim('"');
                    string cv = line[(colon + 1)..].Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(ck) && !string.IsNullOrWhiteSpace(cv))
                    {
                        // 值里可能还带分号分隔的 Cookie，最后统一再拆一次。
                        foreach (string fragment in cv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            int eq2 = fragment.IndexOf('=');
                            if (eq2 > 0)
                            {
                                string fk = fragment[..eq2].Trim().Trim('"');
                                string fv = fragment[(eq2 + 1)..].Trim().Trim('"');
                                if (!string.IsNullOrWhiteSpace(fk) && !string.IsNullOrWhiteSpace(fv))
                                {
                                    values[fk] = fv;
                                    equalsFacts.Add(new(fk, fv));
                                }
                            }
                        }
                        colonFacts.Add(new(ck, cv));
                    }
                }
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
                            equalsFacts.Add(new(key, value));
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
            DeviceFp = GetFirst(values, DeviceFpKeys),
            DeviceName = GetFirst(values, DeviceNameKeys),
            SystemVersion = GetFirst(values, SystemVersionKeys),
            AppVersion = GetFirst(values, AppVersionKeys),
            UserAgent = GetFirst(values, UserAgentKeys),
            DeviceId = GetFirstFact(colonFacts, DeviceIdHeaderKeys)
                ?? GetFirstFact(equalsFacts, DeviceIdEqualsKeys)
                ?? GetFirstFact(equalsFacts, DeviceIdCookieKeys),
            Values = values,
        };
    }




    /// <summary>
    /// 解析设备信息。任何一栏粘贴整段请求头、UA 或者 Cookie 都能识别出其余几项。
    /// <para>
    /// 冒号分隔的请求头优先于键值对写法：整段请求头里同时有 <c>x-rpc-device_fp: xxx</c> 和
    /// <c>DEVICEFP=yyy</c> 时，要取前者——实测 <c>DEVICEFP</c> 那个 cookie 值这些接口不认。
    /// </para>
    /// <para>
    /// <paramref name="fallback"/> 传入界面输入框里已经填过的值，填过的项不会被解析结果覆盖。
    /// </para>
    /// </summary>
    /// <param name="text">用户粘贴的整段文本。</param>
    /// <param name="fallback">界面输入框里已经填过的值，作为兜底。</param>
    public static PassportDeviceInfo ParseDeviceInfo(string? text, PassportDeviceInfo? fallback = null)
    {
        var headerInfo = new PassportDeviceInfo();
        if (!string.IsNullOrWhiteSpace(text))
        {
            // 冒号分隔的请求头写法，Parse 的 Values 里拿不到，单独扫一遍。
            foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int index = line.IndexOf(':');
                if (index <= 0)
                {
                    continue;
                }
                string key = line[..index].Trim().Trim('"');
                string value = line[(index + 1)..].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                if (Contains(DeviceFpKeys, key))
                {
                    headerInfo.DeviceFp ??= value;
                }
                else if (Contains(DeviceNameKeys, key))
                {
                    headerInfo.DeviceName ??= value;
                }
                else if (Contains(SystemVersionKeys, key))
                {
                    headerInfo.SystemVersion ??= value;
                }
                else if (Contains(AppVersionKeys, key))
                {
                    headerInfo.AppVersion ??= value;
                }
                else if (Contains(UserAgentKeys, key))
                {
                    FillFromUserAgent(headerInfo, value);
                }
            }
        }
        // 键值对写法（分号、&、逗号、换行分隔）作为兜底。
        var parsed = Parse(text);
        var info = new PassportDeviceInfo
        {
            DeviceFp = fallback?.DeviceFp ?? headerInfo.DeviceFp ?? parsed.DeviceFp,
            DeviceName = fallback?.DeviceName ?? headerInfo.DeviceName ?? parsed.DeviceName,
            SystemVersion = fallback?.SystemVersion ?? headerInfo.SystemVersion ?? parsed.SystemVersion,
            AppVersion = fallback?.AppVersion ?? headerInfo.AppVersion ?? parsed.AppVersion,
        };
        if (!string.IsNullOrWhiteSpace(text))
        {
            // 一坨文本里也可能只贴了 UA 本身。
            if (!string.IsNullOrWhiteSpace(parsed.UserAgent))
            {
                FillFromUserAgent(info, parsed.UserAgent);
            }
            if (text.Contains("Android", StringComparison.OrdinalIgnoreCase) && text.Contains("Build/", StringComparison.OrdinalIgnoreCase))
            {
                FillFromUserAgent(info, text);
            }
        }
        return info;
    }




    /// <summary>从 UA 里补设备名与 Android 版本，已经有了的不覆盖。</summary>
    private static void FillFromUserAgent(PassportDeviceInfo info, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(info.SystemVersion))
        {
            var match = UASystemVersionRegex.Match(userAgent);
            if (match.Success)
            {
                info.SystemVersion = match.Groups[1].Value;
            }
        }
        if (string.IsNullOrWhiteSpace(info.DeviceName))
        {
            var match = UADeviceNameRegex.Match(userAgent);
            if (match.Success)
            {
                info.DeviceName = match.Groups[1].Value;
            }
        }
    }




    private static bool Contains(string[] keys, string key)
    {
        foreach (string item in keys)
        {
            if (string.Equals(item, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }




    /// <summary>
    /// 从"原始形态"的键值里按候选键名取第一个非空值（用于区分请求头形态与 Cookie 形态；后出现的同名键优先）。
    /// </summary>
    /// <param name="facts">按出现顺序记录的原始键值。</param>
    /// <param name="keys">候选键名。</param>
    /// <returns>命中的值；没有则返回 null。</returns>
    private static string? GetFirstFact(List<KeyValuePair<string, string>> facts, string[] keys)
    {
        string? result = null;
        foreach (var fact in facts)
        {
            if (Contains(keys, fact.Key) && !string.IsNullOrWhiteSpace(fact.Value))
            {
                result = fact.Value;
            }
        }
        return result;
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
