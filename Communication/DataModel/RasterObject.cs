namespace MSPChallenge_Simulation.Communication.DataModel;

public class RasterObject
{
	public string url { get; set; }
	public bool request_from_server { get; set; }
	public float[][] boundingbox { get; set; }
	public string layer_raster_material = "RasterMELNew";
	public string layer_raster_pattern = "Default";
	public float layer_raster_minimum_value_cutoff = 0.05f;
	public ERasterColorInterpolationMode layer_raster_color_interpolation = ERasterColorInterpolationMode.Linear;
	public FilterMode layer_raster_filter_mode = FilterMode.Bilinear;

	public RasterObject()
	{
		request_from_server = true;
	}
}
