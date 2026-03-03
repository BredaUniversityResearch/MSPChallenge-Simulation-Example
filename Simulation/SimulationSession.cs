using System.Collections.Specialized;
using MSPChallenge_Simulation.Extensions;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication;
using MSPChallenge_Simulation.Communication.DataModel;
using MSPChallenge_Simulation.Simulation;
using MSPChallenge_Simulation.Simulation.Exceptions;
using MSPChallenge_Simulation.StateMachine;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using ModelContextProtocol.Client;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace MSPChallenge_Simulation.Simulation;

public class SimulationSession
{
	const string API_GET_WATCHDOG_TOKEN = "/api/Simulation/GetWatchdogTokenForServer";
	const string API_GET_TOKEN = "/api/User/RequestToken";
	const string API_SET_KPI = "/api/kpi/BatchPost";									//Sets kpis in "kpiValues" list
	const string API_SET_SIM_DEFINITIONS = "/api/Simulation/Upsert";                    //Sets simulation definitions used for session
	const string API_SET_RASTER = "/api/layer/UpdateRaster";							//set raster for layer with "layer_name"
	const int DefaultMonth = -1; // setup month
	const int PollTokenFrequencySec = 60;
	const int RefreshApiAccessTokenFrequencySec = 900;
	enum SimulationState { Internal, External, Aggregation };

	//Session meta
	private double m_refreshApiAccessTokenTimeLeftSec = RefreshApiAccessTokenFrequencySec;
	private double m_pollTokenTimeLeftSec = PollTokenFrequencySec;
	private string m_gameSessionToken;
	private GameSessionInfo m_gameSessionInfo;

	//Session state
	private int m_currentMonth = DefaultMonth;
	private int m_targetMonth = DefaultMonth;
	private EGameState? m_currentGameState;
	private EGameState? m_targetGameState = EGameState.Setup;
	private ProgramStateMachine? m_programStateMachine;
	private SimulationState m_simulationState;

	//Server communication
	private MspClient m_mspClient;

	//Simulation specific data
	public LayerMeta m_bathymetryMeta;
	public string m_originalBathymetryRaster;
	public LayerMeta m_sandDepthMeta;
	public LayerMeta m_pitsMeta;
	public LayerMeta m_shoreLineMeta;
	public LayerMeta m_benthicImpactMeta;
	public float[,] m_distanceToShoreRaster; //Has the same resolution as sandDepth raster
	public double m_totalExtractedVolume = 0d;
	//public double m_totalDTS = 0d;
	public List<BenthicSimAreaHandler> m_activeBenthicSims = new List<BenthicSimAreaHandler>();
	SimulationResultsAggregation m_simAggregationResult;

	//Output
	public List<KPI> m_kpis;
	public string m_newBathymetryRaster;
	public string m_newSandDepthRaster;
	public string m_newBenthicImpactRaster;

	public int CurrentMonth => m_currentMonth;
	public int CurrentSimMonth => m_currentMonth-1;
	public MspClient MSPClient => m_mspClient;
	public GameSessionInfo GameSessionInfo => m_gameSessionInfo;
	public string SessionToken => m_gameSessionToken;

	Action<SimulationSession> m_onSetupStateEntered, m_onSimulationStateEntered, m_onSessionClose;

	public SimulationSession(
		string a_gameSessionToken, 
		string a_serverId, 
		string a_gameSessionApi, 
		ApiToken a_apiAccessToken, 
		ApiToken a_apiAccessRenewToken,
		EGameState a_newGameState, 
		int a_targetMonth,
		GameSessionInfo a_gameSessionInfo,
		Dictionary<string, List<Version>> a_simulationDefinitions,
		Action<SimulationSession> a_onSetupStateEntered,
		Action<SimulationSession> a_onSimulationStateEntered,
		Action<SimulationSession> a_onSessionClose)
	{
		m_gameSessionToken = a_gameSessionToken;
		m_onSetupStateEntered = a_onSetupStateEntered;
		m_onSimulationStateEntered = a_onSimulationStateEntered;
		m_gameSessionInfo = a_gameSessionInfo;
		m_onSessionClose = a_onSessionClose; 
		m_targetMonth = a_targetMonth;
		m_targetGameState = a_newGameState;

		m_programStateMachine = new ProgramStateMachine();
		m_programStateMachine.OnSetupStateEnteredEvent += OnSetupStateEntered;
		m_programStateMachine.OnSimulationStateEnteredEvent += OnSimulationStateEntered;
		m_programStateMachine.OnReportStateEnteredEvent += OnReportStateEntered;

		m_mspClient = new MspClient(a_serverId, a_gameSessionApi, a_apiAccessToken.token, a_apiAccessRenewToken.token);
		m_mspClient.SetDefaultErrorHandler(exception => { Console.WriteLine("Error: " + exception.Message); });
		m_mspClient.apiAccessToken = a_apiAccessToken.token;
		m_mspClient.apiRefreshToken = a_apiAccessRenewToken.token;
		m_refreshApiAccessTokenTimeLeftSec = RefreshApiAccessTokenFrequencySec;

		var nameValueCollection = new NameValueCollection();
		foreach (var simulationDefinition in a_simulationDefinitions)
		{
			nameValueCollection.Add(simulationDefinition.Key, simulationDefinition.Value.ToString());
		}
		m_mspClient.HttpPost(
			API_SET_SIM_DEFINITIONS, 
			nameValueCollection,
			new NameValueCollection { { "X-Remove-Previous", "true" } } );

		//Force trigger if we are loading into a game past setup phase
		if (a_newGameState != EGameState.Setup) 
		{
			m_programStateMachine?.Fire(Trigger.SetupGame);
		}
	}

