using Stateless;
using Stateless.Graph;

namespace MSPChallenge_Simulation_Example.StateMachine;

public class ProgramStateMachine
{
    private readonly StateMachine<State, Trigger> m_machine;
    
    public event Action? OnSetupStateEnteredEvent;
    public event Action? OnSimulationStateEnteredEvent;
    public event Action? OnReportStateEnteredEvent;
    public event Action<StateMachine<State,Trigger>.Transition>? OnStateTransitionedEvent;

    public ProgramStateMachine()
    {
        m_machine = new StateMachine<State, Trigger>(State.AwaitingSetup);

        m_machine.OnUnhandledTrigger((state, trigger) =>
        {
            Console.WriteLine($"Trigger {trigger} is not permitted in state {state}. Ignoring.");
        });
        
        m_machine.Configure(State.AwaitingSetup)
            .Permit(Trigger.SetupGame, State.Setup)
            .OnEntry(() => Console.WriteLine("Entering AwaitingSetup state"))
            .OnExit(() => Console.WriteLine("Exiting AwaitingSetup state"));

        m_machine.Configure(State.Setup)
            .Permit(Trigger.FinishedSetup, State.AwaitingNextMonth)
            .Permit(Trigger.EndGame, State.AwaitingSetup)
            .OnEntry(OnSetupStateEntered)
            .OnExit(() => Console.WriteLine("Exiting Setup state"));

        m_machine.Configure(State.AwaitingNextMonth)
            .Permit(Trigger.SetupGame, State.Setup) // could be that end game is never reached, but a new game arrives
            .Permit(Trigger.MonthUpdated, State.Simulation)
            .Permit(Trigger.EndGame, State.AwaitingSetup)
            .OnEntry(() => Console.WriteLine("Entering AwaitingNextMonth state"))
            .OnExit(() => Console.WriteLine("Exiting AwaitingNextMonth state"));

        m_machine.Configure(State.Simulation)
            .Permit(Trigger.FinishedSimulation, State.Report)
            .OnEntry(OnSimulationStateEntered)
            .OnExit(() => Console.WriteLine("Exiting Simulation state"));
        
        m_machine.Configure(State.Report)
            .Permit(Trigger.FinishedReport, State.AwaitingNextMonth)
            .OnEntry(OnReportStateEntered)
            .OnExit(() => Console.WriteLine("Exiting Report state"));
        
        m_machine.OnTransitioned(transition =>
        {
            Console.WriteLine($"Transitioned from {transition.Source} to {transition.Destination} via {transition.Trigger}");
            OnStateTransitionedEvent?.Invoke(transition);
        });
    }
    
    private void OnSetupStateEntered()
    {
        Console.WriteLine("Entering Setup state");
        
        // eg. register kpi's with MSP API
        OnSetupStateEnteredEvent?.Invoke();
    }
    
    private void OnSimulationStateEntered()
    {
        Console.WriteLine("Entering Simulation state");
        
        // eg. do simulation calculations
        OnSimulationStateEnteredEvent?.Invoke();
    }
    
    private void OnReportStateEntered()
    {
        Console.WriteLine("Entering Report state");
        
        // eg. submit kpi's to MSP API
        OnReportStateEnteredEvent?.Invoke();
    }
    
    public bool CanFire(Trigger trigger)
    {
        return m_machine.CanFire(trigger);
    }

    public void Fire(Trigger trigger)
    {
        m_machine.Fire(trigger);
        Console.WriteLine($"Current state: {m_machine.State}");
    }
    
    public State GetCurrentState()
    {
        return m_machine.State;
    }
    
    public void WriteToDotFile(string filePath)
    {
        var graph = UmlDotGraph.Format(m_machine.GetInfo());
        File.WriteAllText(filePath, graph);
    }
}