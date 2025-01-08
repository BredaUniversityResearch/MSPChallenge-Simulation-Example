namespace MSPChallenge_Simulation_Example.Communication.DataModel;

public class SubEntityObject
{
	public int id { get; set; }
	public List<List<float>> geometry { get; set; }
	public List<GeometryObject> subtractive { get; set; }
	public int active { get; set; }
	public int persistent { get; set; }
	public string mspid { get; set; }
	public int country = -1;
	public string type { get; set; }
	public Dictionary<string, string> data { get; set; }
}