	public void UpdateState(ApiToken a_apiAccessToken, ApiToken a_apiAccessRenewToken, EGameState a_newGameState, int a_targetMonth)
	{
		m_mspClient.apiAccessToken = a_apiAccessToken.token;
		m_mspClient.apiRefreshToken = a_apiAccessRenewToken.token;
		m_refreshApiAccessTokenTimeLeftSec = RefreshApiAccessTokenFrequencySec;
		m_targetMonth = a_targetMonth;
		m_targetGameState = a_newGameState;
		Util.LogSessionLevel($"State of session {m_gameSessionToken} changed. Setting target month to {m_targetMonth} and state to {m_targetGameState}");
	}

	public void SetTargetMonth(int a_targetMonth)
	{
		m_targetMonth = a_targetMonth;
		Util.LogSessionLevel($"Target month of session {m_gameSessionToken} changed to {m_targetMonth}");
	}

	public void TickSession(double a_deltaTimeSec, McpClient a_MCPClient)
	{
		RefreshWatchDogToken(a_deltaTimeSec);
		RefreshApiAccessToken(a_deltaTimeSec);

		if (
				// If game state is Setup the state machine goes to Setup state as well
				m_currentGameState != m_targetGameState && m_targetGameState == EGameState.Setup &&
				// But if the state machine is not ready yet, postpone the state change
				m_programStateMachine?.CanFire(Trigger.SetupGame) == true
			)
		{
			m_currentMonth = DefaultMonth; // back to setup month
			m_currentGameState = m_targetGameState;
			m_programStateMachine?.Fire(Trigger.SetupGame);
			return;
		}
		// fail-safe
		if (m_targetGameState == EGameState.Setup) return; // do not proceed until next target game state

		//While in simulation state, start and maintain any external simulations
		if (m_programStateMachine?.GetCurrentState() == State.Simulation)
		{
			if (m_simulationState == SimulationState.External)
			{
				//poll external sims
				bool externalSimsDone = true;
				foreach (BenthicSimAreaHandler sim in m_activeBenthicSims)
				{
					if (sim.Status == BenthicSimAreaHandler.ExternalSimStatus.Failed)
					{
						//Sim failed. Log error and continue without compiling KPIs.
						Util.LogSimLevel1($"Simulation with ID [{sim.ID}] failed. Continuing wihout benthic sims. Message: {sim.m_message}.");
						externalSimsDone = false;
						FireStateMachineTrigger(Trigger.FinishedSimulation);
						break;
					}
					else if (sim.Status != BenthicSimAreaHandler.ExternalSimStatus.Completed)
					{
						externalSimsDone = false;
						sim.PollResult(a_MCPClient);
					}
				}
				if (externalSimsDone)
				{
					//Compile KPIs
					if (m_activeBenthicSims.Count > 0)
					{
						m_simulationState = SimulationState.Aggregation;
						AggregateResults(a_MCPClient);
					}
					else
					{
						SkipAggregation();
					}
				}
			}
		}

		// AwaitingNextMonth is the only state we allow triggers: MonthUpdated, EndGame 
		if (m_programStateMachine?.GetCurrentState() != State.AwaitingNextMonth) return;

		// on game state End the state machine goes back to AwaitingSetup state
		if (m_currentGameState != m_targetGameState && m_targetGameState == EGameState.End)
		{
			m_currentGameState = m_targetGameState;
			m_programStateMachine?.Fire(Trigger.EndGame);
			return;
		}
		// fail-safe
		if (m_targetGameState == EGameState.End) return; // nothing to do until there is a new game (target game state Setup)

		// the game is either paused or simulating, check if new months have arrived
		if (m_targetMonth <= m_currentMonth) return;

		// we shouldn't skip months, so increment the previous month until we reach the current month
		m_currentMonth++;
		Util.LogSessionLevel($"Month updated to {m_currentMonth}");
		m_programStateMachine?.Fire(Trigger.MonthUpdated);
	}

