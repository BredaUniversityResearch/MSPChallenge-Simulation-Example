using System.Collections.Specialized;
using Microsoft.AspNetCore.Http;
using MSPChallenge_Simulation;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication.DataModel;
using MSPChallenge_Simulation.Extensions;
using MSPChallenge_Simulation.Simulation;
using MSPChallenge_Simulation.StateMachine;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SunCalcNet;
using SunCalcNet.Model;

// note that this program is designed to only handle one game session at a time
//   any new game session will be ignored until the current game session is finished
var program = new SessionManager(args);

program.AddSimulationDefinition(SessionManager.SIM_NAME, new Version("1.0.0"));
program.OnQuestionAcceptSessionEvent += OnQuestionAcceptSetupEvent;
program.OnSessionInitialiseEvent += InitialiseSession;
program.OnSimulationStateEnteredEvent += SessionSimulationStateEntered;
program.Run();
return;

List<SimulationDefinition> OnSimulationDefinitionsEvent(GameSessionInfo gameSessionInfo)
{
    // here you can decide based on the game session info data what simulations you want to run
    // e.g. a watchdog could have multiple simulations, but you only want to run some of them
    return [new SimulationDefinition(SessionManager.SIM_NAME, "1.0.0")];
}

bool OnQuestionAcceptSetupEvent(GameSessionInfo gameSessionInfo)
{
    // here you can decide based on the game session info data if you want to accept this game session or not
    return "North_Sea_basic" == gameSessionInfo.config_file_name && // the only one with layer tags 
        Version.Parse(gameSessionInfo.server_version).Major >= 6; // requires at least server version 6.x.x
}

// Once connected to the server, start setup.
//   This will register the OnSimulationStateEnteredEvent event with the necessary data - eventually, and if found.
Task InitialiseSession(SimulationSession a_session)
{
    var values = new NameValueCollection
    {
        { "layer_tags", "EEZ,Polygon" }
    };
    return a_session.MSPClient.HttpPost<List<LayerMeta>>(
        "/api/Layer/List", values
    ).ContinueWithOnSuccess(layerListTask =>
    {
        var layers = layerListTask.Result;
        if (layers.Count == 0)
            throw new Exception($"Could not find layer with tags: {values["layer_tags"]}.");
        var layer = layerListTask.Result[0];
        Console.WriteLine(
            $"Found layer with ID={layer.layer_id}, Name={layer.layer_name}, GeoType={layer.layer_geotype}.");
        return (layer, a_session.MSPClient.HttpPost<LayerMeta>(
            "/api/Layer/Meta",
            new NameValueCollection
            {
                { "layer_id", layer.layer_id.ToString() }
            }));
    }).ContinueWithOnSuccess(request =>
    {
        var (layer, layerMetaTask) = request.Result;
		a_session.m_eezLayerMeta = layerMetaTask.Result;
        if (a_session.m_eezLayerMeta.layer_id == 0)
        {
            throw new Exception($"Could not find layer data for layer id {layer.layer_id}.");
        }
        Console.WriteLine(
            $"Retrieved additional data for Layer with id {layer.layer_id} having {layer.layer_type.Count} layer types.");
        return a_session.MSPClient.HttpPost<List<SubEntityObject>>(
            "/api/Layer/Get",
            new NameValueCollection
            {
                { "layer_id", layer.layer_id.ToString() }
            });
    }).ContinueWithOnSuccess(geometry =>
    {
		a_session.m_eezGeometry = geometry.Result.Result;
        if (a_session.m_eezGeometry == null || a_session.m_eezGeometry.Count == 0)
        {
            throw new Exception($"Could not find any layer geometry objects for layer with id {a_session.m_eezLayerMeta.layer_id}");
        }

        Console.WriteLine(
            $"Retrieved geometry for layer with id {a_session.m_eezLayerMeta.layer_id} having {a_session.m_eezGeometry.Count} layer objects.");
        foreach (var layerObject in a_session.m_eezGeometry)
        {
            Console.WriteLine($"Layer object with ID={layerObject.id}, Type={layerObject.type}.");
        }
    });
}

