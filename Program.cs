using System.Collections.Specialized;
using MSPChallenge_Simulation;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication.DataModel;
using MSPChallenge_Simulation.Extensions;
using MSPChallenge_Simulation.Simulation;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

const string API_GET_RASTER = "api/Layer/GetRaster"; //get raster for layer with "layer_name"
const string API_GET_LAYER_LIST = "/api/Layer/List"; //get list of layers with tags matching "layer_tags"
const string API_GET_LAYER_META = "/api/Layer/Meta"; //get layer metadata for layer with "layer_id"
const string API_GET_LAYER_VECTOR = "/api/Layer/Get"; //get geometry objects for layer with "layer_id"


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

List<SimulationDefinition> OnSimulationDefinitionsEvent(GameSessionInfo gameSessionInfo, SimulationPersistentData persistentData)
{
    // here you can decide based on the game session info data what simulations you want to run
    // e.g. a watchdog could have multiple simulations, but you only want to run some of them
    return [new SimulationDefinition("SandExtraction", "1.0.0")];
}

bool OnQuestionAcceptSetupEvent(GameSessionInfo gameSessionInfo, SimulationPersistentData persistentData)
{
    // here you can decide based on the game session info data if you want to accept this game session or not
    return "North_OR_ELSE" == gameSessionInfo.config_file_name; // the only one with layer tags
}

// Once connected to the server, start setup.
//   This will register the OnSimulationStateEnteredEvent event with the necessary data - eventually, and if found.
Task Setup(SimulationPersistentData persistentData)
{
    var values = new NameValueCollection
    {
        { "layer_tags", "EEZ,Polygon" }
    };
    return program.GetMspClient().HttpPost<List<LayerMeta>>(
	   API_GET_LAYER_LIST, values
    ).ContinueWithOnSuccess(layerListTask =>
    {
        var layers = layerListTask.Result;
        if (layers.Count == 0)
            throw new Exception($"Could not find layer with tags: {values["layer_tags"]}.");
        var layer = layerListTask.Result[0];
        Console.WriteLine(
            $"Found layer with ID={layer.layer_id}, Name={layer.layer_name}, GeoType={layer.layer_geotype}.");
        return (layer, program.GetMspClient().HttpPost<LayerMeta>(
			API_GET_LAYER_META,
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
			API_GET_LAYER_VECTOR,
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

        program.OnSimulationStateEnteredEvent += OnSimulationStateEnteredEvent;
    });
}

// Once the simulation state - the next month - is entered, this event will be triggered.
Task OnSimulationStateEnteredEvent(int month, SimulationPersistentData persistentData) 
{
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
		CalculatateKPIsForMonth(month, persistentData);
    });
}


//TODO: Taken from Imagesharp reference, adapt to actual functionality
void CalculatateKPIsForMonth(int month, SimulationPersistentData persistentData)
{
	using Image<Rgba32> image = Image.Load<Rgba32>("my_file.png");
	image.ProcessPixelRows(accessor =>
	{
		// Color is pixel-agnostic, but it's implicitly convertible to the Rgba32 pixel type
		Rgba32 transparent = Color.Transparent;

		for (int y = 0; y < accessor.Height; y++)
		{
			Span<Rgba32> pixelRow = accessor.GetRowSpan(y);

			// pixelRow.Length has the same value as accessor.Width,
			// but using pixelRow.Length allows the JIT to optimize away bounds checks:
			for (int x = 0; x < pixelRow.Length; x++)
			{
				// Get a reference to the pixel at position x
				ref Rgba32 pixel = ref pixelRow[x];
				if (pixel.A == 0)
				{
					// Overwrite the pixel referenced by 'ref Rgba32 pixel':
					pixel = transparent;
				}
			}
		}
	});
}

//TODO: Taken from MEL, adapt to the raster we need to send
public void SubmitRasterLayerData(string layerName, Bitmap rasterImage)
{
	using MemoryStream stream = new(16384);
#pragma warning disable CA1416 // Validate platform compatibility
	rasterImage.Save(stream, ImageFormat.Png);
#pragma warning restore CA1416
	NameValueCollection postData = new NameValueCollection(2);
	postData.Set("layer_name", MEL.ConvertLayerName(layerName));
	postData.Set("image_data", Convert.ToBase64String(stream.ToArray()));
	HttpSet("/api/layer/UpdateRaster", postData);
}

void CalculateKpis(
    int simulatedMonthIdentifier,
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
        };
        

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
