namespace MSPChallenge_Simulation.Simulation;

public class SimulationDefinition(string name, string version)
{
    public string Name { get; set; } = name;
    public string Version { get; set; } = version;
}