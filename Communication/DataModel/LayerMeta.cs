using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;

namespace MSPChallenge_Simulation.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")] // needs to match json
public class LayerMeta
{
	public LayerMeta()
	{
		layer_id = 0;
		layer_original_id = null;
		layer_name = "test_layer";
		layer_geotype = "polygon";
		layer_short = "Test Layer";
		layer_group = "";
		layer_type = new List<EntityTypeValues>();
		layer_category = "Test Category";
		layer_subcategory = "aquaculture";
		layer_active = "1";
		layer_entity_value_max = null;
	}

	public int layer_id { get; set; }
	public string layer_original_id { get; set; }
	public string[] layer_tags { get; set; }
	public string layer_name { get; set; }
	public string layer_geotype { get; set; }
	public string layer_short { get; set; }
	public string layer_group { get; set; }
	public List<EntityTypeValues> layer_type { get; set; }
	public string layer_category { get; set; }
	public string layer_subcategory { get; set; }
	public string layer_active { get; set; }
	public string layer_raster { get; set; }
	public float? layer_entity_value_max { get; set; }
	public ScaleConfig scale { get; set; }
}