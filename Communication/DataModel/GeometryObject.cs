namespace MSPChallenge_Simulation.Communication.DataModel;

public class GeometryObject
{
	public int id { get; set; }
	public List<List<float>> geometry { get; set; }
	public List<GeometryObject> subtractive { get; set; }
	public int active { get; set; }
	public int persistent { get; set; }
	public string mspid { get; set; }
}
