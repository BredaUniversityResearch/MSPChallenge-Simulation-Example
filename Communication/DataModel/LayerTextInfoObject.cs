using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MSPChallenge_Simulation.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class LayerTextInfoObject
{
	public Dictionary<ETextState, string> property_per_state;
	public string text_color;
	[JsonConverter(typeof(StringEnumConverter))]
	public ETextSize text_size;
	public float zoom_cutoff;
	public float x;
	public float y;
	public float z;
}
