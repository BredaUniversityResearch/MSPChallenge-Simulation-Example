namespace MSPChallenge_Simulation.Communication.DataModel
{
	public class SimulationCallResult
	{
		public string simulation_id;
		public string status;

		//optional
		public string message;
		public bool Failed => status == "failed";
	}

	public class SimulationStatusResult
	{
		public string simulation_id;
		public string status; //"pending", "running", "complete", "failed"
		public int progress; //0-100

		//optional
		public string current_step;
		public string created_at; //date
		public string started_at; //date
		public string completed_at; //date
		public float duration_seconds;
		public string error_message;

		public bool Failed => status == "failed";
		public bool Completed => status == "complete";
	}
	public class SimulationResults
	{
		public string simulation_id;
		public SimulationResultsSummary summary; //object
		//public string output_files; //object

		//optional
		//public string metadata; //object
	}

	public class SimulationRasterResults
	{
		public string simulation_id;
		public string layer; //Which layer to return: baseline (pre-disturbance), modified (post-disturbance), or impact (change)
		public string crs; //Coordinate reference system (EPSG:32631
		public float[] bounds; //[xmin, ymin, xmax, ymax]
		public float resolution; //pixel size in m
		public int width; //raster pixel width
		public int height; //raster pixel height
		public string data; //base64 encoded GeoTIFF (data:image/tiff;base64,...)
	}

	public class SimulationResultsSummary
	{
		public int total_points;
		public int affected_points;
		//public string metadata;
		//public string baseline_stats;
		//public string modified_stats;
		public SimulationResultsSummaryImpact impact;
		//public string bathymetry_changes;
		public SimulationResultsSummaryDeltaRaster delta_raster;
	}

	public class SimulationResultsSummaryImpact
	{
		public float sum_losses_sample_units;
		public float sum_losses_individuals;
		public float sum_gains_sample_units;
		public float sum_gains_individuals;
		public float sum_net_change_sample_units;
		public float sum_net_change_individuals;
		public float mean_change_per_point_sample_units;
		public float mean_change_per_point_individuals;
		public float mean_percent_change;
	}

	public class SimulationResultsSummaryDeltaRaster
	{
		public float mean_depth_change;
		public float max_depth_change;
		public int affected_cells;
	}
}
