using System;
using System.Collections.Specialized;
using System.Data;
using System.Numerics;
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
using ClipperLib;

const float INT_CONVERSION = 100000000000000.0f;
const string API_GET_RASTER = "api/Layer/GetRaster";    //get raster for layer with "layer_name"
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
	return true;// "North_OR_ELSE" == gameSessionInfo.config_file_name;
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
	/* General algorithm overview:
	 * Per pit, iterate through bathymetry raster pixels that overlap the pit's bounds
	 * Calculate sand volume extracted for pixel (pit depth, available sand, overlap of pit with pixel)
	 * Update bathymetry raster pixel with result
	 */

	using Image<Rgba32> bathRaster = Image.Load < Rgba32 >(Convert.FromBase64String(a_bathymetryRaster.image_data));
	double totalExtractedVolume = 0d;

	//Depth raster dimensions
	int sdRasterPixelWidth = a_session.m_sandDepthRaster.Width;
	int sdRasterPixelHeight = a_session.m_sandDepthRaster.Height;
	float sdRasterRealWidth = a_session.m_sandDepthRasterBounds[1][0] - a_session.m_sandDepthRasterBounds[0][0];
	float sdRasterRealHeight = a_session.m_sandDepthRasterBounds[1][1] - a_session.m_sandDepthRasterBounds[0][1];
	float sdRealWidthPerPixel = sdRasterRealWidth / sdRasterPixelWidth;
	float sdRealHeightPerPixel = sdRasterRealHeight / sdRasterPixelHeight;

	foreach (SubEntityObject pit in a_pitGeometry)
    {
		double pitVolume = 0f;
		int pitDepth;
		if(pit.data == null || !pit.data.TryGetValue("PitExtractionDepth", out string pitDepthStr) || !int.TryParse(pitDepthStr, out pitDepth))
		{
			Console.WriteLine($"Missing pit depth for pit with ID={pit.id}, skipped for calculation");
			continue;
		}

		//Determine pit bounding box
		float pitXMin = float.PositiveInfinity;
		float pitYMin = float.PositiveInfinity;
		float pitXMax = float.NegativeInfinity;
		float pitYMax = float.NegativeInfinity;
		foreach(float[] point in pit.geometry)
		{
			pitXMin = Math.Min(pitXMin, point[0]);	
			pitXMax = Math.Max(pitXMax, point[0]);
			pitYMin = Math.Min(pitYMin, point[1]);
			pitYMax = Math.Max(pitYMax, point[1]);
		}

		//Relative normalized position of the bounding box of the SubEntity within the Raster bounding box.
		//Converts world coordinates to normalized[0, 1] range relative to the raster's bounds.
		float relativeXNormalized = (pitXMin - a_session.m_sandDepthRasterBounds[0][0]) / sdRasterRealWidth;
		float relativeYNormalized = (pitYMin - a_session.m_sandDepthRasterBounds[0][1]) / sdRasterRealHeight;

		//Calculates the range of raster pixels that overlap with the polygon's bounding box.
		int startX = (int)Math.Floor(relativeXNormalized * sdRasterPixelWidth);
		int startY = (int)Math.Floor(relativeYNormalized * sdRasterPixelHeight);
		int endX = (int)Math.Ceiling((relativeXNormalized + (pitXMax - pitXMin) / sdRasterRealWidth) * sdRasterPixelWidth + 1);
		int endY = (int)Math.Ceiling((relativeYNormalized + (pitYMax - pitYMin) / sdRasterRealHeight) * sdRasterPixelHeight + 1);

		//Iterates through every pixel in the calculated range.
		for (int x = startX; x < endX; x++)
		{
			for (int y = startY; y < endY; y++)
			{
				float[,] pixelPoints = {
						{ a_session.m_sandDepthRasterBounds[0][0] + x * sdRealWidthPerPixel, a_session.m_sandDepthRasterBounds[0][1] + y * sdRealHeightPerPixel },
						{ a_session.m_sandDepthRasterBounds[0][0] + x * sdRealWidthPerPixel, a_session.m_sandDepthRasterBounds[0][1] + (y + 1) * sdRealHeightPerPixel },
						{ a_session.m_sandDepthRasterBounds[0][0] + (x + 1) * sdRealWidthPerPixel, a_session.m_sandDepthRasterBounds[0][1] + (y + 1) * sdRealHeightPerPixel },
						{ a_session.m_sandDepthRasterBounds[0][0] + (x + 1) * sdRealWidthPerPixel, a_session.m_sandDepthRasterBounds[0][1] + y * sdRealHeightPerPixel } };

					//Map raster value to actual depth based on your JSON data
					float depth = 0f;
					switch (a_session.m_sandDepthRaster[x, y].R)
					{
						case 43: depth = 2f; break;    // 0-2m
						case 85: depth = 4f; break;    // 2-4m
						case 128: depth = 6f; break;   // 4-6m
						case 170: depth = 8f; break;   // 6-8m
						case 213: depth = 10f; break;  // 8-10m
						case 255: depth = 12f; break;  // 10-12m
						default: depth = 0f; break;   // Unknown value
					}
				pitVolume += GetPolygonOverlapArea(pit.geometry, pixelPoints) * Math.Min(depth, pitDepth);				
			}
		}
		totalExtractedVolume += pitVolume;
	}
	//image.ProcessPixelRows(accessor =>
	//{
	//	// Color is pixel-agnostic, but it's implicitly convertible to the Rgba32 pixel type
	//	Rgba32 transparent = Color.Transparent;

	//	for (int y = 0; y < accessor.Height; y++)
	//	{
	//		Span<Rgba32> pixelRow = accessor.GetRowSpan(y);

	//		// pixelRow.Length has the same value as accessor.Width,
	//		// but using pixelRow.Length allows the JIT to optimize away bounds checks:
	//		for (int x = 0; x < pixelRow.Length; x++)
	//		{
	//			// Get a reference to the pixel at position x
	//			ref Rgba32 pixel = ref pixelRow[x];
	//			if (pixel.A == 0)
	//			{
	//				// Overwrite the pixel referenced by 'ref Rgba32 pixel':
	//				pixel = transparent;
	//			}
	//		}
	//	}
	//});

	//Write new bathymetry raster
	using MemoryStream stream = new(16384);
	bathRaster.Save(stream, new PngEncoder());
	a_session.m_newBathymetryRaster = Convert.ToBase64String(stream.ToArray());
	//bathRaster.Dispose();

	//Set extraction KPIs
	a_session.m_kpis = new List<KPI>() { new KPI()
		{
			name = "Extracted sand volume",
			type = "EXTERNAL",
			value = totalExtractedVolume,
			unit = "m3",
			month = a_session.CurrentMonth+1,
			country = -1 // for now, the server only supports showing non-country specific external KPIs
            //country = layerType.Value.value // eez layer type value = country id
        }};
}

