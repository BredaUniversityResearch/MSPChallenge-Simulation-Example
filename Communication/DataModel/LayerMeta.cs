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
		layer_depth = "0";
		layer_name = "test_layer";
		layer_geotype = "polygon";
		layer_short = "Test Layer";
		layer_media = null;
		layer_group = "";
		layer_tooltip = "";
		layer_sub = "";
		layer_icon = "";
		layer_info_properties = new LayerInfoPropertiesObject[0];
		layer_text_info = null;
		layer_type = new Dictionary<int, EntityTypeValues>();
		layer_category = "Test Category";
		layer_subcategory = "aquaculture";
		layer_active = "1";
		layer_selectable = true;
		layer_kpi_category = ELayerKPICategory.Miscellaneous;
		layer_editable = true;
		layer_toggleable = true;
		layer_active_on_start = false;
		layer_states = null;
		layer_editing_type = "";
		layer_special_entity_type = ELayerSpecialEntityType.Default;
		layer_filecreationtime = -1;
		layer_entity_value_max = null;
	}

	public int layer_id { get; set; }
	public string layer_original_id { get; set; }
	public string layer_depth { get; set; }
	public string[] layer_tags { get; set; }
	public string layer_name { get; set; }
	public string layer_geotype { get; set; }
	public string layer_short { get; set; }
	public string layer_media { get; set; }
	public string layer_group { get; set; }
	public string layer_tooltip { get; set; }
	public string layer_sub { get; set; }
	public string layer_icon { get; set; }
	public LayerInfoPropertiesObject[] layer_info_properties { get; set; } 
	public LayerTextInfoObject layer_text_info { get; set; }
	[JsonConverter(typeof(JsonConverterLayerType))]
	public Dictionary<int, EntityTypeValues> layer_type { get; set; }
	public int[] layer_dependencies { get; set; }
	public string layer_category { get; set; }
	public string layer_subcategory { get; set; }
	public ELayerKPICategory layer_kpi_category { get; set; }
	public string layer_active { get; set; }
	public bool layer_selectable { get; set; }
	public bool layer_editable { get; set; }
	public bool layer_toggleable { get; set; }
	public bool layer_active_on_start { get; set; }
	public LayerStateObject[] layer_states { get; set; }
	public GeometryParameterObject[] layer_geometry_parameters { get; set; }	
	public RasterObject layer_raster { get; set; }
	public string layer_editing_type { get; set; }
	public ELayerSpecialEntityType layer_special_entity_type { get; set; }
	public int layer_green { get; set; }
	public int layer_filecreationtime { get; set; }
	public float? layer_entity_value_max { get; set; }
}
