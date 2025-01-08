using System.Collections.Specialized;
using MSPChallenge_Simulation_Example;
using MSPChallenge_Simulation_Example.Communication.DataModel;
using Newtonsoft.Json;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SunCalcNet;
using SunCalcNet.Model;

var program = new ProgramManager(args);
var kpis = new List<KPI>();

program.OnSetupEvent += Setup;
program.OnReportStateEnteredEvent += OnReportStateEnteredEvent;
program.Run();
return;

// Once connected to the server, start setup.
//   This will register the OnSimulationStateEnteredEvent event with the necessary data, if found.
//   User is responsible for invoking the onSetupFinished action once setup is finished.
void Setup(Action onSetupFinished)
{
    program.GetMspClient().SetDefaultHttpPostErrorHandler(exception => {
        Console.WriteLine("Error: " + exception.Message);
    });
    var values = new NameValueCollection
    {
        { "layer_tags", "EEZ,Polygon" }
    };    
    program.GetMspClient().HttpPost("/Layer/List", values,
        onSuccess: (List<LayerMeta> layers) =>
        {
            if (layers.Count == 0)
            {
                Console.WriteLine($"Could not find layer with tags: {values["layer_tags"]}.");
                return;
            }
            OnEezLayerFound(layers[0], onSetupFinished);
        });
}

// Once the simulation state - the next month - is entered, this event will be triggered.
// User is responsible for invoking the onSimulationFinished action once simulation has finished.
void OnSimulationStateEnteredEvent(
    int month,
    LayerMeta eezLayer,
    List<SubEntityObject> eezLayerObjects,
    Action onSimulationFinished
) {
    var values = new NameValueCollection
    {
        { "simulated_month", month.ToString() }
    };
    program.GetMspClient().HttpPost("/Game/GetActualDateForSimulatedMonth", values, 
        onSuccess: (YearMonthObject yearMonthObject) =>
        {
            if (yearMonthObject.year == 0)
            {
                Console.WriteLine($"Could not find actual date for simulated month {month}.");
                return;
            }
            CalculateKpis(month, yearMonthObject, eezLayer, eezLayerObjects, onSimulationFinished);
        });
}

void CalculateKpis(
    int simulatedMonthIdentifier,
    YearMonthObject yearMonthObject,
    LayerMeta eezLayer,
    List<SubEntityObject> eezLayerObjects,
    Action onSimulationFinished
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
            name = "SunHours",
            type = "ECOLOGY", // to fix
            value = sunHoursPerCoordinate.Average(),
            unit = "hours",
            month = simulatedMonthIdentifier,
            country = layerType.Value.value // eez layer type value = country id
        };
        Console.WriteLine($"KPI: {kpi.name} {layerType.Value.displayName}, Value: {kpi.value} {kpi.unit}");
        kpis.Add(kpi);
    }
    onSimulationFinished.Invoke();
}

double[] ConvertToLatLong(double[] coordinate)
{
    var epsg3035 = ProjectedCoordinateSystem.WGS84_UTM(33, true);
    var epsg4326 = GeographicCoordinateSystem.WGS84;
    var coordinateTransformationFactory = new CoordinateTransformationFactory();
    var transformation = coordinateTransformationFactory.CreateFromCoordinateSystems(epsg3035, epsg4326);
    return transformation.MathTransform.Transform(coordinate);
}

// User is responsible for invoking the onReportFinished action once simulation has finished.
void OnReportStateEnteredEvent(Action onReportFinished)
{
    if (kpis.Count == 0) return;
    var values = new NameValueCollection { { "kpiValues", JsonConvert.SerializeObject(kpis) } };
    program.GetMspClient().HttpPost("/kpi/BatchPost", values,
        onSuccess: () =>
        {
            Console.WriteLine("KPI's submitted successfully.");
            onReportFinished.Invoke();
        }
    );
}

void OnEezLayerFound(LayerMeta layer, Action onSetupFinished)
{
    Console.WriteLine($"Found layer with ID={layer.layer_id}, Name={layer.layer_name}, GeoType={layer.layer_geotype}.");
    var values = new NameValueCollection
    {
        { "layer_id", layer.layer_id.ToString() }
    };    
    program.GetMspClient().HttpPost("/Layer/Meta", values,
        onSuccess: (LayerMeta layerWithMeta) =>
        {
            if (layerWithMeta.layer_id == 0)
            {
                Console.WriteLine($"Could not find layer data for layer id {layer.layer_id}.");
                return;
            }
            OnLayerMetaSuccess(layerWithMeta, onSetupFinished);
        });
}

void OnLayerMetaSuccess(LayerMeta layer, Action onSetupFinished)
{
    Console.WriteLine($"Retrieved additional data for Layer with id {layer.layer_id} having {layer.layer_type.Count} layer types.");
    var values = new NameValueCollection
    {
        { "layer_id", layer.layer_id.ToString() }
    };    
    program.GetMspClient().HttpPost("/Layer/Get", values,
        onSuccess: (List<SubEntityObject> layerObjects) =>
        {
            if (layerObjects.Count == 0)
            {
                Console.WriteLine($"Could not find any layer geometry objects for layer with id {layer.layer_id}");
                return;
            }
            OnLayerGetSuccess(layer, layerObjects, onSetupFinished);
        });
}

void OnLayerGetSuccess(LayerMeta layer, List<SubEntityObject> layerObjects, Action onSetupFinished)
{
    Console.WriteLine($"Retrieved geometry for layer with id {layer.layer_id} having {layerObjects.Count} layer objects.");
    foreach (var layerObject in layerObjects)
    {
        Console.WriteLine($"Layer object with ID={layerObject.id}, Type={layerObject.type}.");
    }
    program.OnSimulationStateEnteredEvent += (month, onSimulationFinished) =>
    {
        OnSimulationStateEnteredEvent(month, layer, layerObjects, onSimulationFinished);
    };
    onSetupFinished.Invoke();
}