float[,] GetPolygonOverlap(float[][] a_polygon1, float[,] a_polygon2)
{
	Clipper co = new Clipper();
	co.AddPath(FloatSparseToIntPoint(a_polygon1), PolyType.ptClip, true);
	co.AddPath(Float2DToIntPoint(a_polygon2), PolyType.ptSubject, true);

	List<List<IntPoint>> csolution = new List<List<IntPoint>>();
	co.Execute(ClipType.ctIntersection, csolution);
	if (csolution.Count > 0)
	{
		return IntPointToVector(csolution[0]);
	}
	return new float[0,0];
}

float GetPolygonOverlapArea(float[][] a_polygon1, float[,] a_polygon2)
{
	return GetPolygonArea(GetPolygonOverlap(a_polygon1, a_polygon2));
}

float GetPolygonArea(float[,] polygon)
{
	float area = 0;
	for (int i = 0; i < polygon.Length; ++i)
	{
		int j = (i + 1) % polygon.Length;
		area += polygon[i,0] * polygon[j,1] - polygon[i,1] * polygon[j,0];
	}
	return Math.Abs(area * 0.5f);
}

float[,] IntPointToVector(List<IntPoint> points)
{
	float[,] verts = new float[points.Count,2];

	for (int i = 0; i < points.Count; i++)
	{
		verts[i,0] = points[i].X / INT_CONVERSION;
		verts[i,1] = points[i].Y / INT_CONVERSION;
	}

	return verts;
}

List<IntPoint> FloatSparseToIntPoint(float[][] points)
{
	List<IntPoint> verts = new List<IntPoint>();

	for (int i = 0; i < points.Length; i++)
	{
		verts.Add(new IntPoint(points[i][0] * INT_CONVERSION, points[i][1] * INT_CONVERSION));
	}

	return verts;
}

List<IntPoint> Float2DToIntPoint(float[,] points)
{
	List<IntPoint> verts = new List<IntPoint>();

	for (int i = 0; i < points.Length; i++)
	{
		verts.Add(new IntPoint(points[i,0] * INT_CONVERSION, points[i,1] * INT_CONVERSION));
	}

	return verts;
}