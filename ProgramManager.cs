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

namespace MSPChallenge_Simulation;

public class ProgramManager()
{
	private const int DefaultTickRateMs = 1000; // 1000ms = 1 second

    private int m_tickRateMs = DefaultTickRateMs;
	private DateTime m_lastTickTime = DateTime.Now;
	private readonly string[] m_args = [];

    //Session data
    private Dictionary<string, SimulationSession> m_sessions; //Unique session tokens as keys

	// Define public events
	public event Func<GameSessionInfo, SimulationSession, List<SimulationDefinition>>? OnSimulationDefinitionsEvent;
    public event Func<GameSessionInfo, SimulationSession, bool>? OnQuestionAcceptSetupEvent;
    public event Func<SimulationSession, Task>? OnSetupEvent;
    public event Func<SimulationSession, Task>? OnSimulationStateEnteredEvent;
    public event Func<SimulationSession, Task<List<KPI>>>? OnReportStateEnteredEvent;
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
        Console.WriteLine("Resetting.");
        m_programStateMachine = null;
        m_setupAccepted = false;
        m_gameSessionToken = null;
        m_mspClient = null;
        m_simulationDefinitions = null;
        m_currentGameState = null;
    }
    
    private Task GetSimulationDefinitions()
    {
        if (OnSimulationDefinitionsEvent == null) return Task.CompletedTask;
        m_simulationDefinitions = OnSimulationDefinitionsEvent(m_gameSessionInfo!, m_sessions);
        var nameValueCollection = new NameValueCollection();
        foreach (var simulationDefinition in m_simulationDefinitions)
        {
            nameValueCollection.Add(simulationDefinition.Name, simulationDefinition.Version);
        }
        return GetMspClient().HttpPost("/api/Simulation/Upsert", nameValueCollection,
            new NameValueCollection { { "X-Remove-Previous", "true" } }
        );
    }
    
    private void OnSetupStateEntered(SimulationSession a_session)
    {
        GetMspClient().SetDefaultErrorHandler(exception => { Console.WriteLine("Error: " + exception.Message); });
        GetSimulationDefinitions().ContinueWithOnSuccess(_ => 
            OnSetupEvent != null ? OnSetupEvent.Invoke(a_session) : Task.CompletedTask
        ).Unwrap().ContinueWith(task => {
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
    
    private void OnReportStateEntered(SimulationSession a_session)
    {
        // eg. submit kpi's to MSP API
        OnReportStateEnteredEvent?.Invoke(a_session).ContinueWithOnSuccess(reportKpisTask =>
        {
            var kpis = reportKpisTask.Result;
            return SubmitKpis(kpis);
        }).ContinueWithOnSuccess(_ =>
        {
			a_session.FireStateMachineTrigger(Trigger.FinishedReport);
        });
    }

    private Task SubmitKpis(List<KPI> kpis)
    {
        if (kpis.Count == 0) return Task.CompletedTask;
        return GetMspClient().HttpPost(
            "/api/kpi/BatchPost",
            new NameValueCollection
            {
                { "kpiValues", JsonConvert.SerializeObject(kpis) }
            },
            new NameValueCollection { { "x-notify-monthly-simulation-finished", "true" } }
        );
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
            HandleDotFile(dotfile);
            RunInternal(port, httpsRedirection);
        }, portOption, dotfileOption, httpsRedirectionOption);
        rootCommand.InvokeAsync(m_args).Wait();
    }
    
    public MspClient GetMspClient()
    {
        // throw error is m_mspClient is null
        if (m_mspClient == null)
        {
            throw new Exception("MSP client is not initialized. The client is available after the OnSetupEvent event.");
        }
        return m_mspClient;
    }

    private void HandleDotFile(string? dotfile)
    {
        // if dotfile is set, generate a DOT file of the state machine
        if (string.IsNullOrEmpty(dotfile)) return;
        m_programStateMachine?.WriteToDotFile(dotfile);
    }

    private void Init(string serverId, string gameSessionApi, ApiToken apiAccessToken, ApiToken apiAccessRenewToken)
    {
        m_mspClient ??= new MspClient(serverId, gameSessionApi, apiAccessToken.token, apiAccessRenewToken.token);
		m_mspClient.SetDefaultErrorHandler(exception => { Console.WriteLine("Error: " + exception.Message); });

		// update token just received
		m_mspClient.apiAccessToken = apiAccessToken.token;
        m_mspClient.apiRefreshToken = apiAccessRenewToken.token;
        m_refreshApiAccessTokenTimeLeftSec = RefreshApiAccessTokenFrequencySec;
        
        if (m_programStateMachine != null) return;
        m_programStateMachine = new ProgramStateMachine();
        m_programStateMachine.OnAwaitingSetupStateEnteredEvent += () =>
        {
            m_setupAccepted = false;
            m_simulationDefinitions = null;
        };
        m_programStateMachine.OnSetupStateEnteredEvent += OnSetupStateEntered;
        m_programStateMachine.OnSimulationStateEnteredEvent += OnSimulationStateEntered;
        m_programStateMachine.OnReportStateEnteredEvent += OnReportStateEntered;
    }
    
    private bool IsSetupAccepted(GameSessionInfo gameSessionInfo)
    {
        m_gameSessionInfo = gameSessionInfo;
        if (OnQuestionAcceptSetupEvent == null) return true;
        return OnQuestionAcceptSetupEvent.GetInvocationList().Cast<Func<GameSessionInfo, bool>?>().All(
            handler => handler != null && handler(gameSessionInfo)
        );
    }    
    
    private void RunInternal(int? port, bool? httpsRedirection)
    {
        var builder = WebApplication.CreateBuilder(m_args);
        
        // Load environment variables from .env file
        Env.Load();
        Env.Load(".env.local");

        // Get or create SERVER_ID environment variable
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

        app.MapPost("/Watchdog/Ping", () => Results.Ok(new { success = "1", message = "Pong" }))
        .DisableAntiforgery()
        .WithName("Ping")
        .WithOpenApi();

        app.MapPost("/Watchdog/SetMonth", ([FromBody] SetMonthRequest request) =>
        {
            try {
                ValidateRequestAllowed(request.game_session_token);
            } catch (Exception e) {
                return Results.Json(
                    new { success = "0", message = e.Message },
                    statusCode: StatusCodes.Status405MethodNotAllowed
                );    
            }
            m_targetMonth = request.month;
            Console.WriteLine($"Setting target month to {m_targetMonth}");
            return Results.Ok(new { success = "1", message = "Month set successfully" });
        })
        .DisableAntiforgery()
        .WithName("SetMonth")
        .WithOpenApi();

        //TODO: Token should be duplicates of other sessions, they share an API token
        app.MapPost("/Watchdog/UpdateState", ([FromBody] UpdateStateRequest request) => {
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
            //TODO: create session here?
            Init(serverId, request.game_session_api, apiAccessToken!, apiAccessRenewToken!); // this kicks in the game state machine
            m_targetMonth = request.month;
            Console.WriteLine($"Setting target month to {m_targetMonth}");
            m_targetGameState = newGameState;
            Console.WriteLine($"Setting target game state to {m_targetGameState}");
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
        if(IsSetupAccepted(request.game_session_info))
        {
            // yes, we accepted this setup, add a new session object
            m_sessions.Add(request.game_session_token, new SimulationSession(request.game_session_token));
		}
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
}
