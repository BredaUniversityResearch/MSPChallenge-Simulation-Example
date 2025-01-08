using System.CommandLine;
using DotNetEnv;
using Microsoft.AspNetCore.Mvc;
using MSPChallenge_Simulation_Example.Api;
using MSPChallenge_Simulation_Example.Communication;
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
    
    // created upon 
    private ProgramStateMachine? m_programStateMachine;
    
    // create upon the first UpdateState request
    private MspClient? m_mspClient;

    // Define public events
    public event Action<Action /* onSetupFinished */>? OnSetupEvent;
    public event Action<int /* month */, Action /* onSimulationFinished */>? OnSimulationStateEnteredEvent;
    public event Action<Action /* onReportFinished */>? OnReportStateEnteredEvent;
    public event Action<double /* deltaTimeSec */>? OnTickEvent;
    
    public ProgramManager(string[] args) : this()
    {
        m_args = args;
    }
    
    private void OnSetupStateEntered()
    {
        // eg. load layers from MSP API
        OnSetupEvent?.Invoke(() => {
            m_programStateMachine?.Fire(Trigger.FinishedSetup);
        });
    }
    
    private void OnSimulationStateEntered()
    {
        // eg. do simulation calculations
        OnSimulationStateEnteredEvent?.Invoke(m_currentMonth, () => {
            m_programStateMachine?.Fire(Trigger.FinishedSimulation);
        });
    }
    
    private void OnReportStateEntered()
    {
        // eg. submit kpi's to MSP API
        OnReportStateEnteredEvent?.Invoke(() => {
            m_programStateMachine?.Fire(Trigger.FinishedReport);
        });
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

    private void Init(string gameSessionApi, ApiToken apiAccessToken, ApiToken apiAccessRenewToken)
    {
        m_mspClient ??= new MspClient(gameSessionApi, apiAccessToken, apiAccessRenewToken);
        if (m_programStateMachine != null) return;
        m_programStateMachine = new ProgramStateMachine();
        m_programStateMachine.OnSetupStateEnteredEvent += OnSetupStateEntered;
        m_programStateMachine.OnSimulationStateEnteredEvent += OnSimulationStateEntered;
        m_programStateMachine.OnReportStateEnteredEvent += OnReportStateEntered;          
    }
    
    private void RunInternal(int? port, bool? httpsRedirection)
    {
        var builder = WebApplication.CreateBuilder(m_args);

        // Load environment variables from .env file
        Env.Load();

        // Get or create SERVER_ID environment variable
        var serverId = Environment.GetEnvironmentVariable("SERVER_ID");
        if (string.IsNullOrEmpty(serverId))
        {
            Console.WriteLine("SERVER_ID environment variable is not set. Generating a new one.");
            // Generate a new UUID, save it back to the .env file
            serverId = Guid.NewGuid().ToString();
            Environment.SetEnvironmentVariable("SERVER_ID", serverId);
            File.AppendAllText(".env", $"SERVER_ID={serverId}{Environment.NewLine}");
        }

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
        app.MapPost("/Watchdog/SetMonth", ([FromForm] SetMonthRequest request) =>
        {
            m_targetMonth = request.month;
            Console.WriteLine($"Setting target month to {m_targetMonth}");
            return Results.Ok(new { success = "1", message = "Month set successfully" });
        })
        .DisableAntiforgery()
        .WithName("SetMonth")
        .WithOpenApi();

        app.MapPost("/Watchdog/UpdateState", ([FromForm] UpdateStateRequest request) => {
            var apiAccessToken = JsonConvert.DeserializeObject<ApiToken>(request.api_access_token);
            var apiAccessRenewToken = JsonConvert.DeserializeObject<ApiToken>(request.api_access_renew_token);
            if (apiAccessToken == null || apiAccessRenewToken == null)
            {
                return Results.BadRequest(new { success = "0", message = "Invalid JSON format for API tokens" });
            }
                
            Init(request.game_session_api, apiAccessToken, apiAccessRenewToken); // this kicks in the game state machine
            m_targetMonth = request.month;
            Console.WriteLine($"Setting target month to {m_targetMonth}");
            if (!Enum.TryParse(request.game_state, true, out EGameState newGameState))
            {
                return Results.BadRequest(new { success = "0", message = "Invalid game state" });
            }
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
