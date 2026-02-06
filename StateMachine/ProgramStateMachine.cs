using MSPChallenge_Simulation.Simulation;
using Stateless;
using Stateless.Graph;

namespace MSPChallenge_Simulation.StateMachine;

public class ProgramStateMachine
{
    private readonly StateMachine<State, Trigger> m_machine;
    
    public event Action? OnAwaitingSetupStateEnteredEvent;
    public event Action? OnSetupStateEnteredEvent;
    public event Action? OnSimulationStateEnteredEvent;
    public event Action? OnReportStateEnteredEvent;
    public event Action<StateMachine<State,Trigger>.Transition>? OnStateTransitionedEvent;

    public ProgramStateMachine()
    {
        m_machine = new StateMachine<State, Trigger>(State.AwaitingSetup);

        m_machine.OnUnhandledTrigger((state, trigger) =>
        {
            Util.LogSessionLevel($"Trigger {trigger} is not permitted in state {state}. Ignoring.");
        });

        m_machine.Configure(State.AwaitingSetup)
            .Permit(Trigger.SetupGame, State.Setup)
            .OnEntry(OnAwaitingSetupStateEntered);

        m_machine.Configure(State.Setup)
            .Permit(Trigger.FinishedSetup, State.AwaitingNextMonth)
            .Permit(Trigger.EndGame, State.AwaitingSetup)
            .OnEntry(OnSetupStateEntered);

        m_machine.Configure(State.AwaitingNextMonth)
            .Permit(Trigger.SetupGame, State.Setup) // could be that end game is never reached, but a new game arrives
            .Permit(Trigger.MonthUpdated, State.Simulation)
            .Permit(Trigger.EndGame, State.AwaitingSetup);

        m_machine.Configure(State.Simulation)
            .Permit(Trigger.FinishedSimulation, State.Report)
            .OnEntry(OnSimulationStateEntered);

        m_machine.Configure(State.Report)
            .Permit(Trigger.FinishedReport, State.AwaitingNextMonth)
            .OnEntry(OnReportStateEntered);
        
        m_machine.OnTransitioned(transition =>
        {
			Util.LogSessionLevel($"Transitioned from {transition.Source} to {transition.Destination} via {transition.Trigger}");
            OnStateTransitionedEvent?.Invoke(transition);
        });
    }

    private void OnAwaitingSetupStateEntered()
    {
        OnAwaitingSetupStateEnteredEvent?.Invoke();
    }    
    
    private void OnSetupStateEntered()
    {
        // eg. register kpi's with MSP API
        OnSetupStateEnteredEvent?.Invoke();
    }
    
    private void OnSimulationStateEntered()
    {
        // eg. do simulation calculations
        OnSimulationStateEnteredEvent?.Invoke();
    }
    
    private void OnReportStateEntered()
    {
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