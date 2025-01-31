namespace MSPChallenge_Simulation.StateMachine;

public enum State
{
    AwaitingSetup, // await setup game state from MSP server
    Setup, // register kpi's with MSP API
    AwaitingNextMonth, // await next month trigger from MSP server
    Simulation, // calculate kpi's for current month
    Report // submit kpi's to MSP API
}