namespace MSPChallenge_Simulation.Simulation.Exceptions;

// TriggerResetException is thrown when the simulation should be reset to the initial state WaitingSetup
public class TriggerResetException(string message): Exception(message);