// Once the simulation state - the next month - is entered, this event will be triggered.
Task SessionSimulationStateEntered(SimulationSession a_session)
{
	Util.LogSimLevel0($"Starting internal simulation for month {a_session.CurrentMonth}.");
	return a_session.MSPClient.HttpPost<YearMonthObject>(
    "/api/Game/GetActualDateForSimulatedMonth",
        new NameValueCollection
        {
            { "simulated_month", a_session.CurrentMonth.ToString() }
        }
    ).ContinueWithOnSuccess(task => {
        var yearMonthObject = task.Result;
        if (yearMonthObject.year == 0)
        {
            throw new Exception($"Could not find actual date for simulated month {a_session.CurrentMonth}.");
        }
        CalculateKpis(a_session.CurrentMonth, yearMonthObject, a_session);
    });
}

void CalculateKpis(
    int simulatedMonthIdentifier,
    YearMonthObject yearMonthObject,
	SimulationSession a_session
) {
	Util.LogSimLevel0($"Calculating KPIs for month {a_session.CurrentMonth}.");
    a_session.m_kpis = new List<KPI>();

	foreach (var layerType in a_session.m_eezLayerMeta.layer_type)
    {
        // find the eez layer object that has property type being equal to layerType.key
        var eezLayerObject = a_session.m_eezGeometry.Find(obj => obj.type == layerType.Key.ToString());
        if (eezLayerObject == null)
        {
            Console.WriteLine($"Could not find layer object with type {layerType.Key}.");
            continue;
        }
        var sunHoursPerCoordinate = Enumerable.Repeat(0.0, eezLayerObject.geometry.Count).ToList();
        var key = 0;
        foreach (var coordinate in eezLayerObject.geometry)
        {
            var daysInMonth = DateTime.DaysInMonth(yearMonthObject.year, yearMonthObject.month_of_year+1); //Uses 1-indexed months...
            for (var dayNumber = 1; dayNumber < daysInMonth; ++dayNumber)
            {
                var latLong = ConvertToLatLong(Array.ConvertAll(
                    coordinate.ToArray(), 
                    n => (double)n)
                );
                var sunPhases = SunCalc.GetSunPhases(
                    new DateTime(
                        yearMonthObject.year,
                        yearMonthObject.month_of_year+1, //Uses 1-indexed months...
						dayNumber
                    ),
                    latLong[0],
                latLong[1]
                )
                .ToDictionary(phase => phase.Name.Value, phase => phase);
                if (!sunPhases.TryGetValue(SunPhaseName.Sunset.Value, out var sunsetPhase)) continue;
                if (!sunPhases.TryGetValue(SunPhaseName.Sunrise.Value, out var sunrisePhase)) continue;
                var sunTimeSpan = sunsetPhase.PhaseTime - sunrisePhase.PhaseTime;
                sunHoursPerCoordinate[key] += sunTimeSpan.TotalHours;                
            }
            ++key;
        }

        var kpi = new KPI()
        {
            name = $"SunHours {layerType.Value.displayName}",
            type = "EXTERNAL",
            value = sunHoursPerCoordinate.Average(),
            unit = "hours",
            month = simulatedMonthIdentifier,
            country = -1 // for now, the server only supports showing non-country specific external KPIs
            //country = layerType.Value.value // eez layer type value = country id
        };
		a_session.m_kpis.Add(kpi);
		Util.LogSimLevel1($"KPI: {kpi.name}, Value: {kpi.value} {kpi.unit}");
	}
	a_session.FireStateMachineTrigger(Trigger.FinishedSimulation);
}

double[] ConvertToLatLong(double[] coordinate)
{
    var epsg3035 = ProjectedCoordinateSystem.WGS84_UTM(33, true);
    var epsg4326 = GeographicCoordinateSystem.WGS84;
    var coordinateTransformationFactory = new CoordinateTransformationFactory();
    var transformation = coordinateTransformationFactory.CreateFromCoordinateSystems(epsg3035, epsg4326);
    return transformation.MathTransform.Transform(coordinate);
}
