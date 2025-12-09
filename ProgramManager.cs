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

namespace MSPChallenge_Simulation;

public class ProgramManager()
{
	const string API_PING = "/Watchdog/Ping";               //Incoming ping request
	const string API_CONNECT_SESSION = "/Watchdog/ConnectSession"; //Incoming call to connect with a new session
	const string API_SET_MONTH = "/Watchdog/SetMonth";      //Incoming call to set session month
	const string API_SET_STATE = "/Watchdog/UpdateState";   //Incoming call to set session month and state
    const int DefaultTickRateMs = 1000;                     // 1000ms = 1 second

    private int m_tickRateMs = DefaultTickRateMs;
	private DateTime m_lastTickTime = DateTime.Now;
	private readonly string[] m_args = [];
	private Dictionary<string, List<Version>> m_simulationDefinitions;

    // Session data
    private Dictionary<string, SimulationSession> m_sessions; //Unique session tokens as keys

	// Define public events
    public event Func<GameSessionInfo, bool>? OnQuestionAcceptSessionEvent;
    public event Func<SimulationSession, Task>? OnSessionInitialiseEvent;
    public event Func<SimulationSession, Task>? OnSimulationStateEnteredEvent;
    public event Action<double /* deltaTimeSec */, SimulationSession>? OnTickEvent;

    public ProgramManager(string[] args) : this()
    {
        m_args = args;
        m_sessions = new Dictionary<string, SimulationSession>();
		m_simulationDefinitions = new Dictionary<string, List<Version>>();
		GetServerID(); //Just initialise .env.local file

		TaskExtensions.RegisterExceptionHandler<FatalException>(
            (exception) => throw exception);
        TaskExtensions.RegisterExceptionHandler<TriggerResetException>(_ => { Reset(); });
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
        Console.WriteLine("Resetting ProgramManager, all sessions will be removed.");
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
            //HandleDotFile(dotfile);
            RunInternal(port, httpsRedirection);
        }, portOption, dotfileOption, httpsRedirectionOption);
        rootCommand.InvokeAsync(m_args).Wait();
    }

    //private void HandleDotFile(string? dotfile)
    //{
    //    // if dotfile is set, generate a DOT file of the state machine
    //    if (string.IsNullOrEmpty(dotfile)) return;
    //    m_programStateMachine?.WriteToDotFile(dotfile);
    //}
    
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
			Console.WriteLine(e.Message);
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
					Console.WriteLine(exception.Message);
				Console.WriteLine($"Session Initialisation for session ({session.SessionToken}) failed. Session will be removed.");
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
			Console.WriteLine(e.Message);
			return Results.BadRequest(new { success = "0", message = "Bad request: " + e.Message });
		}


		if (m_sessions.TryGetValue(a_request.game_session_token, out var session))
		{
			session.UpdateState(apiAccessToken!, apiAccessRenewToken!, newGameState, a_request.month);
		}
		else if(IsSessionConnectionAccepted(a_request.game_session_info))
		{
			//try
			//{
			//	CheckRequiredSimulations(requiredSimulations);
			//}
			//catch (Exception e)
			//{
			//	Console.WriteLine(e.Message);
			//	return Results.BadRequest(new { success = "0", message = "Bad request: " + e.Message });
			//}

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

	IResult APISetStateOld([FromBody] UpdateStateRequest a_request)
	{
		var apiAccessToken = JsonConvert.DeserializeObject<ApiToken>(a_request.api_access_token);
		var apiAccessRenewToken = JsonConvert.DeserializeObject<ApiToken>(a_request.api_access_renew_token);
		var requiredSimulations = JsonConvert.DeserializeObject<Dictionary<string, string>>(a_request.required_simulations);

		EGameState newGameState;
		try
		{
			ValidateRequestDataOld(apiAccessToken, apiAccessRenewToken, a_request, out newGameState);
		}
		catch (Exception e)
		{
			Console.WriteLine(e.Message);
			return Results.BadRequest(new { success = "0", message = "Bad request: " + e.Message });
		}

		if (m_sessions.TryGetValue(a_request.game_session_token, out var session))
		{
			session.UpdateState(apiAccessToken!, apiAccessRenewToken!, newGameState, a_request.month);
		}
		else
		{
			return Results.BadRequest(new { success = "0", message = "No active session for provided session token." });
		}
		return Results.Ok(new { success = "1", message = "State updated successfully" });
	}

	private void ValidateRequestDataOld(ApiToken? apiAccessToken, ApiToken? apiAccessRenewToken, UpdateStateRequest request,  out EGameState newGameState)
    {
        if (apiAccessToken == null || apiAccessRenewToken == null)
        {
            throw new Exception("Invalid JSON format for API tokens");
        }
        if (!Enum.TryParse(request.game_state, true, out newGameState))
        {
            throw new Exception("Invalid game state: " + request.game_state);
        }

        //if (newGameState != EGameState.Setup) return;
        
        if (request.game_session_info == null)
        {
            throw new Exception("Missing setup game session info");
        }
        if(IsSessionConnectionAccepted(request.game_session_info) && !m_sessions.ContainsKey(request.game_session_token))
        {
            // yes, we accepted this setup, add a new session object
            m_sessions.Add(request.game_session_token, new SimulationSession(
			request.game_session_token, GetServerID(), 
                request.game_session_api, apiAccessToken, apiAccessRenewToken, newGameState, request.month, request.game_session_info, 
				m_simulationDefinitions, OnSetupStateEntered, OnSimulationStateEntered, OnSessionClose));
		}
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
            kvp.Value.TickSession(deltaTime.TotalSeconds);
		}
	}

    private string GetServerID()
    {
		var serverId = Environment.GetEnvironmentVariable("SERVER_ID");
		if (string.IsNullOrEmpty(serverId))
		{
			Console.WriteLine("SERVER_ID environment variable is not set. Generating a new one.");
			// Generate a new UUID, save it back to the .env file
			serverId = Guid.NewGuid().ToString();
			Environment.SetEnvironmentVariable("SERVER_ID", serverId);
			File.AppendAllText(".env.local", $"SERVER_ID={serverId}{Environment.NewLine}");
		}
		Console.WriteLine($"Server ID: {serverId}");
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
					Console.WriteLine(exception.Message);
				Console.WriteLine($"Session Initialisation for session ({a_session.SessionToken}) failed. Session will be removed.");
				m_sessions.Remove(a_session.SessionToken);
			}
			a_session.FireStateMachineTrigger(Trigger.FinishedSetup);
		});
	}

	private void OnSimulationStateEntered(SimulationSession a_session)
	{
		// eg. do simulation calculations
		OnSimulationStateEnteredEvent?.Invoke(a_session).ContinueWith(_ => {
			a_session.FireStateMachineTrigger(Trigger.FinishedSimulation);
		});
	}

	private void OnSessionClose(SimulationSession a_session)
    {
        m_sessions.Remove(a_session.SessionToken);
    }
}