	void RefreshWatchDogToken(double a_deltaTimeSec)
	{
		m_pollTokenTimeLeftSec -= a_deltaTimeSec;
		if (m_pollTokenTimeLeftSec <= 0)
		{
			// reset the poll token time
			while (m_pollTokenTimeLeftSec < 0)
			{
				m_pollTokenTimeLeftSec += PollTokenFrequencySec;
			}

			// poll the token
			m_mspClient.HttpPost<WatchdogToken>(
				API_GET_WATCHDOG_TOKEN,
				new NameValueCollection()
			).ContinueWithOnSuccess(task =>
				{
					var tokenObj = task.Result;
					if (m_gameSessionToken == tokenObj.watchdog_token) return;
					Util.LogSessionLevel("Watchdog token changed.");
					m_onSessionClose?.Invoke(this);
				}, exception =>
				{
					Util.LogSessionLevel($"Could not retrieve watchdog token: {exception.Message}.");
					m_onSessionClose?.Invoke(this);
				}
			);
		}
	}

	private void RefreshApiAccessToken(double a_deltaTimeSec)
	{
		if (m_mspClient == null) return; // we need the MSP client to validate the api access token
		m_refreshApiAccessTokenTimeLeftSec -= a_deltaTimeSec;
		if (m_refreshApiAccessTokenTimeLeftSec <= 0)
		{
			// reset the poll token time
			while (m_refreshApiAccessTokenTimeLeftSec < 0)
			{
				m_refreshApiAccessTokenTimeLeftSec += RefreshApiAccessTokenFrequencySec;
			}
			// poll the token
			m_mspClient.HttpPost<RequestTokenResult>(
				API_GET_TOKEN, new NameValueCollection()
				{
					{ "api_refresh_token", m_mspClient.apiRefreshToken }
				}
			).ContinueWithOnSuccess(task =>
				{
					m_mspClient.apiAccessToken = task.Result.api_access_token;
					m_mspClient.apiRefreshToken = task.Result.api_refresh_token;
					Util.LogSessionLevel("Api access token refreshed.");
				}, exception =>
				{
					Util.LogSessionLevel($"Could not refresh api access token: {exception.Message}.");
					m_onSessionClose?.Invoke(this);
				}
			);
		}
	}

	private void OnSetupStateEntered()
	{
		m_onSetupStateEntered?.Invoke(this);
	}

	private void OnSimulationStateEntered()
	{
		m_simulationState = SimulationState.Internal;
		m_onSimulationStateEntered?.Invoke(this);
	}

	private void OnReportStateEntered()
	{
		SubmitResults().ContinueWithOnSuccess(_ =>
		{
			m_kpis = null;
			m_newSandDepthRaster = null;
			m_newBathymetryRaster = null;
			FireStateMachineTrigger(Trigger.FinishedReport);
		});
	}

