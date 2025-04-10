using System.Diagnostics.CodeAnalysis;

namespace MSPChallenge_Simulation.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")] // needs to match json
public class EntityTypeValues
{
    public string displayName { get; set; }
    public long capacity { get; set; }
    public int value { get; set; }
}