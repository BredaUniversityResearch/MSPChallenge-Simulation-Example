using System.Diagnostics.CodeAnalysis;

namespace MSPChallenge_Simulation.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")] // needs to match json
public class LayerStateObject
{
    public string state { get; set; }
    public int time { get; set; }
}
