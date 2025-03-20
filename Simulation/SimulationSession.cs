using System.Collections.Specialized;
using System.CommandLine;
using DotNetEnv;
using Microsoft.AspNetCore.Mvc;
using MSPChallenge_Simulation.Extensions;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication;
using MSPChallenge_Simulation.Communication.DataModel;
using MSPChallenge_Simulation.Simulation;
using MSPChallenge_Simulation.Simulation.Exceptions;
using MSPChallenge_Simulation.StateMachine;
using Newtonsoft.Json;
using TaskExtensions = MSPChallenge_Simulation.Extensions.TaskExtensions;

namespace MSPChallenge_Simulation.Simulation;

public class SimulationSession
{
	const string API_GET_WATCHDOG_TOKEN = "/api/Simulation/GetWatchdogTokenForServer";
	const string API_GET_TOKEN = "/api/User/RequestToken";

	private const int DefaultMonth = -1; // setup month
	private const int PollTokenFrequencySec = 60;
	private const int RefreshApiAccessTokenFrequencySec = 900;

	private double m_refreshApiAccessTokenTimeLeftSec = RefreshApiAccessTokenFrequencySec;
	private double m_pollTokenTimeLeftSec = PollTokenFrequencySec;
	private int m_currentMonth = DefaultMonth;
	private int m_targetMonth = DefaultMonth;
	private EGameState? m_currentGameState;
	private EGameState? m_targetGameState = EGameState.Setup;

	private string m_gameSessionToken;
	private GameSessionInfo? m_gameSessionInfo = null;
	private List<SimulationDefinition>? m_simulationDefinitions = null;
	private ProgramStateMachine? m_programStateMachine;
	private MspClient? m_mspClient;

	public LayerMeta m_bathymetryMeta;
	public LayerMeta m_sandDepthMeta;
	public LayerMeta m_pitsMeta;

	public int CurrentMonth => m_currentMonth;

	Action<SimulationSession> m_onSetupStateEntered, m_onSimulationStateEntered, m_onReportStateEntered;

	public SimulationSession(string a_gameSessionToken, string a_serverId, string a_gameSessionApi, ApiToken a_apiAccessToken, ApiToken a_apiAccessRenewToken,
		Action<SimulationSession> a_onSetupStateEntered,
		Action<SimulationSession> a_onSimulationStateEntered,
		Action<SimulationSession> a_onReportStateEntered)
	{
		m_gameSessionToken = a_gameSessionToken;
		m_onSetupStateEntered = a_onSetupStateEntered;
		m_onSimulationStateEntered = a_onSimulationStateEntered;
		m_onReportStateEntered = a_onReportStateEntered;

		m_programStateMachine = new ProgramStateMachine();
		m_programStateMachine.OnAwaitingSetupStateEnteredEvent += () =>
		{
			m_simulationDefinitions = null;
		};
		m_programStateMachine.OnSetupStateEnteredEvent += OnSetupStateEntered;
		m_programStateMachine.OnSimulationStateEnteredEvent += OnSimulationStateEntered;
		m_programStateMachine.OnReportStateEnteredEvent += OnReportStateEntered;

		m_mspClient ??= new MspClient(a_serverId, a_gameSessionApi, a_apiAccessToken.token, a_apiAccessRenewToken.token);
		m_mspClient.SetDefaultErrorHandler(exception => { Console.WriteLine("Error: " + exception.Message); });
		m_mspClient.apiAccessToken = a_apiAccessToken.token;
		m_mspClient.apiRefreshToken = a_apiAccessRenewToken.token;
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
					Reset();
				}, exception =>
				{
					Console.WriteLine($"Could not retrieve watchdog token: {exception.Message}.");
					Reset();
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
					Reset();
				}
			);
		}
	}

	public void CheckRequiredSimulations(Dictionary<string, string> requiredSimulations)
	{
		// if a required simulation is set, and it is not in the list of available simulations, throw an exception
		if (m_simulationDefinitions == null)
			throw new Exception($"No available simulations configured");
		foreach (var simulationDefinition in m_simulationDefinitions)
		{
			if (!requiredSimulations.ContainsKey(simulationDefinition.Name))
				throw new Exception($"Required simulation {simulationDefinition.Name} is not available");			
			if (new Version(requiredSimulations[simulationDefinition.Name]) > new Version(simulationDefinition.Version))
				throw new Exception($"Required simulation {simulationDefinition.Name} v{requiredSimulations[simulationDefinition.Name]} is not available");
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
		m_onReportStateEntered?.Invoke(this);
	}

	public void FireStateMachineTrigger(Trigger a_trigger)
	{
		m_programStateMachine?.Fire(a_trigger);
	}
}