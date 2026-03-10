using MSPChallenge_Simulation.Communication.DataModel;
using Newtonsoft.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using DotSpatial.Projections;
using System;
using System.Reflection.Metadata;
using System.Data;

namespace MSPChallenge_Simulation.Simulation;

public class BenthicSimAreaHandler
{
	public const string MSPCProjString = "EPSG:3035";
	public const int MSPCProjEPSG = 3035;
	public const float bound_xMin = 3771769.75f;
	public const float bound_yMin = 3173152.75f;
	public const float bound_xMax = 4110089.25f;
	public const float bound_yMax = 3630772.75f;
	public static int fullWidth = 2514;
	public static int fullHeight = 4609;

	public enum ExternalSimStatus { Unscheduled, AwaitingCreate, AwaitingResultsIdle, AwaitingResultsPolled, AwaitingResultsFetch, Completed, Failed }
	public enum SimDetailLevel { Fast, Medium, Fine }

	string? m_jsonGeotiff;
	string? m_simID;
	ExternalSimStatus m_status = ExternalSimStatus.Unscheduled;
	SimDetailLevel m_detail = SimDetailLevel.Fine;
	public RasterPixelRect m_rasterPixelRect;
	public string? m_message;
	public SimulationResults? m_resultsSummary;
	public SimulationRasterResults? m_resultsRaster;
	public List<int> m_pitIDs;

	public ExternalSimStatus Status => m_status;
	public string? ID => m_simID;

	public BenthicSimAreaHandler(RasterPixelRect a_rasterPixelRect, int a_pitID)
	{
		m_rasterPixelRect = a_rasterPixelRect;
		m_pitIDs = new List<int>() { a_pitID};
		if(m_detail == SimDetailLevel.Fast)
		{
			fullWidth = 292;
			fullHeight = 488;
		}
	}

	void ResetRequest()
	{
		m_status = ExternalSimStatus.Unscheduled;
		m_simID = null;
		m_jsonGeotiff = null;
		m_message = null;
		m_resultsSummary = null;
		m_resultsRaster = null;
		//DOES NOT RESET RECT!
	}

	public bool AddAreaOnOverlap(BenthicSimAreaHandler a_other)
	{
		//If area changes, it will have to be resimulated, so it is Reset.
		if (m_rasterPixelRect.Overlaps(a_other.m_rasterPixelRect))
		{
			m_rasterPixelRect.AddBounds(a_other.m_rasterPixelRect);
			m_pitIDs.AddRange(a_other.m_pitIDs);
			if (m_status != ExternalSimStatus.Unscheduled)
				ResetRequest();
			return true;
		}
		return false;
	}

	public void DetermineDeltaRaster(Image<Rgba32> a_orignalBath, Image<Rgba32> a_newBath, float[][] a_rasterBounds, double a_realPixelWidth, double a_realPixelHeight, SimulationSession a_session)
	{
		if (m_status != ExternalSimStatus.Unscheduled)
			return;

		GeoTIFF deltaRaster = new GeoTIFF()
		{
			data = new float[m_rasterPixelRect.m_xMax - m_rasterPixelRect.m_xMin, m_rasterPixelRect.m_yMax - m_rasterPixelRect.m_yMin],
			crs = MSPCProjString,
			extent = new float[] {
				(float)(a_rasterBounds[0][0] + m_rasterPixelRect.m_xMin * a_realPixelWidth),
				(float)(a_rasterBounds[0][1] + m_rasterPixelRect.m_yMin * a_realPixelHeight),
				(float)(a_rasterBounds[0][0] + m_rasterPixelRect.m_xMax * a_realPixelWidth),
				(float)(a_rasterBounds[0][1] + m_rasterPixelRect.m_yMax * a_realPixelHeight)
			}
		};
		for (int x = 0; x < m_rasterPixelRect.m_xMax - m_rasterPixelRect.m_xMin; x++)
		{
			for (int y = 0; y < m_rasterPixelRect.m_yMax - m_rasterPixelRect.m_yMin; y++)
			{
				deltaRaster.data[x, y] =
				a_session.m_bathymetryMeta.scale.PixelToValue(a_orignalBath[x + m_rasterPixelRect.m_xMin, a_orignalBath.Height - 1 - (m_rasterPixelRect.m_yMin + y)].R)
				- a_session.m_bathymetryMeta.scale.PixelToValue(a_newBath[x + m_rasterPixelRect.m_xMin, a_newBath.Height - 1 - (m_rasterPixelRect.m_yMin + y)].R);
			}
		}
		m_jsonGeotiff = JsonConvert.SerializeObject(deltaRaster);
	}

