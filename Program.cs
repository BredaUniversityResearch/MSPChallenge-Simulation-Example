using System.Collections.Specialized;
using MSPChallenge_Simulation;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication.DataModel;
using MSPChallenge_Simulation.Extensions;
using MSPChallenge_Simulation.Simulation;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SunCalcNet;
using SunCalcNet.Model;

// note that this program is designed to only handle one game session at a time
//   any new game session will be ignored until the current game session is finished
var program = new ProgramManager(args);
var kpis = new List<KPI>();

program.OnSimulationDefinitionsEvent += OnSimulationDefinitionsEvent;
program.OnQuestionAcceptSetupEvent += OnQuestionAcceptSetupEvent;
program.OnSetupEvent += Setup;
program.OnReportStateEnteredEvent += () => Task.FromResult(kpis);
program.Run();
return;

List<SimulationDefinition> OnSimulationDefinitionsEvent(GameSessionInfo gameSessionInfo)
{
    // here you can decide based on the game session info data what simulations you want to run
    // e.g. a watchdog could have multiple simulations, but you only want to run some of them
    return [new SimulationDefinition("SunHours", "1.0.0")];
}

bool OnQuestionAcceptSetupEvent(GameSessionInfo gameSessionInfo)
{
    // here you can decide based on the game session info data if you want to accept this game session or not
    return "North_Sea_basic" == gameSessionInfo.config_file_name && // the only one with layer tags 
        Version.Parse(gameSessionInfo.server_version).Major >= 6; // requires at least server version 6.x.x
}

// Once connected to the server, start setup.
//   This will register the OnSimulationStateEnteredEvent event with the necessary data - eventually, and if found.
Task Setup()
{
    var values = new NameValueCollection
    {
        { "layer_tags", "EEZ,Polygon" }
    };
    return program.GetMspClient().HttpPost<List<LayerMeta>>(
        "/api/Layer/List", values
    ).ContinueWithOnSuccess(layerListTask =>
    {
        var layers = layerListTask.Result;
        if (layers.Count == 0)
            throw new Exception($"Could not find layer with tags: {values["layer_tags"]}.");
        var layer = layerListTask.Result[0];
        Console.WriteLine(
            $"Found layer with ID={layer.layer_id}, Name={layer.layer_name}, GeoType={layer.layer_geotype}.");
        return (layer, program.GetMspClient().HttpPost<LayerMeta>(
            "/api/Layer/Meta",
            new NameValueCollection
            {
                { "layer_id", layer.layer_id.ToString() }
            }));
    }).ContinueWithOnSuccess(dataset =>
    {
        var (layer, layerMetaTask) = dataset.Result;
        var layerWithMeta = layerMetaTask.Result;
        if (layerWithMeta.layer_id == 0)
        {
            throw new Exception($"Could not find layer data for layer id {layer.layer_id}.");
        }
        Console.WriteLine(
            $"Retrieved additional data for Layer with id {layer.layer_id} having {layer.layer_type.Count} layer types.");
        return (layerWithMeta, program.GetMspClient().HttpPost<List<SubEntityObject>>(
            "/api/Layer/Get",
            new NameValueCollection
            {
                { "layer_id", layer.layer_id.ToString() }
            }));
    }).ContinueWithOnSuccess(dataset =>
    {
        var (layer, layerGetTask) = dataset.Result;
        var layerObjects = layerGetTask.Result;
        if (layerObjects.Count == 0)
        {
            throw new Exception($"Could not find any layer geometry objects for layer with id {layer.layer_id}");
        }

        Console.WriteLine(
            $"Retrieved geometry for layer with id {layer.layer_id} having {layerObjects.Count} layer objects.");
        foreach (var layerObject in layerObjects)
        {
            Console.WriteLine($"Layer object with ID={layerObject.id}, Type={layerObject.type}.");
        }

        // notify that setup is finished
        program.GetMspClient().HttpPost(
            "/api/Simulation/NotifyMonthSimulationFinished",
            new NameValueCollection
            {
                { "simulation_name", "SunHours" },
                { "month", "-1" }
            }
        );

        program.OnSimulationStateEnteredEvent += (month) =>
            OnSimulationStateEnteredEvent(month, layer, layerObjects);
    });
}

// Once the simulation state - the next month - is entered, this event will be triggered.
Task OnSimulationStateEnteredEvent(
    int month,
    LayerMeta eezLayer,
    List<SubEntityObject> eezLayerObjects
) {
    return program.GetMspClient().HttpPost<YearMonthObject>(
    "/api/Game/GetActualDateForSimulatedMonth",
        new NameValueCollection
        {
            { "simulated_month", month.ToString() }
        }
    ).ContinueWithOnSuccess(task => {
        var yearMonthObject = task.Result;
        if (yearMonthObject.year == 0)
        {
            throw new Exception($"Could not find actual date for simulated month {month}.");
        }
        CalculateKpis(month, yearMonthObject, eezLayer, eezLayerObjects);
    });
}

void CalculateKpis(
    int simulatedMonthIdentifier,
    YearMonthObject yearMonthObject,
    LayerMeta eezLayer,
    List<SubEntityObject> eezLayerObjects
) {
    kpis.Clear();
    foreach (var layerType in eezLayer.layer_type)
    {
        // find the eez layer object that has property type being equal to layerType.key
        var eezLayerObject = eezLayerObjects.Find(obj => obj.type == layerType.Key.ToString());
        if (eezLayerObject == null)
        {
            Console.WriteLine($"Could not find layer object with type {layerType.Key}.");
            continue;
        }
        var sunHoursPerCoordinate = Enumerable.Repeat(0.0, eezLayerObject.geometry.Count).ToList();
        var key = 0;
        foreach (var coordinate in eezLayerObject.geometry)
        {
            var daysInMonth = DateTime.DaysInMonth(yearMonthObject.year, yearMonthObject.month_of_year);
            for (var dayNumber = 1; dayNumber < daysInMonth; ++dayNumber)
            {
                var latLong = ConvertToLatLong(Array.ConvertAll(
                    coordinate.ToArray(), 
                    n => (double)n)
                );
                var sunPhases = SunCalc.GetSunPhases(
                    new DateTime(
                        yearMonthObject.year,
                        yearMonthObject.month_of_year,
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
        Console.WriteLine($"KPI: {kpi.name}, Value: {kpi.value} {kpi.unit}");
        kpis.Add(kpi);
    }
}

double[] ConvertToLatLong(double[] coordinate)
{
    var epsg3035 = ProjectedCoordinateSystem.WGS84_UTM(33, true);
    var epsg4326 = GeographicCoordinateSystem.WGS84;
    var coordinateTransformationFactory = new CoordinateTransformationFactory();
    var transformation = coordinateTransformationFactory.CreateFromCoordinateSystems(epsg3035, epsg4326);
    return transformation.MathTransform.Transform(coordinate);
}
