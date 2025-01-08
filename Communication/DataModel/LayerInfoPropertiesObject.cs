using System.Diagnostics.CodeAnalysis;

namespace MSPChallenge_Simulation_Example.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")] // needs to match json
public class LayerInfoPropertiesObject
{
    public enum ContentValidation { None, ShippingWidth, NumberCables, PitExtractionDepth }

    public string property_name { get; set; }
    public bool enabled { get; set; }
    public bool editable { get; set; }
    public string display_name { get; set; }
    public string sprite_name { get; set; }
    public string default_value { get; set; }
    public string policy_type { get; set; }
    public bool update_visuals { get; set; }
    public bool update_text { get; set; }
    public bool update_calculation { get; set; }
    public EInputFieldContentType content_type { get; set; }
    public ContentValidation content_validation { get; set; }
    public string unit { get; set; }
}