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
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using System;
using Microsoft.AspNetCore.Http.HttpResults;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol;

namespace MSPChallenge_Simulation;

public class SessionManager()
{
	const string API_PING = "/Watchdog/Ping";               //Incoming ping request
	const string API_CONNECT_SESSION = "/Watchdog/ConnectSession"; //Incoming call to connect with a new session
	const string API_SET_MONTH = "/Watchdog/SetMonth";      //Incoming call to set session month
	const string API_SET_STATE = "/Watchdog/UpdateState";   //Incoming call to set session month and state
	const string API_SETUP_ENTERED = "/api/Simulation/NotifyMonthSimulationFinished";   //Notify server that 'request -> setup' is complete
    const int DefaultTickRateMs = 1000;                     // 1000ms = 1 second

    private int m_tickRateMs = DefaultTickRateMs;
	private DateTime m_lastTickTime = DateTime.Now;
	private readonly string[] m_args = [];
	private Dictionary<string, List<Version>> m_simulationDefinitions;
	private McpClient m_simulationMCP;

    // Session data
    private Dictionary<string, SimulationSession> m_sessions; //Unique session tokens as keys

	// Define public events
    public event Func<GameSessionInfo, bool>? OnQuestionAcceptSessionEvent;
    public event Func<SimulationSession, Task>? OnSessionInitialiseEvent;
    public event Func<SimulationSession, McpClient, Task>? OnSimulationStateEnteredEvent;
    public event Action<double /* deltaTimeSec */, SimulationSession>? OnTickEvent;

    public SessionManager(string[] args) : this()
    {
        m_args = args;
        m_sessions = new Dictionary<string, SimulationSession>();
		m_simulationDefinitions = new Dictionary<string, List<Version>>();
		GetServerID(); //Just initialise .env.local file
		
		Util.LogAppLevel("Address: host.docker.internal");
		Util.LogAppLevel("Simulation settings:");
		Util.LogAppLevel(File.ReadAllText("SimSettings.txt"));

		TaskExtensions.RegisterExceptionHandler<FatalException>(
            (exception) => throw exception);
        TaskExtensions.RegisterExceptionHandler<TriggerResetException>(_ => { Reset(); });



		//Console.WriteLine("Executing test console command");
		//Process cmd = new Process();
		//cmd.StartInfo.FileName = "cmd.exe";
		//cmd.StartInfo.RedirectStandardInput = true;
		//cmd.StartInfo.RedirectStandardOutput = true;
		//cmd.StartInfo.CreateNoWindow = true;
		//cmd.StartInfo.UseShellExecute = false;

		//cmd.Start();

		///* execute "dir" */

		//cmd.StandardInput.WriteLine("docker image");
		//cmd.StandardInput.Flush();
		//cmd.StandardInput.Close();
		//Console.WriteLine(cmd.StandardOutput.ReadToEnd());

		InitialiseMCP();
	}

	async Task InitialiseMCP()
	{
		var clientTransport = new StdioClientTransport(new StdioClientTransportOptions
		{
			Name = "BenthosSim",
			Command = "docker run -i --rm --name BenthosSim --mount type=volume,src=data,dst=/app/data --mount type=volume,src=cache,dst=/app/data/cache henriqueguarneri/benthic-impact-assessment",
			//Command = "docker run -i --rm --name BenthosSim -v ./data:/app/data -v ./cache:/app/data/cache henriqueguarneri/benthic-impact-assessment",
			//Command = "docker run -i --rm --name BenthosSim -v ./data:/app/data henriqueguarneri/benthic-impact-assessment",
			WorkingDirectory = AppContext.BaseDirectory
		});
		Util.LogAppLevel($"Connecting as MCP client");
		
		try
		{
			m_simulationMCP = await McpClient.CreateAsync(clientTransport);
		}
		catch (Exception e)
		{
			Util.LogAppLevel("MCP Server failed to start, message: " + e.Message);
			if(m_simulationMCP != null)
				await m_simulationMCP.DisposeAsync().ConfigureAwait(false);
			throw;
		}
		Util.LogAppLevel("MCP Server connected");
		//Set logging level
		if (m_simulationMCP.ServerCapabilities.Logging is null)
		{
			Util.LogAppLevel("Server does not support logging.");
		}
		else
		{
			Util.LogAppLevel("Requesting logging level: debug");
			await m_simulationMCP.SetLoggingLevelAsync(LoggingLevel.Debug);
		}

		//Get server instructions
		if(m_simulationMCP.ServerInstructions != null)
			Util.LogAppLevel(m_simulationMCP.ServerInstructions);
		else
			Util.LogAppLevel("No server instructions specified.");

		// Print the list of tools available from the server.
		Util.LogAppLevel("Tools available on the server:");
		foreach (var tool in await m_simulationMCP.ListToolsAsync())
		{
			Util.LogAppLevel($" - {tool.Name}: {tool.Description}");
		}
		//RunTestSimulation();
	}

