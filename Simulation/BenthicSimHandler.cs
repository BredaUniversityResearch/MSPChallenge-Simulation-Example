using MSPChallenge_Simulation.Communication.DataModel;
using Newtonsoft.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MSPChallenge_Simulation.Simulation;

public class BenthicSimHandler
{
	public enum ExternalSimStatus { Unscheduled, AwaitingCreate, AwaitingResultsIdle, AwaitingResultsPolled, AwaitingResultsFetch, Completed, Failed }

	string m_jsonGeotiff;
	string? m_simID;
	ExternalSimStatus m_status = ExternalSimStatus.Unscheduled;
	public int m_rasterXMin, m_rasterYMin, m_rasterXMax, m_rasterYMax;
	public string? m_message;
	public SimulationResults? m_resultsSummary;
	public SimulationRasterResults? m_resultsRaster;

	public ExternalSimStatus Status => m_status;
	public string ID => m_simID;

	public BenthicSimHandler(McpClient a_MCPClient, string a_jsonGeotiff, int a_rasterXMin, int a_rasterYMin, int a_rasterXMax, int a_rasterYMax)
	{
		m_jsonGeotiff = a_jsonGeotiff;
		m_rasterXMin = a_rasterXMin;
		m_rasterXMax = a_rasterXMax;
		m_rasterYMin = a_rasterYMin;
		m_rasterYMax = a_rasterYMax;
		m_status = ExternalSimStatus.AwaitingCreate;
		CreateSimInternal(a_MCPClient);
	}

	async void CreateSimInternal(McpClient a_MCPClient)
	{
		var result = await a_MCPClient.CallToolAsync(
			"create_simulation",
			new Dictionary<string, object?>()
			{
				["scenario_type"] = "dredging",
				["delta_raster"] = m_jsonGeotiff,
				["scenario_name"] = "MSPC_SE_Pit",
				["prediction_grid"] = "model",
				["generate_plots"] = false
			},
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

	public void PollResult(McpClient a_MCPClient)
	{
		if (m_status != ExternalSimStatus.AwaitingResultsIdle)
		{
			return;
		}
		m_status = ExternalSimStatus.AwaitingResultsPolled;
		PollResultInternal(a_MCPClient);
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
			Console.WriteLine($"Polling simulation with ID [{m_simID}] failed. Error message: {callResult.error_message}");
			m_status = ExternalSimStatus.Failed;
			return;
		}
		else if (callResult.Completed)
		{
			if (m_status == ExternalSimStatus.Failed) //check for intermediate failure (cancellation)
				return;
			Console.WriteLine($"Simulation with ID [{m_simID}] completed! Fetching results.");
			m_status = ExternalSimStatus.AwaitingResultsFetch;
			FetchResults(a_MCPClient);
		}
		else
		{
			Console.WriteLine($"Progress of simulation with ID [{m_simID}]: {callResult.progress}%. Status: {callResult.status}");
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
					["layer"] = "modified"
				},
				cancellationToken: CancellationToken.None);

		if (simResultSummaryCall.IsError.HasValue && simResultSummaryCall.IsError.Value)
		{
			m_status = ExternalSimStatus.Failed;
			m_message = "MCP call to fetch benthic sim result raster failed for unknown reasons.";
			return;
		}
		m_resultsRaster = JsonConvert.DeserializeObject<SimulationRasterResults>(simResultSummaryCall.Content.OfType<TextContentBlock>().First().Text);
		m_status = ExternalSimStatus.Completed;
		Console.WriteLine($"Simulation with ID [{m_simID}] results fetched.");
		Console.WriteLine($"Simulation result net change: {m_resultsSummary.summary.impact.sum_net_change_individuals}");
		Console.WriteLine($"Simulation result mean percent change: {m_resultsSummary.summary.impact.mean_percent_change}");
	}
}