	private Task SubmitResults()
	{
		return Task.Run(async () =>
		{
			//Rasters are set for the next month, so the original raster is maintained. Sims use data from currentmonth-1.
			if (m_newSandDepthRaster != null)
			{
				await m_mspClient.HttpPost(API_SET_RASTER,
					new NameValueCollection { { "layer_name", m_sandDepthMeta.layer_name }, { "image_data", m_newSandDepthRaster }, { "month", m_currentMonth.ToString() } });
			}
			if (m_newBathymetryRaster != null)
			{
				await m_mspClient.HttpPost(API_SET_RASTER,
					new NameValueCollection { { "layer_name", m_bathymetryMeta.layer_name }, { "image_data", m_newBathymetryRaster }, { "month", m_currentMonth.ToString() } });
			}
			//if (m_newBenthicImpactRaster != null)
			//{
			//	float[,] bounds = new float[,] { { m_activeBenthicSims[0].m_resultsRaster.bounds[0], m_activeBenthicSims[0].m_resultsRaster.bounds[1] },
			//		{ m_activeBenthicSims[0].m_resultsRaster.bounds[2], m_activeBenthicSims[0].m_resultsRaster.bounds[3] }};
			//	await m_mspClient.HttpPost(API_SET_RASTER,
			//		new NameValueCollection { { "layer_name", m_benthicImpactMeta.layer_name }, { "image_data", m_newBenthicImpactRaster }, 
			//			{ "month", m_currentMonth.ToString() }, {"raster_bounds", JsonConvert.SerializeObject(bounds)} });
			//	Console.WriteLine(m_newBenthicImpactRaster);
			//}
			if (m_kpis == null)
			{
				Util.LogSimLevel1("No KPIs set, sending empty KPI Set request");
				await m_mspClient.HttpPost(API_SET_KPI,
					new NameValueCollection { { "kpiValues", JsonConvert.SerializeObject(new List<KPI>()) } },
					new NameValueCollection { { "x-notify-monthly-simulation-finished", "true" } });
			}
			else
			{
				Util.LogSimLevel1($"Setting {m_kpis.Count} KPIs");
				foreach (var kpi in m_kpis)
					Util.LogSimLevel2($"{kpi.name}:{kpi.value} {kpi.unit}");
				await m_mspClient.HttpPost(API_SET_KPI,
					new NameValueCollection { { "kpiValues", JsonConvert.SerializeObject(m_kpis) } },
					new NameValueCollection { { "x-notify-monthly-simulation-finished", "true" } });
			}
			Util.LogSimLevel1($"Results submitted for month: {CurrentMonth}");
		});

		if (m_kpis == null || m_kpis.Count == 0) return Task.CompletedTask;
		return m_mspClient.HttpPost(
			API_SET_RASTER, 
			new NameValueCollection { { "layer_name", m_sandDepthMeta.layer_name}, { "image_data", m_newSandDepthRaster } }
		).ContinueWithOnSuccess(_ =>
		{
			return m_mspClient.HttpPost(
				API_SET_RASTER,
				new NameValueCollection { { "layer_name", m_bathymetryMeta.layer_name}, { "image_data", m_newBathymetryRaster } });
		}).ContinueWithOnSuccess(_ =>
		{
			return m_mspClient.HttpPost(
				API_SET_KPI,
				new NameValueCollection { { "kpiValues", JsonConvert.SerializeObject(m_kpis) } },
				new NameValueCollection  {  { "x-notify-monthly-simulation-finished", "true" }  });
		});
	}

	private async void AggregateResults(McpClient a_MCPClient)
	{
		Util.LogSimLevel1("Aggregating benthic simulation results.");
		string[] simIds = new string[m_activeBenthicSims.Count];
		for(int i = 0; i < m_activeBenthicSims.Count; i++)
		{
			simIds[i] = m_activeBenthicSims[i].ID;
		}
		var result = await a_MCPClient.CallToolAsync(
			"aggregate_simulations",
			new Dictionary<string, object?>()
			{
				["simulation_ids"] = simIds,
				["include_details"] = false
			},
			cancellationToken: CancellationToken.None);

		if (result.IsError.HasValue && result.IsError.Value)
		{
			Util.LogSimLevel1($"Failed to aggregate simulation results. Error message: {result.Content.OfType<TextContentBlock>().First().Text}");
			return;
		}SimulationResultsAggregation callResult = JsonConvert.DeserializeObject<SimulationResultsAggregation>(result.Content.OfType<TextContentBlock>().First().Text);
		if (callResult == null)
		{
			m_kpis.Add(new KPI()
			{
				name = "Total Net Change",
				type = "SandExtraction",
				value = m_simAggregationResult == null ? 0f : m_simAggregationResult.total_net_change_individuals,
				unit = "Individuals",
				month = CurrentMonth,
				country = -1
			});
			m_kpis.Add(new KPI()
			{
				name = "Total Mean Percent Change",
				type = "SandExtraction",
				value = m_simAggregationResult == null ? 0f : m_simAggregationResult.weighted_mean_percent_change,
				unit = "%",
				month = CurrentMonth,
				country = -1
			});
		}
		else
		{
			m_simAggregationResult = callResult;
			m_kpis.Add(new KPI()
			{
				name = "Total Net Change",
				type = "SandExtraction",
				value = m_simAggregationResult.total_net_change_individuals,
				unit = "Individuals",
				month = CurrentMonth,
				country = -1
			});
			m_kpis.Add(new KPI()
			{
				name = "Total Mean Percent Change",
				type = "SandExtraction",
				value = m_simAggregationResult.weighted_mean_percent_change,
				unit = "%",
				month = CurrentMonth,
				country = -1
			});
		}
		//CombineImpactRasters();

		Util.LogSimLevel2($"Aggregated result net indivisuals change: {callResult.total_net_change_individuals}");
		Util.LogSimLevel2($"Aggregated result mean percent change: {callResult.weighted_mean_percent_change}");

		m_simulationState = SimulationState.Internal;
		FireStateMachineTrigger(Trigger.FinishedSimulation);
	}

