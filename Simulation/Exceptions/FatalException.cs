namespace MSPChallenge_Simulation.Simulation.Exceptions;

// FatalException is thrown when a fatal error occurs in the simulation or reporting. This will crash the application
public class FatalException(string message) : Exception(message);
