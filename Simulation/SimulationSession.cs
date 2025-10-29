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

	//Server communication
	private MspClient m_mspClient;

	//Simulation specific data
	public LayerMeta m_bathymetryMeta;
	public LayerMeta m_sandDepthMeta;
	public LayerMeta m_pitsMeta;
	public LayerMeta m_shoreLineMeta;
	public float[,] m_distanceToShoreRaster; //Has the same resolution as sandDepth raster
	public double m_totalExtractedVolume = 0d;
	public double m_totalDTS = 0d;

	//Output
	public List<KPI> m_kpis;
	public string m_newBathymetryRaster;
	public string m_newSandDepthRaster;

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
		Console.WriteLine($"State of session {m_gameSessionToken} changed. Setting target month to {m_targetMonth} and state to {m_targetGameState}");
	}

	public void SetTargetMonth(int a_targetMonth)
	{
		m_targetMonth = a_targetMonth;
		Console.WriteLine($"Target month of session {m_gameSessionToken} changed to {m_targetMonth}");
	}


	public void TickSession(double a_deltaTimeSec)
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
		Console.WriteLine($"Month updated to {m_currentMonth}");
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
					Console.WriteLine("Watchdog token changed.");
					m_onSessionClose?.Invoke(this);
				}, exception =>
				{
					Console.WriteLine($"Could not retrieve watchdog token: {exception.Message}.");
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
					Console.WriteLine("Api access token refreshed.");
				}, exception =>
				{
					Console.WriteLine($"Could not refresh api access token: {exception.Message}.");
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
			if (m_kpis == null)
			{
				Console.WriteLine("No KPIs set, sending empty KPI Set request");
				await m_mspClient.HttpPost(API_SET_KPI,
					new NameValueCollection { { "kpiValues", JsonConvert.SerializeObject(new List<KPI>()) } },
					new NameValueCollection { { "x-notify-monthly-simulation-finished", "true" } });
			}
			else
			{
				Console.WriteLine($"Setting {m_kpis.Count} KPIs");
				foreach (var kpi in m_kpis)
					Console.WriteLine($"	{kpi.name}:{kpi.value} {kpi.unit}");
				await m_mspClient.HttpPost(API_SET_KPI,
					new NameValueCollection { { "kpiValues", JsonConvert.SerializeObject(m_kpis) } },
					new NameValueCollection { { "x-notify-monthly-simulation-finished", "true" } });
			}
			Console.WriteLine($"Results submitted for month: {CurrentSimMonth}");
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
	}
}