	public void FireStateMachineTrigger(Trigger a_trigger)
	{
		m_programStateMachine?.Fire(a_trigger);
	}

	public void SetLayerMeta(LayerMeta a_meta, int a_internalLayerID)
	{
		if(a_internalLayerID == 0)
			m_bathymetryMeta = a_meta;
		else if (a_internalLayerID == 1)
			m_sandDepthMeta = a_meta;
		else if (a_internalLayerID == 2)
			m_pitsMeta = a_meta;
		else if (a_internalLayerID == 3)
			m_shoreLineMeta = a_meta;
		else if (a_internalLayerID == 4)
			m_benthicImpactMeta = a_meta;
	}

	public void InternalSimComplete()
	{
		if(m_activeBenthicSims.Count == 0)
		{
			Util.LogSimLevel1("No active benthic sim groups to simulate.");
			SkipAggregation();
		}
		else
		{
			Util.LogSimLevel1("Current benthic sim groups:");
			int activeGroups = 0;
			foreach(var group in m_activeBenthicSims)
			{
				bool active = group.Status == BenthicSimAreaHandler.ExternalSimStatus.Unscheduled;
				if (active)
					activeGroups ++;
				Util.LogSimLevel2($"{(active ? "Fresh" : "Stale")}: {string.Join(", ", group.m_pitIDs)}");
			}
			if(activeGroups == 0)
			{
				Util.LogSimLevel1($"No fresh groups. Skipping external sims and aggregation.");
				SkipAggregation();
			}
			else
			{
				m_simulationState = SimulationState.External;
			}
		}
	}

	void CombineImpactRasters()
	{
		int index = m_activeBenthicSims[0].m_resultsRaster.data.IndexOf(',');
		string base64 = m_activeBenthicSims[0].m_resultsRaster.data.Substring(index + 1);
		using Image<Rgba32> baseRaster = Image.Load<Rgba32>(Convert.FromBase64String(base64));
		for (int i = 1; i < m_activeBenthicSims.Count; i++)
		{
			index = m_activeBenthicSims[i].m_resultsRaster.data.IndexOf(',');
			base64 = m_activeBenthicSims[i].m_resultsRaster.data.Substring(index + 1);
			using Image<Rgba32> addRaster = Image.Load<Rgba32>(Convert.FromBase64String(base64));
			baseRaster.ProcessPixelRows(addRaster, (sourceAccessor, targetAccessor) =>
			{
				for (int y = 0; y < baseRaster.Height; y++)
				{
					Span<Rgba32> sourceRow = sourceAccessor.GetRowSpan(y);
					Span<Rgba32> targetRow = targetAccessor.GetRowSpan(y);
					for (int x = 0; x < sourceRow.Length; x++)
					{
						ref Rgba32 pixel = ref sourceRow[x];
						if (pixel.R < targetRow[x].R)
						{
							pixel = new Rgba32(targetRow[x].R, targetRow[x].R, targetRow[x].R);
						}
					}
				}
			});

		}
		using MemoryStream stream2 = new(16384);
		baseRaster.Save(stream2, new PngEncoder());
		m_newBenthicImpactRaster = Convert.ToBase64String(stream2.ToArray());
	}

	void SkipAggregation()
	{
		m_kpis.Add(new KPI()
		{
			name = "Total Net Change",
			type = "SandExtraction",
			value = m_simAggregationResult == null ? 0f : m_simAggregationResult.total_net_change_individuals,
			unit = "Individuals",
			month = CurrentMonth,
			country = -1
		});
		m_kpis.Add(new KPI()
		{
			name = "Total Mean Percent Change",
			type = "SandExtraction",
			value = m_simAggregationResult == null ? 0f : m_simAggregationResult.weighted_mean_percent_change,
			unit = "%",
			month = CurrentMonth,
			country = -1
		});
		m_simulationState = SimulationState.Internal;
		FireStateMachineTrigger(Trigger.FinishedSimulation);
	}
}