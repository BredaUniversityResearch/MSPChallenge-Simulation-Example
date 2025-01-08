using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MSPChallenge_Simulation_Example.Communication.DataModel;
class JsonConverterLayerType : JsonConverter
{
	public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
	{
		throw new NotImplementedException();
	}

	public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
	{
		var token = JToken.Load(reader);
		switch (token.Type)
		{
			case JTokenType.Array:
			{
				var layerTypeList = token.ToObject<List<EntityTypeValues>>(serializer);
				var result = new Dictionary<int, EntityTypeValues>();
				if (layerTypeList == null) return result;
				for (var i = 0; i < layerTypeList.Count; i++)
				{
					result.Add(i, layerTypeList[i]);
				}
				return result;
			}
			case JTokenType.Object:
				return token.ToObject<Dictionary<int, EntityTypeValues>>(serializer) ?? throw new InvalidOperationException();
			default:
				throw new JsonSerializationException("Unexpected JSON format encountered in LayerTypeConverter: " + token.ToString());
		}
	}

	public override bool CanWrite => false;

	public override bool CanConvert(Type objectType)
	{
		return objectType == typeof(Dictionary<int, EntityTypeValues>) || objectType == typeof(List<EntityTypeValues>);
	}
}
