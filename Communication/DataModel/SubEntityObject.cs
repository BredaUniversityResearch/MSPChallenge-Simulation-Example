namespace MSPChallenge_Simulation.Communication.DataModel;

public class SubEntityObject
{
	public int id { get; set; }
	public float[][] geometry { get; set; }
	public List<GeometryObject> subtractive { get; set; }
	public int active { get; set; }
	public int persistent { get; set; }
	public int implementation_time { get; set; }
	public string mspid { get; set; }
	public int country = -1;
	public string type { get; set; }
	public Dictionary<string, string> data { get; set; }
}

public class RasterPixelRect
{
	public int m_xMin;
	public int m_xMax;
	public int m_yMin;
	public int m_yMax;

	public bool Overlaps(RasterPixelRect a_other)
	{
		return m_xMin < a_other.m_xMax && m_xMax > a_other.m_xMin &&
				m_yMin < a_other.m_yMax && m_yMax > a_other.m_yMin;
	}

	public void AddBounds(RasterPixelRect a_other)
	{
		m_xMin = Math.Min(m_xMin, a_other.m_xMin);
		m_xMax = Math.Max(m_xMax, a_other.m_xMax);
		m_yMin = Math.Min(m_yMin, a_other.m_yMin);
		m_yMax = Math.Max(m_yMax, a_other.m_yMax);
	}
}
