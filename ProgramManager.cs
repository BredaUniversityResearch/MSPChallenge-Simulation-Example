using System.Collections.Specialized;
using System.CommandLine;
using DotNetEnv;
using Microsoft.AspNetCore.Mvc;
using MSPChallenge_Simulation_Example.Api;
using MSPChallenge_Simulation_Example.Communication;
using MSPChallenge_Simulation_Example.Communication.DataModel;
using MSPChallenge_Simulation_Example.Extensions;
using MSPChallenge_Simulation_Example.Simulation;
using MSPChallenge_Simulation_Example.Simulation.Exceptions;
using MSPChallenge_Simulation_Example.StateMachine;
using Newtonsoft.Json;

namespace MSPChallenge_Simulation_Example;

public class ProgramManager()
{
    private const int DefaultTickRateMs = 1000; // 1000ms = 1 second
    private const int DefaultMonth = -1; // setup month
    private int m_tickRateMs = DefaultTickRateMs;
    private readonly string[] m_args = [];
    private int m_currentMonth = DefaultMonth;
    private int m_targetMonth = DefaultMonth;
    private EGameState? m_currentGameState;
    private EGameState? m_targetGameState = EGameState.Setup;
    private DateTime m_lastTickTime = DateTime.Now;
    private bool m_setupAccepted = false;
    private string? m_gameSessionToken = null;
    private GameSessionInfo? m_gameSessionInfo = null;
    private List<SimulationDefinition>? m_simulationDefinitions = null;
    
    // created upon 
    private ProgramStateMachine? m_programStateMachine;
    
    // create upon the first UpdateState request
    private MspClient? m_mspClient;

    // Define public events
    public event Func<GameSessionInfo, List<SimulationDefinition>>? OnSimulationDefinitionsEvent;
    public event Func<GameSessionInfo, bool>? OnQuestionAcceptSetupEvent;
    public event Func<Task>? OnSetupEvent;
    public event Func<int /* month */, Task>? OnSimulationStateEnteredEvent;
    public event Func<Task<List<KPI>>>? OnReportStateEnteredEvent;
    public event Action<double /* deltaTimeSec */>? OnTickEvent;
    
    public ProgramManager(string[] args) : this()
    {
        m_args = args;
    }
    
    private Task GetSimulationDefinitions()
    {
        if (OnSimulationDefinitionsEvent == null) return Task.CompletedTask;
        m_simulationDefinitions = OnSimulationDefinitionsEvent(m_gameSessionInfo!);
        var tasks = new List<Task>();
        foreach (var definition in m_simulationDefinitions!)
        {
            tasks.Add(GetMspClient().HttpPost("/api/Simulation/Upsert", new NameValueCollection
            {
                { "name", definition.Name },
                { "version", definition.Version }
            }));
        }
        return Task.WhenAll(tasks);
    }
    