	public void AddSimulationDefinition(string a_name, Version a_version)
	{
		if (m_simulationDefinitions.TryGetValue(a_name, out var result))
		{
			result.Add(a_version);
		}
		else
		{
			m_simulationDefinitions.Add(a_name, new List<Version>() { a_version });
		}
	}

    private void Reset()
    {
		Util.LogAppLevel("Resetting ProgramManager, all sessions will be removed.");
        m_sessions = new Dictionary<string, SimulationSession>();
	}
    
    public void SetTickRateMs(int tickRateMs)
    {
        m_tickRateMs = tickRateMs;
    }
    
    public void Run()
    {
        var portOption = new Option<int?>("--port", "Set the port the API server is running on");
        var dotfileOption = new Option<string?>("--dotfile", "Output the DOT file to the specified path. You can view it on http://www.webgraphviz.com/.");
        var httpsRedirectionOption = new Option<bool?>("--https-redirection", "Enable or disable HTTPS redirection");

        var rootCommand = new RootCommand("MSP Challenge Simulation Example");
        rootCommand.AddOption(portOption);
        rootCommand.AddOption(dotfileOption);
        rootCommand.AddOption(httpsRedirectionOption);
        rootCommand.SetHandler((int? port, string? dotfile, bool? httpsRedirection) =>
        {
            RunInternal(port, httpsRedirection);
        }, portOption, dotfileOption, httpsRedirectionOption);
        rootCommand.InvokeAsync(m_args).Wait();
    }
	    
