using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starward.Core.Passport;

/// <summary>
/// 宽容的整数转换器。
/// <para>
/// 通行证接口的数值字段并不稳定：同一个字段在不同状态下可能是数字、数字字符串，
/// 甚至是空字符串（例如扫码未确认时的 <c>user_info.aid</c>）。
/// 如果直接反序列化为 <see cref="long"/>，一个空字符串就会让整个响应解析失败，
/// 因此这里把无法解析的内容统一当作 0。
/// </para>
/// </summary>
public sealed class LenientInt64Converter : JsonConverter<long>
{

    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long number) ? number : (long)reader.GetDouble();
            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long text) ? text : 0;
            case JsonTokenType.True:
                return 1;
            default:
                return 0;
        }
    }


    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }

}


/// <summary>
/// 宽容的 32 位整数转换器，处理方式与 <see cref="LenientInt64Converter"/> 相同。
/// </summary>
public sealed class LenientInt32Converter : JsonConverter<int>
{

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out int number) ? number : (int)reader.GetDouble();
            case JsonTokenType.String:
                return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int text) ? text : 0;
            case JsonTokenType.True:
                return 1;
            default:
                return 0;
        }
    }


    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }

}


/// <summary>
/// 宽容的字符串转换器：数字、布尔值也会转换成字符串，null 或空内容返回 null。
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string?>
{

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                string? text = reader.GetString();
                return string.IsNullOrEmpty(text) ? null : text;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long number) ? number.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonTokenType.True:
                return "1";
            case JsonTokenType.False:
                return "0";
            default:
                return null;
        }
    }


    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

}
