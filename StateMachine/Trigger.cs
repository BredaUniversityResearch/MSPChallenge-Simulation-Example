namespace MSPChallenge_Simulation.StateMachine;

public enum Trigger
{
    MonthUpdated, // month updated trigger from MSP server
    EndGame, // end game trigger from MSP server, stop simulations
    SetupGame, // setup game trigger from MSP server,
    FinishedSetup,
    FinishedSimulation,
    FinishedReport
}