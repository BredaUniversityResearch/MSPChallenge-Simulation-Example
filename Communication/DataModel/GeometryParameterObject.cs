using System.Diagnostics.CodeAnalysis;

namespace MSPChallenge_Simulation_Example.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")] // needs to match json
public class GeometryParameterObject
{
    public string meta_name { get; set; }
    public string display_name { get; set; }
    public string sprite_name { get; set; }
    public int update_visuals { get; set; }
}