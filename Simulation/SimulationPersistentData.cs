using System;
using System.Collections.Generic;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication.DataModel;

namespace MSPChallenge_Simulation.Simulation;

public class SimulationPersistentData
{
	private const int DefaultMonth = -1; // setup month

	private int m_currentMonth = DefaultMonth;
	private int m_targetMonth = DefaultMonth;
	private EGameState? m_currentGameState;
	private EGameState? m_targetGameState = EGameState.Setup;
	private DateTime m_lastTickTime = DateTime.Now;
	private bool m_setupAccepted = false;
	private GameSessionInfo? m_gameSessionInfo = null;
	private List<SimulationDefinition>? m_simulationDefinitions = null;

	public LayerMeta m_bathymetryMeta;
	public LayerMeta m_sandDepthMeta;
	public LayerMeta m_pitsMeta;
}