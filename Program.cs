using System;
using System.Collections.Specialized;
using MSPChallenge_Simulation;
using MSPChallenge_Simulation.Api;
using MSPChallenge_Simulation.Communication.DataModel;
using MSPChallenge_Simulation.Extensions;
using MSPChallenge_Simulation.Simulation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using ClipperLib;
using System.Numerics;
using System.Data;

const float BATHYMETRY_MAX_DEPTH = 1000f;
const string API_GET_RASTER = "api/Layer/GetRaster";    //get raster for layer with "layer_name"
const string API_GET_LAYER_LIST = "/api/Layer/List";    //get list of layers with tags matching "layer_tags"
const string API_GET_LAYER_META = "/api/Layer/Meta";    //get layer metadata for layer with "layer_id"
const string API_GET_LAYER_VECTOR = "/api/Layer/Get";   //get geometry objects for layer with "layer_id"

var program = new ProgramManager(args);

program.OnQuestionAcceptSessionEvent += OnQuestionAcceptSetupEvent;
program.OnSessionInitialiseEvent += InitialiseSession;
program.OnSimulationStateEnteredEvent += SessionSimulationStateEntered;
program.AddSimulationDefinition("SandExtraction", new Version("1.0.0"));
program.Run();
return;

bool OnQuestionAcceptSetupEvent(GameSessionInfo gameSessionInfo)
{
	//The simulation requires layers only present in the 'OR ELSE' config file
	return true;// "NS_OR_ELSE" == gameSessionInfo.config_file_name;
}

// Once connected to the server, start setup. Get layermeta for all layers required for the simulation.
Task InitialiseSession(SimulationSession a_session)
{
	Task t1 = GetLayerMeta(a_session, "ValueMap,Bathymetry", 0);
	Task t2 = GetLayerMeta(a_session, "ValueMap,SandDepth", 1);
	Task t3 = GetLayerMeta(a_session, "Polygon,SandAndGravel,Extraction", 2);
	Task t4 = GetLayerMeta(a_session, "Line,Coast", 3);
	//TODO: get initial TotalDTS and TotalExtractedVolume

	return Task.WhenAll(t1, t2, t3, t4).ContinueWithOnSuccess(_ => CalculateDTSRaster(a_session));

	//Get bathymetry meta
	//return GetLayerMeta(session, "ValueMap,Bathymetry", 0)
	//	.ContinueWithOnSuccess(_ =>
	//	{
	//		//Get sand depth raster meta
	//		return GetLayerMeta(session, "ValueMap,SandDepth", 1);
	//	})
	//	.ContinueWithOnSuccess(_ =>
	//	{
	//		//Get pits meta
	//		return GetLayerMeta(session, "Polygon,SandAndGravel,Extraction", 2);
	//	});
}

Task GetLayerMeta(SimulationSession a_session, string a_tags, int a_internalLayerID)
{
	return a_session.MSPClient.HttpPost<List<LayerMeta>>(
		API_GET_LAYER_LIST,
		new NameValueCollection { { "layer_tags", a_tags } }
	).ContinueWithOnSuccess(layerListTask =>
	{
		List<LayerMeta> layers = layerListTask.Result;
		if (layers.Count == 0)
			throw new Exception($"Could not find layer with tags: {a_tags}.");
		LayerMeta layer = layerListTask.Result[0];
		Console.WriteLine($"Found layer with ID={layer.layer_id}, Name={layer.layer_name}, GeoType={layer.layer_geotype}.");

		return a_session.MSPClient.HttpPost<LayerMeta>(
			API_GET_LAYER_META,
			new NameValueCollection { { "layer_id", layer.layer_id.ToString() }
		});
	}).ContinueWithOnSuccess(metaResult =>
	{
		a_session.SetLayerMeta(metaResult.Result.Result, a_internalLayerID);
	});
}