    private void RunInternal(int? port, bool? httpsRedirection)
    {
        var builder = WebApplication.CreateBuilder(m_args);
        
        // Load environment variables from .env file
        Env.Load();
        Env.Load(".env.local");

        // Add services to the container.
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        
        // Configure Kestrel server options
        if (port.HasValue)
        {
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ListenAnyIP(port.Value);
            });
        }        

        var app = builder.Build();
        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        if (httpsRedirection == true)
        {
            app.UseHttpsRedirection();
        }

        app.MapPost(API_PING, () => Results.Ok(new { success = "1", message = "Pong" }))
			.DisableAntiforgery()
			.WithName("Ping")
			.WithOpenApi();

		app.MapPost(API_CONNECT_SESSION, APIConnectSession)
			.DisableAntiforgery()
			.WithName("ConnectSession")
			.WithOpenApi();

		app.MapPost(API_SET_MONTH, APISetMonth)
			.DisableAntiforgery()
			.WithName("SetMonth")
			.WithOpenApi();

        app.MapPost(API_SET_STATE, APISetState)
			.DisableAntiforgery()
			.WithName("UpdateState")
			.WithOpenApi();

        // Timer/tick setup
        var timer = new Timer(Tick, null, 0, m_tickRateMs); // 1000ms = 1 second
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            timer.Dispose();
        });
        app.Run();
    }

	IResult APIConnectSession([FromBody] UpdateStateRequest a_request)
    {
		var apiAccessToken = JsonConvert.DeserializeObject<ApiToken>(a_request.api_access_token);
		var apiAccessRenewToken = JsonConvert.DeserializeObject<ApiToken>(a_request.api_access_renew_token);
		var requiredSimulations = JsonConvert.DeserializeObject<Dictionary<string, string>>(a_request.required_simulations);

		EGameState newGameState;
		try
		{
			if (apiAccessToken == null || apiAccessRenewToken == null)
				throw new Exception("Invalid JSON format for API tokens");
			if (!Enum.TryParse(a_request.game_state, true, out newGameState))
				throw new Exception("Invalid game state: " + a_request.game_state);
			if (a_request.game_session_info == null)
				throw new Exception("Missing setup game session info");
			if (!IsSessionConnectionAccepted(a_request.game_session_info))
				throw new Exception("Session is not compatible with the available simulations");
			if(m_sessions.ContainsKey(a_request.game_session_token))
				throw new Exception("A session with this game_session_token already exists");
			CheckRequiredSimulations(requiredSimulations);
		}
		catch (Exception e)
		{
			Util.LogAppLevel("Error in update state request: " + e.Message);
			return Results.BadRequest(new { success = "0", message = "Bad request: " + e.Message });
		}

		SimulationSession session = new SimulationSession(
			a_request.game_session_token, GetServerID(),
			a_request.game_session_api, apiAccessToken, apiAccessRenewToken, newGameState, a_request.month, a_request.game_session_info,
			m_simulationDefinitions, null, OnSimulationStateEntered, OnSessionClose);
		m_sessions.Add(a_request.game_session_token, session);

		(OnSessionInitialiseEvent != null ? OnSessionInitialiseEvent.Invoke(session) : Task.CompletedTask)
		.ContinueWith(task => {
			if (task.IsFaulted)
			{
				// output all aggregated exceptions
				foreach (var exception in task.Exception!.InnerExceptions)
					Util.LogAppLevel(exception.Message);
				Util.LogAppLevel($"Session Initialisation for session ({session.SessionToken}) failed. Session will be removed.");
				m_sessions.Remove(session.SessionToken);
			}
			else
				session.FireStateMachineTrigger(Trigger.FinishedSetup);
		});

		return Results.Ok(new { success = "1", message = "State updated successfully" });
	}

    IResult APISetMonth([FromBody] UpdateStateRequest a_request)
    {
		if (m_sessions.TryGetValue(a_request.game_session_token, out var session))
		{
			session.SetTargetMonth(a_request.month);
		}
		else
		{
			return Results.BadRequest(new { success = "0", message = "No active session for provided session token." });
		}
		return Results.Ok(new { success = "1", message = "Month set successfully" });
	}

	IResult APISetState([FromBody] UpdateStateRequest a_request)
	{
		var apiAccessToken = JsonConvert.DeserializeObject<ApiToken>(a_request.api_access_token);
		var apiAccessRenewToken = JsonConvert.DeserializeObject<ApiToken>(a_request.api_access_renew_token);
		var requiredSimulations = JsonConvert.DeserializeObject<Dictionary<string, string>>(a_request.required_simulations);

		EGameState newGameState;
		try
		{
			if (apiAccessToken == null || apiAccessRenewToken == null)
				throw new Exception("Invalid JSON format for API tokens");
			if (!Enum.TryParse(a_request.game_state, true, out newGameState))
				throw new Exception("Invalid game state: " + a_request.game_state);
			if (a_request.game_session_info == null)
				throw new Exception("Missing setup game session info");
		}
		catch (Exception e)
		{
			Util.LogAppLevel("Error in state update request: " + e.Message);
			return Results.BadRequest(new { success = "0", message = "Bad request: " + e.Message });
		}


		if (m_sessions.TryGetValue(a_request.game_session_token, out var session))
		{
			session.UpdateState(apiAccessToken!, apiAccessRenewToken!, newGameState, a_request.month);
		}
		else if(IsSessionConnectionAccepted(a_request.game_session_info))
		{
			//Create new session
			SimulationSession newSession = new SimulationSession(
				a_request.game_session_token, GetServerID(),
				a_request.game_session_api, apiAccessToken, apiAccessRenewToken, newGameState, a_request.month, a_request.game_session_info,
				m_simulationDefinitions, OnSetupStateEntered, OnSimulationStateEntered, OnSessionClose);
			m_sessions.Add(a_request.game_session_token, newSession);
		}
		else
		{
			return Results.BadRequest(new { success = "0", message = "No active session for provided session token. Not valid for a new session." });
		}
		return Results.Ok(new { success = "1", message = "State updated successfully" });
	}

	private bool IsSessionConnectionAccepted(GameSessionInfo a_gameSessionInfo)
	{
		if (OnQuestionAcceptSessionEvent == null) return true;
		return OnQuestionAcceptSessionEvent.GetInvocationList().Cast<Func<GameSessionInfo, bool>?>().All(
			handler => handler != null && handler(a_gameSessionInfo)
		);
	}

	public void CheckRequiredSimulations(Dictionary<string, string> a_requiredSimulations)
	{
		if (a_requiredSimulations == null)
			return;
		if (m_simulationDefinitions == null)
			throw new Exception("No available simulations configured");

		foreach (var required in a_requiredSimulations)
		{
			if (m_simulationDefinitions.TryGetValue(required.Key, out var versions))
			{
				bool versionFound = false;
				Version requiredVersion = new Version(required.Value);
				foreach (Version version in versions)
				{
					if (requiredVersion <= version)
					{
						versionFound = true;
						break;
					}
				}
				if (!versionFound)
					throw new Exception($"Required version of simulation {required.Key} (v{required.Value}) is not available");
			}
			else
				throw new Exception($"Required simulation {required.Key} is not available");
		}
	}
    
    private void Tick(object? state)
    {
        var currentTickTime = DateTime.Now;
        var deltaTime = currentTickTime - m_lastTickTime;
        m_lastTickTime = currentTickTime;

        if (m_sessions == null)
            return;
        foreach(var kvp in m_sessions)
        {
            OnTickEvent?.Invoke(deltaTime.TotalSeconds, kvp.Value);
            kvp.Value.TickSession(deltaTime.TotalSeconds, m_simulationMCP);
		}
	}

	private string GetServerID()
    {
		var serverId = Environment.GetEnvironmentVariable("SERVER_ID", EnvironmentVariableTarget.User);
		if (string.IsNullOrEmpty(serverId))
		{
			Util.LogAppLevel("SERVER_ID environment variable is not set. Generating a new one.");
			// Generate a new UUID, save it back to the .env file
			serverId = Guid.NewGuid().ToString();
			Environment.SetEnvironmentVariable("SERVER_ID", serverId, EnvironmentVariableTarget.User);
			File.AppendAllText(".env.local", $"SERVER_ID={serverId}{Environment.NewLine}");
		}
		Util.LogAppLevel($"Server ID: {serverId}");
		return serverId;
	}

	private void OnSetupStateEntered(SimulationSession a_session)
	{
		(OnSessionInitialiseEvent != null ? OnSessionInitialiseEvent.Invoke(a_session) : Task.CompletedTask)
        .ContinueWith(async task => {
			await task;
			if (task.IsFaulted)
			{
				// output all aggregated exceptions
				foreach (var exception in task.Exception!.InnerExceptions)
					Util.LogAppLevel(exception.Message);
				Util.LogAppLevel($"Session Initialisation for session ({a_session.SessionToken}) failed. Session will be removed.");
				m_sessions.Remove(a_session.SessionToken);
			}
			// Notify that setup has been entered, does not need to be awaited
			a_session.MSPClient.HttpPost(
				API_SETUP_ENTERED,
				new NameValueCollection
				{
					{ "simulation_name", "SandExtraction" },
					{ "month", "-1" }
				}
			);
			a_session.FireStateMachineTrigger(Trigger.FinishedSetup);
		});
	}

	private void OnSimulationStateEntered(SimulationSession a_session)
	{
		// eg. do simulation calculations
		OnSimulationStateEnteredEvent?.Invoke(a_session, m_simulationMCP);
		//	.ContinueWith(_ => {
		//	a_session.FireStateMachineTrigger(Trigger.FinishedSimulation);
		//});
	}

	private void OnSessionClose(SimulationSession a_session)
    {
        m_sessions.Remove(a_session.SessionToken);
    }

	private async void RunTestSimulation()
	{
		//Dictionary<string, object> testDeltaRaster = new Dictionary<string, object>()
		//{
		//	["data"] = new int[,] {
		//			{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
		//			{ 0, 0, -1, -2, -2, -2, -2, -1, 0, 0},
		//			{ 0, 0, -2, -4, -4, -4, -4, -2, 0, 0},
		//			{ 0, 0, -2, -4, -4, -4, -4, -2, 0, 0},
		//			{ 0, 0, -1, -2, -2, -2, -2, -1, 0, 0},
		//			{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }
		//		},
		//	["extent"] = new int[] { 555000, 5905000, 556000, 5906000 },
		//	["crs"] = "EPSG:32631",
		//	["nodata"] = 0
		//};

		Dictionary<string, object> testDeltaRaster = new Dictionary<string, object>()
		{
			["data"] = new double[,] {
					{ 0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0 },
					{ 0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0},
					{ 0.0,0.0,0.0,-3.1583462,-3.4012966,0.0,0.0,0.0,0.0},
					{ 0.0,0.0,-1.9435997,-10.689787,-10.689787,-1.4576988,0.0,0.0,0.0},
					{ 0.0,0.0,-0.24295044,-8.98914,-11.904535,-8.26029,0.0,0.0,0.0},
					{ 0.0,0.0,0.0,-2.4294968,-8.503241,-9.9609375,-0.24295044,0.0,0.0 },
					{ 0.0,0.0,0.0,0.0,0.0,-2.1865463,0.0,0.0,0.0 },
					{ 0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0 },
					{ 0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0 }
				},
			["extent"] = new double[] { 3939316.0, 3294528.8, 3943767.8, 3298979.8 },
			["crs"] = "EPSG:3035",
			["nodata"] = 0
		};

		Util.LogAppLevel("Creating test simulation");
		// Execute a tool (this would normally be driven by LLM tool invocations).
		var result = await m_simulationMCP.CallToolAsync(
			"create_simulation",
			new Dictionary<string, object?>()
			{
				["scenario_type"] = "dredging",
				["delta_raster"] = JsonConvert.SerializeObject(testDeltaRaster),
				["scenario_name"] = "TestSimulation",
				//["prediction_grid"] = "model",
				["prediction_grid"] = "fine",
				["baseline_bathymetry"] = "data/raw/depth_IHM_UTM.rds",
				["compute_bpi_onthefly"] = true,
				["generate_plots"] = false
			},
			cancellationToken: CancellationToken.None);

		if (result.IsError.HasValue && result.IsError.Value)
		{
			Util.LogAppLevel($"Creating test simulation failed. Error message: {result.Content.OfType<TextContentBlock>().First().Text}");
			return;
		}

		SimulationCallResult callResult = JsonConvert.DeserializeObject<SimulationCallResult>(result.Content.OfType<TextContentBlock>().First().Text);
		string simulationID = callResult.simulation_id;
		Util.LogAppLevel("Test simulation started, ID is: " + callResult.simulation_id);

		//Poll simulation results
		while (true)
		{
			var simPollResultCall = await m_simulationMCP.CallToolAsync(
				"get_simulation_status",
				new Dictionary<string, object?>()
				{
					["simulation_id"] = simulationID
				},
				cancellationToken: CancellationToken.None);
			if (simPollResultCall.IsError.HasValue && simPollResultCall.IsError.Value)
			{
				Util.LogSessionLevel($"Test simulation with ID [{simulationID}] failed. Error message: {simPollResultCall.Content.OfType<TextContentBlock>().First().Text}");
				return;
			}
			SimulationStatusResult pollCallResult = JsonConvert.DeserializeObject<SimulationStatusResult>(simPollResultCall.Content.OfType<TextContentBlock>().First().Text);
			if (pollCallResult.Failed)
			{
				Util.LogSessionLevel($"Test simulation with ID [{simulationID}] failed. Error message: {pollCallResult.error_message}");
				return;
			}
			else if (pollCallResult.Completed)
			{
				if (simulationID == null)
					return;
				Util.LogSessionLevel($"Test simulation with ID [{simulationID}] completed! Fetching results.");
				var simResultCall = await m_simulationMCP.CallToolAsync(
					"get_simulation_results",
					new Dictionary<string, object?>()
					{
						["simulation_id"] = simulationID
					},
					cancellationToken: CancellationToken.None);
				Util.LogSessionLevel("Test simulation results received.");
				if(simResultCall.IsError.HasValue && simResultCall.IsError.Value)
				{
					Util.LogSessionLevel($"Getting test simulation results failed. Message: {simResultCall.Content.OfType<TextContentBlock>().First().Text}");
					return;
				}
				SimulationResults simResult = JsonConvert.DeserializeObject<SimulationResults>(simResultCall.Content.OfType<TextContentBlock>().First().Text);
				Util.LogSessionLevel($"Test simulation result net change: {simResult.summary.impact.sum_net_change_individuals}");
				Util.LogSessionLevel($"Test simulation result mean percent change: {simResult.summary.impact.mean_percent_change}");
				Util.LogSessionLevel("Test simulation successfull!");
				return;
			}
			Util.LogSessionLevel($"Progress of test simulation with ID [{simulationID}]: {pollCallResult.progress}%. Status: {pollCallResult.status}");
			await Task.Delay(1000);
		}
	}
}
