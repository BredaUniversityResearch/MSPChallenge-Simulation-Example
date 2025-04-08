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

namespace MSPChallenge_Simulation;

public class ProgramManager()
{
	const string API_PING = "/Watchdog/Ping";               //Incoming ping request
	const string API_SET_MONTH = "/Watchdog/SetMonth";      //Incoming call to set session month
	const string API_SET_STATE = "/Watchdog/UpdateState";   //Incoming call to set session month and state
    const int DefaultTickRateMs = 1000;                     // 1000ms = 1 second

    private int m_tickRateMs = DefaultTickRateMs;
	private DateTime m_lastTickTime = DateTime.Now;
	private readonly string[] m_args = [];

    // Session data
    private Dictionary<string, SimulationSession> m_sessions; //Unique session tokens as keys

	// Define public events
	public event Func<GameSessionInfo, List<SimulationDefinition>>? GetSimulationDefinitions;
    public event Func<GameSessionInfo, bool>? OnQuestionAcceptSetupEvent;
    public event Func<SimulationSession, Task>? OnSetupEvent;
    public event Func<SimulationSession, Task>? OnSimulationStateEnteredEvent;
    public event Action<double /* deltaTimeSec */, SimulationSession>? OnTickEvent;

    public ProgramManager(string[] args) : this()
    {
        m_args = args;
        m_sessions = new Dictionary<string, SimulationSession>();

		TaskExtensions.RegisterExceptionHandler<FatalException>(
            (exception) => throw exception);
        TaskExtensions.RegisterExceptionHandler<TriggerResetException>(_ => { Reset(); });
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

        app.MapPost(API_SET_MONTH, ([FromBody] SetMonthRequest request) =>
        {
            try {
                ValidateRequestAllowed(request.game_session_token);
            } catch (Exception e) {
                return Results.Json(
                    new { success = "0", message = e.Message },
                    statusCode: StatusCodes.Status405MethodNotAllowed
                );    
            }
			if (m_sessions.TryGetValue(request.game_session_token, out var session))
			{
				session.SetTargetMonth(request.month);
			}
			else
			{
				return Results.BadRequest(new { success = "0", message = "No active session for provided session token." });
			}
            return Results.Ok(new { success = "1", message = "Month set successfully" });
        })
        .DisableAntiforgery()
        .WithName("SetMonth")
        .WithOpenApi();

        app.MapPost(API_SET_STATE, ([FromBody] UpdateStateRequest request) => {
            var apiAccessToken = JsonConvert.DeserializeObject<ApiToken>(request.api_access_token);
            var apiAccessRenewToken = JsonConvert.DeserializeObject<ApiToken>(request.api_access_renew_token);
            var requiredSimulations = JsonConvert.DeserializeObject<Dictionary<string,string>>(request.required_simulations);

            EGameState newGameState;
            try
            {
                ValidateRequestData(apiAccessToken, apiAccessRenewToken, request, out newGameState);
            } catch (Exception e) {
                Console.WriteLine(e.Message);
                return Results.BadRequest(new { success = "0", message = "Bad request: " + e.Message });  
            }

            try {
                ValidateRequestAllowed(request.game_session_token, requiredSimulations);
            } catch (Exception e) {
                Console.WriteLine(e.Message);
                return Results.Json(
                    new { success = "0", message = "Request not allowed: " + e.Message },
                    statusCode: StatusCodes.Status405MethodNotAllowed
                );    
            }

            if(m_sessions.TryGetValue(request.game_session_token, out var session))
            {
                session.UpdateState(apiAccessToken!, apiAccessRenewToken!, newGameState, request.month);
			}
            else
            {
				return Results.BadRequest(new { success = "0", message = "No active session for provided session token." });
			}
            return Results.Ok(new { success = "1", message = "State updated successfully" });
        })
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

    private void ValidateRequestData(ApiToken? apiAccessToken, ApiToken? apiAccessRenewToken, UpdateStateRequest request,
        out EGameState newGameState)
    {
        if (apiAccessToken == null || apiAccessRenewToken == null)
        {
            throw new Exception("Invalid JSON format for API tokens");
        }
        if (!Enum.TryParse(request.game_state, true, out newGameState))
        {
            throw new Exception("Invalid game state: " + request.game_state);
        }

        if (newGameState != EGameState.Setup) return;
        
        if (request.game_session_info == null)
        {
            throw new Exception("Missing setup game session info");
        }
        if(IsSetupAccepted(request.game_session_info) && !m_sessions.ContainsKey(request.game_session_token))
        {
            // yes, we accepted this setup, add a new session object
            m_sessions.Add(request.game_session_token, new SimulationSession(
                request.game_session_token, GetServerID(), 
                request.game_session_api, apiAccessToken, apiAccessRenewToken, request.game_session_info, GetSimulationDefinitions(request.game_session_info),
				OnSetupStateEntered, OnSimulationStateEntered, OnSessionClose));
		}
    }

	private bool IsSetupAccepted(GameSessionInfo gameSessionInfo)
	{
		if (OnQuestionAcceptSetupEvent == null) return true;
		return OnQuestionAcceptSetupEvent.GetInvocationList().Cast<Func<GameSessionInfo, bool>?>().All(
			handler => handler != null && handler(gameSessionInfo)
		);
	}

	private void ValidateRequestAllowed(string gameSessionToken, Dictionary<string,string>? requiredSimulations = null)
    {
        if(m_sessions.TryGetValue(gameSessionToken, out var session))
        {
            if (requiredSimulations == null) return;
            session.CheckRequiredSimulations(requiredSimulations);
        }
        else
			throw new Exception("Invalid game session token, you might need to start setup first");
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
		(OnSetupEvent != null ? OnSetupEvent.Invoke(a_session) : Task.CompletedTask)
        .ContinueWith(task => {
			if (task.IsFaulted)
			{
				// output all aggregated exceptions
				foreach (var exception in task.Exception!.InnerExceptions)
				{
					Console.WriteLine(exception.Message);
				}
				return; // do not proceed, the "finished setup" trigger will not be fired, just wait for another setup
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