Task CalculateDTSRaster(SimulationSession a_session)
{
	return a_session.MSPClient.HttpPost<List<SubEntityObject>>(
		API_GET_LAYER_VECTOR,
		new NameValueCollection { { "layer_id", a_session.m_shoreLineMeta.layer_id.ToString() } }
	).ContinueWithOnSuccess(shoreGeometry =>
	{
		SubEntityObject shoreLine = shoreGeometry.Result[0];

		return (shoreLine, a_session.MSPClient.HttpPost<RasterRequestResponse>(
		   API_GET_RASTER,
		   new NameValueCollection { { "layer_name", a_session.m_sandDepthMeta.layer_name } }
		));
	}).ContinueWithOnSuccess(dataset =>
	{
		var (shoreLine, sandDepthRaster) = dataset.Result;

		CalculateDTSRasterInternal(a_session, shoreLine, sandDepthRaster.Result);
	});
}

void CalculateDTSRasterInternal(SimulationSession a_session, SubEntityObject a_shoreLine, RasterRequestResponse a_sandDepthRaster)
{
	using Image<Rgba32> sdRaster = Image.Load<Rgba32>(Convert.FromBase64String(a_sandDepthRaster.image_data));
	a_session.m_distanceToShoreRaster = new float[sdRaster.Width, sdRaster.Height];

	float widthPerPixel = (a_sandDepthRaster.displayed_bounds[1][0] - a_sandDepthRaster.displayed_bounds[0][0]) / sdRaster.Width;
	float heightPerPixel = (a_sandDepthRaster.displayed_bounds[1][1] - a_sandDepthRaster.displayed_bounds[0][1]) / sdRaster.Height;
	float originX = a_sandDepthRaster.displayed_bounds[0][0] + widthPerPixel / 2f;
	float originY = a_sandDepthRaster.displayed_bounds[0][1] + heightPerPixel / 2f;

	for (int y = 0; y < sdRaster.Height; y++)
	{
		for (int x = 0; x < sdRaster.Width; x++)
		{
			a_session.m_distanceToShoreRaster[x, y] = Util.PointDistanceFromLineString(originX + x * widthPerPixel, originY + y * heightPerPixel, a_shoreLine.geometry) / 1000f;
		}
	}
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
		var (pitGeometry, bathymetryRaster) = dataset.Result;

		//Get sand depth raster
		return (pitGeometry, bathymetryRaster, session.MSPClient.HttpPost<RasterRequestResponse>(
           API_GET_RASTER,
           new NameValueCollection { { "layer_name", session.m_sandDepthMeta.layer_name } }
        ));
    }).ContinueWithOnSuccess(dataset =>
	{
		//Do actual simulation calculation
		var (pitGeometry, bathymetryRaster, sandDepthRaster) = dataset.Result;
        RunSimulationMonth(session, bathymetryRaster.Result, sandDepthRaster.Result, pitGeometry.Result);
	});
}

