using System.Diagnostics.CodeAnalysis;

namespace MSPChallenge_Simulation_Example.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")] // needs to match json
public class EntityTypeValues
{
    public string displayName { get; set; }
    public bool displayPolygon { get; set; }
    public string polygonColor { get; set; }
    public string polygonPatternName { get; set; }
    public bool innerGlowEnabled { get; set; }
    public int innerGlowRadius { get; set; }
    public int innerGlowIterations { get; set; }
    public float innerGlowMultiplier { get; set; }
    public float innerGlowPixelSize { get; set; }
    public bool displayLines { get; set; }
    public string lineColor { get; set; }
    public float lineWidth { get; set; }
    public string lineIcon { get; set; }
    public ELinePatternType linePatternType { get; set; }
    public bool displayPoints { get; set; }
    public string pointColor { get; set; }
    public float pointSize { get; set; }
    public string pointSpriteName { get; set; }

    public string description { get; set; }
    public long capacity { get; set; }
    public float investmentCost { get; set; }
    public int availability { get; set; }
    public int value { get; set; }
    public string media { get; set; }
    public EApprovalType approval { get; set; } //required approval

    public EntityTypeValues()
    {
        lineWidth = 1.0f;
    }
}