    private void OnSetupStateEntered()
    {
        GetSimulationDefinitions().ContinueWithOnSuccess(_ => 
            OnSetupEvent != null ? OnSetupEvent.Invoke() : Task.CompletedTask
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
            m_programStateMachine?.Fire(Trigger.FinishedSetup);
        });
    }
    
    private void OnSimulationStateEntered()
    {
        // eg. do simulation calculations
        OnSimulationStateEnteredEvent?.Invoke(m_currentMonth).ContinueWith(task => {
            HandleTaskExceptions(task);
            m_programStateMachine?.Fire(Trigger.FinishedSimulation);
        });
    }
    
    private void OnReportStateEntered()
    {
        // eg. submit kpi's to MSP API
        OnReportStateEnteredEvent?.Invoke().ContinueWithOnSuccess(reportKpisTask =>
        {
            HandleTaskExceptions(reportKpisTask);
            var kpis = reportKpisTask.Result;
            return SubmitKpis(kpis);
        }).ContinueWithOnSuccess(submitKpisTask =>
        {
            HandleTaskExceptions(submitKpisTask);
            m_programStateMachine?.Fire(Trigger.FinishedReport);
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
            }
        );
    }

    private void HandleTaskExceptions(Task task)
    {
        if (!task.IsFaulted) return;
        // output all aggregated exceptions
        FatalException? fatal = null;
        TriggerResetException? reset = null;
        foreach (var exception in task.Exception!.InnerExceptions)
        {
            Console.WriteLine(exception.Message);
            if (exception is FatalException fatalException)
            {
                fatal = fatalException;
            }
            if (exception is TriggerResetException resetException)
            {
                reset = resetException;
            }            
        }
        
        if (fatal != null)
        {
            throw fatal; // Rethrow to crash the application
        }

        if (reset == null) return;
        m_programStateMachine = null;
        m_setupAccepted = false;
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
        m_mspClient ??= new MspClient(serverId, gameSessionApi, apiAccessToken, apiAccessRenewToken);
        if (m_programStateMachine != null) return;
        m_programStateMachine = new ProgramStateMachine();
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
        app.MapPost("/Watchdog/SetMonth", ([FromBody] SetMonthRequest request) =>
        {
            try {
                ValidateRequest(request.game_session_token);
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

        app.MapPost("/Watchdog/UpdateState", ([FromBody] UpdateStateRequest request) => {
            var apiAccessToken = JsonConvert.DeserializeObject<ApiToken>(request.api_access_token);
            var apiAccessRenewToken = JsonConvert.DeserializeObject<ApiToken>(request.api_access_renew_token);
            var requiredSimulations = JsonConvert.DeserializeObject<Dictionary<string,string>>(request.required_simulations);
            if (apiAccessToken == null || apiAccessRenewToken == null)
            {
                return Results.BadRequest(new { success = "0", message = "Invalid JSON format for API tokens" });
            }
            if (!Enum.TryParse(request.game_state, true, out EGameState newGameState))
            {
                return Results.BadRequest(new { success = "0", message = "Invalid game state" });
            }
            if (newGameState == EGameState.Setup)
            {
                if (request.game_session_info == null)
                {
                    return Results.BadRequest(new { success = "0", message = "Missing setup game session info" });
                }
                // yes, we accepted this setup, and from now on we will only accept messages having this game session token
                m_setupAccepted = IsSetupAccepted(request.game_session_info);
                m_gameSessionToken = request.game_session_token;
            }
            try {
                ValidateRequest(request.game_session_token, requiredSimulations);
            } catch (Exception e) {
                return Results.Json(
                    new { success = "0", message = e.Message },
                    statusCode: StatusCodes.Status405MethodNotAllowed
                );    
            }
            Init(serverId, request.game_session_api, apiAccessToken, apiAccessRenewToken); // this kicks in the game state machine
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

    private void ValidateRequest(string gameSessionToken, Dictionary<string,string>? requiredSimulations = null)
    {
        if (!m_setupAccepted)
        {
            throw new Exception("Request method not allowed");
        }
        if (m_gameSessionToken != gameSessionToken)
        {
            throw new Exception("Invalid game session token");
        }
        if (requiredSimulations == null) return;
        if (m_simulationDefinitions == null) return;
        // if a required simulation is set, and it is not in the list of available simulations, throw an exception
        foreach (var simulationDefinition in m_simulationDefinitions)
        {
            if (!requiredSimulations.ContainsKey(simulationDefinition.Name))
                throw new Exception($"Required simulation {simulationDefinition.Name} is not available");
            if (new Version(requiredSimulations[simulationDefinition.Name]) > new Version(simulationDefinition.Version))
                throw new Exception($"Required simulation {simulationDefinition.Name} v{requiredSimulations[simulationDefinition.Name]} is not available");
        }
    }
    
    private void Tick(object? state)
    {
        var currentTickTime = DateTime.Now;
        var deltaTime = currentTickTime - m_lastTickTime;
        m_lastTickTime = currentTickTime;
        OnTickEvent?.Invoke(deltaTime.TotalSeconds);
        
        if (
            // If game state is Setup the state machine goes to Setup state as well
            m_currentGameState != m_targetGameState && m_targetGameState == EGameState.Setup &&
            // But if the state machine is not ready yet, postpone the state change
            m_programStateMachine?.CanFire(Trigger.SetupGame) == true
        ) {
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
}