void RunSimulationMonth(SimulationSession a_session, RasterRequestResponse a_bathymetryRaster, RasterRequestResponse a_sandDepthRaster, List<SubEntityObject> a_pitGeometry)
{
	/* General algorithm overview:
	 * Per !changed! pit:
	 *		Iterate through 'available sand depth' raster pixels that overlap the pit's bounds
	 *			Calculate sand volume extracted for pixel (pit depth, available sand, overlap of pit with pixel)
	 *			Update pixel with extracted amount
	 *		Iterate through 'bathymetry' raster pixels that overlap the pit's bounds
	 *			Determine the avg actual extraction depth for the pixel (using values calculated above)
	 *			Update pixel with extracted amount
	 * Calculate KPI for total extracted volume
	 * Send new rasters and KPIs to server
	 */

	using Image<Rgba32> sdRaster = Image.Load < Rgba32 >(Convert.FromBase64String(a_sandDepthRaster.image_data));
	using Image<Rgba32> bathRaster = Image.Load < Rgba32 >(Convert.FromBase64String(a_bathymetryRaster.image_data));

	double monthlyExtractedVolume = 0d;
	double monthlyDTS = 0d;

	//Raster dimensions
	float sdRasterRealWidth = a_sandDepthRaster.displayed_bounds[1][0] - a_sandDepthRaster.displayed_bounds[0][0];
	float sdRasterRealHeight = a_sandDepthRaster.displayed_bounds[1][1] - a_sandDepthRaster.displayed_bounds[0][1];
	float sdRealWidthPerPixel = sdRasterRealWidth / sdRaster.Width;
	float sdRealHeightPerPixel = sdRasterRealHeight / sdRaster.Height;
	float bathRasterRealWidth = a_bathymetryRaster.displayed_bounds[1][0] - a_bathymetryRaster.displayed_bounds[0][0];
	float bathRasterRealHeight = a_bathymetryRaster.displayed_bounds[1][1] - a_bathymetryRaster.displayed_bounds[0][1];
	float bathRealWidthPerPixel = bathRasterRealWidth / bathRaster.Width;
	float bathRealHeightPerPixel = bathRasterRealHeight / bathRaster.Height;

	foreach (SubEntityObject pit in a_pitGeometry)
	{
		if (pit.implementation_time != a_session.CurrentMonth + 1)
		{
			Console.WriteLine($"Skipping old pit geometry with ID {pit.id}");
			continue;
		}

		double pitVolume = 0f;
		double pitDTS = 0f;
		int pitDepth;
		if (pit.data == null || !pit.data.TryGetValue("PitExtractionDepth", out string pitDepthStr) || !int.TryParse(pitDepthStr, out pitDepth))
		{
			Console.WriteLine($"Missing pit depth for pit with ID={pit.id}, skipped for calculation");
			continue;
		}
		float pitArea = Util.GetPolygonAreaJagged(pit.geometry);

		//Determine pit bounding box
		float pitXMin = float.PositiveInfinity;
		float pitYMin = float.PositiveInfinity;
		float pitXMax = float.NegativeInfinity;
		float pitYMax = float.NegativeInfinity;
		foreach (float[] point in pit.geometry)
		{
			pitXMin = Math.Min(pitXMin, point[0]);
			pitXMax = Math.Max(pitXMax, point[0]);
			pitYMin = Math.Min(pitYMin, point[1]);
			pitYMax = Math.Max(pitYMax, point[1]);
		}

		//Calculates the range of SD raster pixels that overlap with the pit's bounding box.
		int sdStartX = (int)Math.Floor((pitXMin - a_sandDepthRaster.displayed_bounds[0][0]) / sdRasterRealWidth * sdRaster.Width);
		int sdStartY = (int)Math.Floor((pitYMin - a_sandDepthRaster.displayed_bounds[0][1]) / sdRasterRealHeight * sdRaster.Height);
		int sdEndX = (int)Math.Ceiling((pitXMax - a_sandDepthRaster.displayed_bounds[0][0]) / sdRasterRealWidth * sdRaster.Width);
		int sdEndY = (int)Math.Ceiling((pitYMax - a_sandDepthRaster.displayed_bounds[0][1]) / sdRasterRealHeight * sdRaster.Height);

		//Actual extraction depth used to update the bathymetry after the sd raster has been updated
		float[,] extractionDepth = new float[sdEndX - sdStartX, sdEndY - sdStartY];

		//Iterate over sanddepth pixel overlapping pit bb
		for (int x = sdStartX; x < sdEndX; x++)
		{
			for (int y = sdStartY; y < sdEndY; y++)
			{
				float[,] pixelPoints = {
						{ a_sandDepthRaster.displayed_bounds[0][0] + x * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + y * sdRealHeightPerPixel },
						{ a_sandDepthRaster.displayed_bounds[0][0] + x * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + (y + 1) * sdRealHeightPerPixel },
						{ a_sandDepthRaster.displayed_bounds[0][0] + (x + 1) * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + (y + 1) * sdRealHeightPerPixel },
						{ a_sandDepthRaster.displayed_bounds[0][0] + (x + 1) * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + y * sdRealHeightPerPixel } };

				float rasterDepth = (float)sdRaster[x, y].R / 256f * 12f;
				float actualDepth = Math.Min(rasterDepth, pitDepth);
				float overlapArea = Util.GetPolygonOverlapArea(pit.geometry, pixelPoints);
				float extractedVolume = overlapArea * actualDepth;

				//Extracted volume / (total volume in pixel) * pixel value
				byte newSDRasterValue = (byte)(extractedVolume / (sdRealWidthPerPixel * sdRealHeightPerPixel * rasterDepth) * sdRaster[x, y].R);

				extractionDepth[x - sdStartX, y - sdStartY] = actualDepth;
				sdRaster[x, y] = new Rgba32(newSDRasterValue, 0, 0);
				pitVolume += extractedVolume;
				pitDTS += a_session.m_distanceToShoreRaster[x, y];
			}
		}
		monthlyExtractedVolume += pitVolume;
		monthlyDTS += pitDTS;

		//Calculates the range of bathymetry raster pixels that overlap with the pit's bounding box.
		int bathStartX = (int)Math.Floor((pitXMin - a_bathymetryRaster.displayed_bounds[0][0]) / bathRasterRealWidth * bathRaster.Width);
		int bathStartY = (int)Math.Floor((pitYMin - a_bathymetryRaster.displayed_bounds[0][1]) / bathRasterRealHeight * bathRaster.Height);
		int bathEndX = (int)Math.Ceiling((pitXMax - a_bathymetryRaster.displayed_bounds[0][0]) / bathRasterRealWidth * bathRaster.Width);
		int bathEndY = (int)Math.Ceiling((pitYMax - a_bathymetryRaster.displayed_bounds[0][1]) / bathRasterRealHeight * bathRaster.Height);

		//Iterate over bathymetry pixels overlapping pit bb
		for (int x = bathStartX; x < bathEndX; x++)
		{
			for (int y = bathStartY; y < bathEndY; y++)
			{
				float[,] bathPixelPoints = {
						{ a_bathymetryRaster.displayed_bounds[0][0] + x * bathRealWidthPerPixel, a_bathymetryRaster.displayed_bounds[0][1] + y * bathRealHeightPerPixel },
						{ a_bathymetryRaster.displayed_bounds[0][0] + x * bathRealWidthPerPixel, a_bathymetryRaster.displayed_bounds[0][1] + (y + 1) * bathRealHeightPerPixel },
						{ a_bathymetryRaster.displayed_bounds[0][0] + (x + 1) * bathRealWidthPerPixel, a_bathymetryRaster.displayed_bounds[0][1] + (y + 1) * bathRealHeightPerPixel },
						{ a_bathymetryRaster.displayed_bounds[0][0] + (x + 1) * bathRealWidthPerPixel, a_bathymetryRaster.displayed_bounds[0][1] + y * bathRealHeightPerPixel } };

				float[,] bathPitOverlap = Util.GetPolygonOverlap(pit.geometry, bathPixelPoints);
				float bathPitOverlapArea = Util.GetPolygonArea(bathPitOverlap);
				if (bathPitOverlapArea < 0.001f)
					continue;
				float bathPixelArea = bathRealWidthPerPixel * bathRealHeightPerPixel; //Area of bath pixel
				float coverageFraction = bathPitOverlapArea / bathPixelArea; //Fraction of bath pixel covered by pit

				//Find average extraction depth of the pit in bathymetry pixel 
				float avgExtractionDepth = 0f;
				//Min and max pixel coordinates of this bath pixel on the sd raster
				int depthStartX = Math.Max(sdStartX, (int)Math.Floor((bathPixelPoints[0, 0] - a_sandDepthRaster.displayed_bounds[0][0]) / sdRasterRealWidth * sdRaster.Width));
				int depthStartY = Math.Max(sdStartY, (int)Math.Floor((bathPixelPoints[0, 1] - a_sandDepthRaster.displayed_bounds[0][1]) / sdRasterRealHeight * sdRaster.Height));
				int depthEndX = Math.Min(sdEndX, (int)Math.Ceiling((bathPixelPoints[2, 0] - a_sandDepthRaster.displayed_bounds[0][0]) / sdRasterRealWidth * sdRaster.Width));
				int depthEndY = Math.Min(sdEndY, (int)Math.Ceiling((bathPixelPoints[2, 1] - a_sandDepthRaster.displayed_bounds[0][1]) / sdRasterRealHeight * sdRaster.Height));
				for (int depthX = depthStartX; depthX < depthEndX; depthX++)
				{
					for (int depthY = depthStartY; depthY < depthEndY; depthY++)
					{
						float[,] sdPixelPoints = {
							{ a_sandDepthRaster.displayed_bounds[0][0] + depthX * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + depthY * sdRealHeightPerPixel },
							{ a_sandDepthRaster.displayed_bounds[0][0] + depthX * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + (depthY + 1) * sdRealHeightPerPixel },
							{ a_sandDepthRaster.displayed_bounds[0][0] + (depthX + 1) * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + (depthY + 1) * sdRealHeightPerPixel },
							{ a_sandDepthRaster.displayed_bounds[0][0] + (depthX + 1) * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + depthY * sdRealHeightPerPixel } };
						//Find overlap of all 3 (pit∪bath∪sd)
						float threeOverlapArea = Util.GetRectangleOverlapArea(bathPitOverlap, sdPixelPoints);
						//Add pre-normalized extraction depth to bath pixel's avg by multiplying with (bath∪pit) coverage fraction
						avgExtractionDepth += threeOverlapArea / bathPitOverlapArea * extractionDepth[depthX - sdStartX, depthY - sdStartY];
					}
				}

				//Update bathymetry raster with avg extraction depth on pixel multiplied by coverage
				float newDepth = GetBathymeteryDepthForRaster(bathRaster[x, y].R) - avgExtractionDepth * coverageFraction;
				bathRaster[x, y] = new Rgba32(GetBathymeteryValueForDepth(newDepth), 0, 0);
			}
		}
	}

	//Write new depth raster
	using MemoryStream stream = new(16384);
	sdRaster.Save(stream, new PngEncoder());
	a_session.m_newBathymetryRaster = Convert.ToBase64String(stream.ToArray());
	stream.Dispose();
	sdRaster.Dispose();

	//Write new bathymetry raster
	using MemoryStream stream2 = new(16384);
	bathRaster.Save(stream2, new PngEncoder());
	a_session.m_newBathymetryRaster = Convert.ToBase64String(stream2.ToArray());
	stream2.Dispose();
	bathRaster.Dispose();

	a_session.m_totalDTS += monthlyDTS;
	a_session.m_totalExtractedVolume += monthlyExtractedVolume;

	//Set extraction KPIs
	a_session.m_kpis = new List<KPI>() { 
		new KPI() {
			name = "Monthly Sand Volume",
			type = "EXTERNAL",
			value = monthlyExtractedVolume,
			unit = "m3",
			month = a_session.CurrentMonth+1,
			country = -1 // for now, the server only supports showing non-country specific external KPIs
        },
		new KPI() {
			name = "Cumulative Sand Volume",
			type = "EXTERNAL",
			value = a_session.m_totalExtractedVolume,
			unit = "m3",
			month = a_session.CurrentMonth+1,
			country = -1 
        },
		new KPI() {
			name = "Monthly DTS",
			type = "EXTERNAL",
			value = monthlyDTS,
			unit = "km",
			month = a_session.CurrentMonth+1,
			country = -1 
        },
		new KPI() {
			name = "Total DTS",
			type = "EXTERNAL",
			value = a_session.m_totalDTS,
			unit = "km",
			month = a_session.CurrentMonth+1,
			country = -1 
        }
	};
}

float GetBathymeteryDepthForRaster(byte a_value)
{
	//TODO: bath depth is not linear
	return a_value / 256f * BATHYMETRY_MAX_DEPTH;
}

byte GetBathymeteryValueForDepth(float a_depth)
{
	//TODO: bath depth is not linear
	return (byte)(a_depth / BATHYMETRY_MAX_DEPTH * 256f);
}