	public void PollResult(McpClient a_MCPClient)
	{
		if(m_status == ExternalSimStatus.Unscheduled)
		{
			CreateSimInternal(a_MCPClient);
			m_status = ExternalSimStatus.AwaitingCreate;
		}
		if (m_status != ExternalSimStatus.AwaitingResultsIdle)
		{
			return;
		}
		m_status = ExternalSimStatus.AwaitingResultsPolled;
		PollResultInternal(a_MCPClient);
	}

	async void CreateSimInternal(McpClient a_MCPClient)
	{
		Dictionary<string, object?> settings = new Dictionary<string, object?>()
		{
			["scenario_type"] = "dredging",
			["delta_raster"] = m_jsonGeotiff,
			["scenario_name"] = "MSPC_SE_Pit",
			["generate_plots"] = false
		};
		//Console.WriteLine(m_jsonGeotiff);

		if(m_detail == SimDetailLevel.Fast)
		{
			settings["prediction_grid"] = "model";
		}
		else if(m_detail == SimDetailLevel.Medium)
		{
			settings["prediction_grid"] = "delta";
			settings["baseline_bathymetry"] = "data/raw/depth_IHM_UTM.rds";
			settings["compute_bpi_onthefly"] = true;
		}
		else
		{
			settings["prediction_grid"] = "fine";
			settings["baseline_bathymetry"] = "data/raw/depth_IHM_UTM.rds";
			settings["compute_bpi_onthefly"] = true;
		}

		var result = await a_MCPClient.CallToolAsync(
			"create_simulation",
			settings,
			cancellationToken: CancellationToken.None);
		if (result.IsError.HasValue && result.IsError.Value)
		{
			m_status = ExternalSimStatus.Failed;
			m_message = "MCP call to create benthic sim failed for unknown reasons.";
			return;
		}
		SimulationCallResult callResult = JsonConvert.DeserializeObject<SimulationCallResult>(result.Content.OfType<TextContentBlock>().First().Text);

		if (callResult.Failed)
		{
			m_status = ExternalSimStatus.Failed;
			m_message = "MCP call to create benthic sim failed. Message: " + callResult.message;
			return;
		}

		m_status = ExternalSimStatus.AwaitingResultsIdle;
		m_simID = callResult.simulation_id;
	}

	async void PollResultInternal(McpClient a_MCPClient)
	{
		var simPollResultCall = await a_MCPClient.CallToolAsync(
			"get_simulation_status",
			new Dictionary<string, object?>()
			{
				["simulation_id"] = m_simID
			},
			cancellationToken: CancellationToken.None);

		if (simPollResultCall.IsError.HasValue && simPollResultCall.IsError.Value)
		{
			m_status = ExternalSimStatus.Failed;
			m_message = "MCP call to poll benthic sim result failed for unknown reasons.";
			return;
		}
		SimulationStatusResult callResult = JsonConvert.DeserializeObject<SimulationStatusResult>(simPollResultCall.Content.OfType<TextContentBlock>().First().Text);
		if (callResult.Failed)
		{
			Util.LogSimLevel1($"Polling simulation with ID [{m_simID}] failed. Error message: {callResult.error_message}");
			m_status = ExternalSimStatus.Failed;
			return;
		}
		else if (callResult.Completed)
		{
			if (m_status == ExternalSimStatus.Failed) //check for intermediate failure (cancellation)
				return;
			Util.LogSimLevel1($"Simulation with ID [{m_simID}] completed! Fetching results.");
			m_status = ExternalSimStatus.AwaitingResultsFetch;
			FetchResults(a_MCPClient);
		}
		else
		{
			Util.LogSimLevel2($"Progress of simulation with ID [{m_simID}]: {callResult.progress}%. Status: {callResult.status}");
			m_status = ExternalSimStatus.AwaitingResultsIdle;
		}
	}

