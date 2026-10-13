using System.Text.Json.Serialization;

namespace Starward.Core.MiyoLive;

[JsonSerializable(typeof(miHoYoApiWrapper<MiyoLiveIndexData>))]
[JsonSerializable(typeof(miHoYoApiWrapper<MiyoLiveCodeData>))]
[JsonSerializable(typeof(miHoYoApiWrapper<MiyoLiveUserInstantListData>))]
internal partial class MiyoLiveJsonContext : JsonSerializerContext
{

}
