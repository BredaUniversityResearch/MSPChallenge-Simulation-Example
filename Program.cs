using System.Collections.Specialized;
using System.Data;
using System.Reflection.Emit;
using MSPChallenge_Simulation;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication.DataModel;
using MSPChallenge_Simulation.Extensions;
using MSPChallenge_Simulation.Simulation;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

const string API_GET_RASTER = "api/Layer/GetRaster";    //get raster for layer with "layer_name"
const string API_SET_RASTER = "/api/layer/UpdateRaster";//set raster for layer with "layer_name"
const string API_GET_LAYER_LIST = "/api/Layer/List";    //get list of layers with tags matching "layer_tags"
const string API_GET_LAYER_META = "/api/Layer/Meta";    //get layer metadata for layer with "layer_id"
const string API_GET_LAYER_VECTOR = "/api/Layer/Get";   //get geometry objects for layer with "layer_id"

var program = new ProgramManager(args);

program.OnQuestionAcceptSetupEvent += OnQuestionAcceptSetupEvent;
program.GetSimulationDefinitions += GetSimulationDefinitions;
program.OnSetupEvent += SetupSession;
program.OnSimulationStateEnteredEvent += SessionSimulationStateEntered;
program.Run();
return;

List<SimulationDefinition> GetSimulationDefinitions(GameSessionInfo gameSessionInfo)
{
    return [new SimulationDefinition("SandExtraction", "1.0.0")];
}

bool OnQuestionAcceptSetupEvent(GameSessionInfo gameSessionInfo)
{
    //The simulation requires layers only present in the 'OR ELSE' config file
    return "North_OR_ELSE" == gameSessionInfo.config_file_name;
}

// Once connected to the server, start setup.
//   This will register the OnSimulationStateEnteredEvent event with the necessary data - eventually, and if found.
Task SetupSession(SimulationSession session)
{
	NameValueCollection bathymetryTags = new NameValueCollection { { "layer_tags", "Bathymetry,ValueMap" } };
	NameValueCollection sandDepthTags = new NameValueCollection { { "layer_tags", "SandDepth,ValueMap" } };
	NameValueCollection sandPitsTags = new NameValueCollection { { "layer_tags", "SandAndGravel,Extraction,Polygon" } };

    return session.MSPClient.HttpPost<List<LayerMeta>>(
	   API_GET_LAYER_LIST, bathymetryTags
	).ContinueWithOnSuccess(layerListTask =>
    {
        List<LayerMeta> layers = layerListTask.Result;
        if (layers.Count == 0)
            throw new Exception($"Could not find layer with tags: {bathymetryTags["layer_tags"]}.");
        LayerMeta layer = layerListTask.Result[0];
        Console.WriteLine($"Found layer with ID={layer.layer_id}, Name={layer.layer_name}, GeoType={layer.layer_geotype}.");

        return (layer, session.MSPClient.HttpPost<LayerMeta>(
			API_GET_LAYER_META,
            new NameValueCollection
            {
                { "layer_id", layer.layer_id.ToString() }
            }));
    }).ContinueWithOnSuccess(dataset =>
    {
        var (layer, layerMetaTask) = dataset.Result;
		LayerMeta layerWithMeta = layerMetaTask.Result;
        if (layerWithMeta.layer_id == 0)
        {
            throw new Exception($"Could not find layer data for layer id {layer.layer_id}.");
        }
        Console.WriteLine(
            $"Retrieved additional data for Layer with id {layer.layer_id} having {layer.layer_type.Count} layer types.");
        return (layerWithMeta, session.MSPClient.HttpPost<List<SubEntityObject>>(
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
    });
}

// Once the simulation state - the next month - is entered, this event will be triggered.
Task SessionSimulationStateEntered(SimulationSession session) 
{
    //Get pit geometry
	return session.MSPClient.HttpPost<List<SubEntityObject>>(
		   API_GET_LAYER_VECTOR,
		   new NameValueCollection { { "layer_id", session.m_pitsMeta.layer_id.ToString() } }
    ).ContinueWithOnSuccess(pitGeometry =>
    {
        //Get bathymetry raster
        return (pitGeometry, session.MSPClient.HttpPost<RasterRequestResponse>(
           API_GET_RASTER,
           new NameValueCollection { { "layer_name", session.m_bathymetryMeta.layer_name } }
        ));
    }).ContinueWithOnSuccess(dataset =>
	{
		//Do actual simulation calculation
		var (pitGeometry, bathymetryRaster) = dataset.Result;
        RunSimulationMonth(session, bathymetryRaster.Result, pitGeometry.Result);
	});
}


//TODO: Taken from Imagesharp reference, adapt to actual functionality
void RunSimulationMonth(SimulationSession a_session, RasterRequestResponse a_bathymetryRaster, List<SubEntityObject> a_pitGeometry)
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
Task SubmitRasterLayerData(string a_layerName, Image<Rgba32> a_rasterImage, SimulationSession a_session)
{
	using MemoryStream stream = new(16384);
	a_rasterImage.Save(stream, new PngEncoder());
    a_rasterImage.Dispose();
	NameValueCollection postData = new NameValueCollection(2);
	postData.Set("layer_name", a_session.m_bathymetryMeta.layer_name);
	postData.Set("image_data", Convert.ToBase64String(stream.ToArray()));
	return a_session.MSPClient.HttpPost(API_SET_RASTER, postData);
}

void CalculateKpis(
	SimulationSession a_session,
	LayerMeta eezLayer,
    List<SubEntityObject> eezLayerObjects
) {
	List<KPI> kpis = new List<KPI>();
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