	async void FetchResults(McpClient a_MCPClient)
	{
		//Get result summary
		var simResultSummaryCall = await a_MCPClient.CallToolAsync(
				"get_simulation_results",
				new Dictionary<string, object?>()
				{
					["simulation_id"] = m_simID
				},
				cancellationToken: CancellationToken.None);

		if (simResultSummaryCall.IsError.HasValue && simResultSummaryCall.IsError.Value)
		{
			m_status = ExternalSimStatus.Failed;
			m_message = "MCP call to fetch benthic sim result summary failed for unknown reasons.";
			return;
		}
		m_resultsSummary = JsonConvert.DeserializeObject<SimulationResults>(simResultSummaryCall.Content.OfType<TextContentBlock>().First().Text);

		//Get result raster
		var simResultRasterCall = await a_MCPClient.CallToolAsync(
				"get_simulation_raster",
				new Dictionary<string, object?>()
				{
					["simulation_id"] = m_simID,
					["layer"] = "impact"
				},
				cancellationToken: CancellationToken.None);

		if (simResultRasterCall.IsError.HasValue && simResultRasterCall.IsError.Value)
		{
			m_status = ExternalSimStatus.Failed;
			m_message = "MCP call to fetch benthic sim result raster failed for unknown reasons.";
			return;
		}
		m_resultsRaster = JsonConvert.DeserializeObject<SimulationRasterResults>(simResultRasterCall.Content.OfType<TextContentBlock>().First().Text);
		m_resultsRaster.bounds = ReprojectBounds(m_resultsRaster.bounds, m_resultsRaster.crs);
		Console.WriteLine($"Reprojected bounds: [[{m_resultsRaster.bounds[0]},{m_resultsRaster.bounds[1]}],[{m_resultsRaster.bounds[2]},{m_resultsRaster.bounds[3]}]], Width: {m_resultsRaster.width}, Height: {m_resultsRaster.height}, original CRS: {m_resultsRaster.crs}");
		m_resultsRaster.crs = null;

		float widthPerPixel = (bound_xMax - bound_xMin) / (float)fullWidth;
		float heightPerPixel = (bound_yMax - bound_yMin) / (float)fullHeight;
		m_resultsRaster.startPixelX = (int)((m_resultsRaster.bounds[0] - bound_xMin) / widthPerPixel);
		m_resultsRaster.startPixelY = fullHeight - m_resultsRaster.height - (int)((m_resultsRaster.bounds[1] - bound_yMin) / heightPerPixel);

		m_status = ExternalSimStatus.Completed;

		Util.LogSimLevel2($"Benthic sim with ID [{m_simID}] set startpizxelY to {m_resultsRaster.startPixelY}");
		Util.LogSimLevel2($"Benthic sim with ID [{m_simID}] results fetched. Pit IDs in group: {string.Join(", ", m_pitIDs)}");
		Util.LogSimLevel2($"Net change: {m_resultsSummary.summary.impact.sum_net_change_individuals}");
		Util.LogSimLevel2($"Mean percent change: {m_resultsSummary.summary.impact.mean_percent_change}");
	}

	float[] ReprojectBounds(float[] a_bounds, string a_crs)
	{
		int externalEPSG = 0;
		if(!int.TryParse(a_crs.Remove(0,5), out externalEPSG)) // Removes the "EPSG:" part of the crs
		{
			Console.WriteLine("ERROR: Failed to parse external CRS when converting raster bounds: " + a_crs);
			return new float[] {0f, 0f, 0f, 0f};
		}
		ProjectionInfo MSPCProj = ProjectionInfo.FromEpsgCode(MSPCProjEPSG);
		ProjectionInfo externalProj = ProjectionInfo.FromEpsgCode(externalEPSG);

		double[] p = new double[] { a_bounds[0], a_bounds[1], a_bounds[2], a_bounds[3] };
		double[] z = new double[] { 1d , 1d };
		Reproject.ReprojectPoints(p, z, externalProj, MSPCProj, 0, 2);

		return new float[] { (float)p[0], (float)p[1], (float)p[2], (float)p[3] };
	}
}