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
using System.Numerics;
using System.Data;
using Clipper2Lib;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

const float BATHYMETRY_MAX_DEPTH = 1000f;
const float SANDDEPTH_MAX_DEPTH = 12f;
const string API_GET_RASTER = "api/Layer/GetRaster";    //get raster for layer with "layer_name"
const string API_GET_LAYER_LIST = "/api/Layer/List";    //get list of layers with tags matching "layer_tags"
const string API_GET_LAYER_META = "/api/Layer/Meta";    //get layer metadata for layer with "layer_id"
const string API_GET_LAYER_VECTOR = "/api/Layer/GetGeometry";   //get geometry objects for layer with "layer_id"

var program = new ProgramManager(args);

program.AddSimulationDefinition("SandExtraction", new Version("1.0.0"));
program.OnQuestionAcceptSessionEvent += OnQuestionAcceptSetupEvent;
program.OnSessionInitialiseEvent += InitialiseSession;
program.OnSimulationStateEnteredEvent += SessionSimulationStateEntered;
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
	Console.WriteLine($"Initialising session: {a_session.SessionToken}.");
	Task t1 = GetLayerMeta(a_session, "ValueMap,Bathymetry", 0);
	Task t2 = GetLayerMeta(a_session, "ValueMap,SandDepth,SandAndGravel", 1);
	Task t3 = GetLayerMeta(a_session, "Polygon,SandAndGravel,Extraction", 2);
	Task t4 = GetLayerMeta(a_session, "Line,Coast", 3);
	//TODO: get initial TotalDTS and TotalExtractedVolume

	return Task.Run(async () =>
	{
		await Task.WhenAll(t1, t2, t3, t4);
		await CalculateDTSRaster(a_session);
	});
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
			throw new Exception($"	Could not find layer with tags: {a_tags}.");
		LayerMeta layer = layers[0];// null;
		//foreach(LayerMeta meta in layers)
		//{
		//	if (!meta.layer_tags.Contains("Invisible"))
		//	{
		//		layer = meta;
		//		break;
		//	}	
		//}
		if (layer == null)
			throw new Exception($"	Could not find layer with tags: {a_tags} that is not 'Invisible'.");
		Console.WriteLine($"	Found layer with ID={layer.layer_id}, Name={layer.layer_name}, GeoType={layer.layer_geotype}.");

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
		new NameValueCollection { { "layer_id", a_session.m_shoreLineMeta.layer_id.ToString() }, { "month", "-1" } }
	).ContinueWithOnSuccess(shoreGeometry =>
	{
		//TODO: Skip this step once raster's LayerMeta contains raster resolution
		List<SubEntityObject> shore = shoreGeometry.Result;
		if(shore == null || shore.Count == 0)
			throw new Exception($"	Shore line layer does not contain expected geometry.");
		else
			Console.WriteLine($"	Coast line geometry received ({shore.Count} lines).");

		return (shore, a_session.MSPClient.HttpPost<RasterRequestResponse>(
		   API_GET_RASTER,
		   new NameValueCollection { { "layer_name", a_session.m_sandDepthMeta.layer_name } }
		));
	}).ContinueWithOnSuccess(dataset =>
	{
		var (shoreLine, sandDepthRaster) = dataset.Result;

		CalculateDTSRasterInternal(a_session, shoreLine, sandDepthRaster.Result);
	});
}

void CalculateDTSRasterInternal(SimulationSession a_session, List<SubEntityObject> a_shoreLine, RasterRequestResponse a_sandDepthRaster)
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
			float minDistance = float.MaxValue;
			foreach(SubEntityObject line in a_shoreLine)
			{
				minDistance = Math.Min(minDistance, Util.PointDistanceFromLineString(originX + x * widthPerPixel, originY + y * heightPerPixel, line.geometry));
			}
			a_session.m_distanceToShoreRaster[x, y] = minDistance;
		}
	}
	Console.WriteLine($"	DTS raster calculated at resolution: {sdRaster.Width}x{sdRaster.Height}");
}


// Once the simulation state - the next month - is entered, this event will be triggered.
Task SessionSimulationStateEntered(SimulationSession session)
{
	//return Task.Run(async () =>
	//{
	//	List<SubEntityObject> pits = await session.MSPClient.HttpPost<List<SubEntityObject>>(API_GET_LAYER_VECTOR,
	//	   new NameValueCollection { { "layer_id", session.m_pitsMeta.layer_id.ToString() }, { "month", session.CurrentMonth.ToString() } } );
	//	RasterRequestResponse bathRaster = await session.MSPClient.HttpPost<RasterRequestResponse>(API_GET_RASTER,
	//	   new NameValueCollection { { "layer_name", session.m_bathymetryMeta.layer_name } } );
	//	RasterRequestResponse sdRaster = await session.MSPClient.HttpPost<RasterRequestResponse>(API_GET_RASTER,
	//	   new NameValueCollection { { "layer_name", session.m_sandDepthMeta.layer_name } });
	//	 RunSimulationMonth(session, bathRaster, sdRaster, pits);
	//});

	//Get pit geometry
	return session.MSPClient.HttpPost<List<SubEntityObject>>(
		   API_GET_LAYER_VECTOR,
		   new NameValueCollection { { "layer_id", session.m_pitsMeta.layer_id.ToString() }, { "month", session.CurrentSimMonth.ToString() } }
    ).ContinueWithOnSuccess(pitGeometry =>
	{
		//Get bathymetry raster
		return (pitGeometry, session.MSPClient.HttpPost<RasterRequestResponse>(
		   API_GET_RASTER,
		   new NameValueCollection { { "layer_name", session.m_bathymetryMeta.layer_name }, { "month", session.CurrentSimMonth.ToString() } }
		));
	}).ContinueWithOnSuccess(dataset =>
    {
		var (pitGeometry, bathymetryRaster) = dataset.Result;

		//Get sand depth raster
		return (pitGeometry, bathymetryRaster, session.MSPClient.HttpPost<RasterRequestResponse>(
           API_GET_RASTER,
           new NameValueCollection { { "layer_name", session.m_sandDepthMeta.layer_name }, { "month", session.CurrentSimMonth.ToString() } }
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
	 *		Per depth layer (1m):
	 *			Iterate through 'available sand depth' raster pixels that overlap the pit's bounds
	 *				Calculate sand volume extracted for pixel at depth layer (pit depth, available sand, overlap of pit with pixel)
	 *			Offset remaining pit contour to next depth layer
	 *		Iterate through 'available sand depth' raster pixels that overlap the pit's bounds
	 *			Update pixel with extracted amount
	 *			Calculate avg DTS per pixel, forming AVG DTS for pit
	 *		Iterate through 'bathymetry' raster pixels that overlap the pit's bounds
	 *			Determine the avg actual extraction depth for the pixel (using values calculated above)
	 *			Update pixel with extracted amount
	 * Calculate KPI for total extracted volume
	 * Send new rasters and KPIs to server
	 */

	Console.WriteLine($"====== Starting simulation for month {a_session.CurrentSimMonth}.");
	using Image<Rgba32> sdRaster = Image.Load < Rgba32 >(Convert.FromBase64String(a_sandDepthRaster.image_data));
	using Image<Rgba32> bathRaster = Image.Load < Rgba32 >(Convert.FromBase64String(a_bathymetryRaster.image_data));

	double monthlyExtractedVolume = 0d;
	double monthlyTotalDTS = 0d;

	//Raster dimensions
	double sdRasterRealWidth = a_sandDepthRaster.displayed_bounds[1][0] - a_sandDepthRaster.displayed_bounds[0][0];
	double sdRasterRealHeight = a_sandDepthRaster.displayed_bounds[1][1] - a_sandDepthRaster.displayed_bounds[0][1];
	double sdRealWidthPerPixel = sdRasterRealWidth / sdRaster.Width;
	double sdRealHeightPerPixel = sdRasterRealHeight / sdRaster.Height;
	double sdAreaPerPixel = sdRealWidthPerPixel * sdRealHeightPerPixel;
	double bathRasterRealWidth = a_bathymetryRaster.displayed_bounds[1][0] - a_bathymetryRaster.displayed_bounds[0][0];
	double bathRasterRealHeight = a_bathymetryRaster.displayed_bounds[1][1] - a_bathymetryRaster.displayed_bounds[0][1];
	double bathRealWidthPerPixel = bathRasterRealWidth / bathRaster.Width;
	double bathRealHeightPerPixel = bathRasterRealHeight / bathRaster.Height;

	foreach (SubEntityObject pit in a_pitGeometry)
	{
		if (pit.implementation_time != a_session.CurrentMonth)
		{
			Console.WriteLine($"Skipping old pit geometry with ID {pit.id}");
			continue;
		}
		Console.WriteLine($"	Simulating pit with ID={pit.id}.");

		double totalPitVolume = 0f;
		int pitDepth = 1;
		if (pit.data == null || !pit.data.TryGetValue("PitExtractionDepth", out string pitDepthStr) || !int.TryParse(pitDepthStr, out pitDepth))
		{
			Console.WriteLine($"		Missing pit depth, using default depth of 1m.");
		}
		int pitSlope = 1;
		if (pit.data == null || !pit.data.TryGetValue("PitSlope", out string pitSlopeStr) || !int.TryParse(pitSlopeStr, out pitSlope))
		{
			Console.WriteLine($"		Missing pit slope, using default slope of 1/1m (45).");
		}
		PathD fullPitPath = new PathD(pit.geometry.Length);
		//PathD currentPitPath = new PathD(pit.geometry.Length);
		foreach (float[] point in pit.geometry)
		{
			fullPitPath.Add(new PointD(point[0], point[1]));
			//currentPitPath.Add(new PointD(point[0], point[1]));
		}
		double pitArea = Util.GetPolygonArea(fullPitPath);
		PathsD currentPitPath = new PathsD();
		currentPitPath.Add(fullPitPath);

		//Determine pit bounding box
		double pitXMin = float.PositiveInfinity;
		double pitYMin = float.PositiveInfinity;
		double pitXMax = float.NegativeInfinity;
		double pitYMax = float.NegativeInfinity;
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

		//Track current pit iteration process
		bool[,] pixelComplete = new bool[sdEndX - sdStartX, sdEndY - sdStartY];
		int activePixels = (sdEndX - sdStartX) * (sdEndY - sdStartY);
		int currentDepth = 0;

		//Actual extraction depth used to update the bathymetry after the sd raster has been updated
		double[,] volumePerPixel = new double[sdEndX - sdStartX, sdEndY - sdStartY];
		double averageDTS = 0;

		while (activePixels > 0)
		{
			currentDepth += 1;
			if (currentDepth > pitDepth)
				break;

			double volumeAtDepth = 0d;

			//Iterate over sanddepth pixel overlapping pit boundingbox
			for (int x = sdStartX; x < sdEndX; x++)
			{
				if (activePixels == 0)
					break;

				for (int y = sdStartY; y < sdEndY; y++)
				{
					if (pixelComplete[x - sdStartX, y - sdStartY])
						continue;

					float rasterDepth = GetSandDepthForRaster(sdRaster[x, y].R, a_session);

					RectD pixelRect = new RectD(
						a_sandDepthRaster.displayed_bounds[0][0] + x * sdRealWidthPerPixel,
						a_sandDepthRaster.displayed_bounds[0][1] + (y + 1) * sdRealHeightPerPixel,
						a_sandDepthRaster.displayed_bounds[0][0] + (x + 1) * sdRealWidthPerPixel,
						a_sandDepthRaster.displayed_bounds[0][1] + y * sdRealHeightPerPixel);
					pixelRect.Height = sdRealHeightPerPixel; //Height of the rect is not correct by default, this fixes

					double overlapArea = Util.GetPixelPolygonOverlapArea(pixelRect, currentPitPath); //Rect overlap is faster than poly

					if (overlapArea <= 0d)
					{
						//No overlap with pit at current depth, no volume and can be skipped
						//Cutting from current pit path not needed as it won't affect the slope
						pixelComplete[x - sdStartX, y - sdStartY] = true;
						activePixels--;
						continue;
					}

					if (rasterDepth < currentDepth)
					{
						//Pit depth does not cover the full 1m of the current layer, calculate remaining volume
						double volume = overlapArea * (1f - (currentDepth - rasterDepth));
						totalPitVolume += volume;
						volumeAtDepth += volume;
						volumePerPixel[x - sdStartX, y - sdStartY] += volume;
						pixelComplete[x - sdStartX, y - sdStartY] = true;
						activePixels--;
						//Then cut out of current pit path, so its correctly used for slopes
						PathD pixelPath = new PathD()
							{
								new PointD(a_sandDepthRaster.displayed_bounds[0][0] + x * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + y * sdRealHeightPerPixel),
								new PointD(a_sandDepthRaster.displayed_bounds[0][0] + x * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + (y + 1) * sdRealHeightPerPixel),
								new PointD(a_sandDepthRaster.displayed_bounds[0][0] + (x + 1) * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + (y + 1) * sdRealHeightPerPixel),
								new PointD(a_sandDepthRaster.displayed_bounds[0][0] + (x + 1) * sdRealWidthPerPixel, a_sandDepthRaster.displayed_bounds[0][1] + y * sdRealHeightPerPixel)
							};
						currentPitPath = Util.ClipFromPolygon(currentPitPath, pixelPath);
					}
					else
					{
						//Remaining pixel area is deeper then 1m slice, add full 1m overlap area
						totalPitVolume += overlapArea;
						volumeAtDepth += overlapArea;
						volumePerPixel[x - sdStartX, y - sdStartY] += overlapArea;
					}
				}
			}
			Console.WriteLine($"		Volume extracted at depth {currentDepth}: {volumeAtDepth} m3.");
			//Offset current pit path by slope amount to create next layer
			currentPitPath = Util.OffsetPolygon(currentPitPath, pitSlope);
			if (currentPitPath == null || currentPitPath.Count == 0)
			{
				break;
			}
		}

		//Update SD raster and get pit's average DTS
		for (int x = sdStartX; x < sdEndX; x++)
		{
			for (int y = sdStartY; y < sdEndY; y++)
			{
				double oldDepth = GetSandDepthForRaster(sdRaster[x, y].R, a_session);
				sdRaster[x, y] = new Rgba32(GetSandValueForDepth(oldDepth - volumePerPixel[x - sdStartX, y - sdStartY] / sdAreaPerPixel, a_session), 0, 0);
				averageDTS += volumePerPixel[x-sdStartX, y-sdStartY] / totalPitVolume * a_session.m_distanceToShoreRaster[x, y];
			}
		}

		monthlyExtractedVolume += totalPitVolume;
		monthlyTotalDTS += totalPitVolume * averageDTS;
		Console.WriteLine($"	Total volume for pit with ID={pit.id}: {totalPitVolume}");

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
				RectD bathPixelRect = new RectD(
					a_bathymetryRaster.displayed_bounds[0][0] + x * bathRealWidthPerPixel,
					a_bathymetryRaster.displayed_bounds[0][1] + (y + 1) * bathRealHeightPerPixel,
					a_bathymetryRaster.displayed_bounds[0][0] + (x + 1) * bathRealWidthPerPixel,
					a_bathymetryRaster.displayed_bounds[0][1] + y * bathRealHeightPerPixel);

				PathsD  bathPitOverlap = Util.GetPixelPolygonOverlap(bathPixelRect, fullPitPath);
				double bathPitOverlapArea = Util.GetPolygonArea(bathPitOverlap);
				if (bathPitOverlapArea < 0.001f)
					continue;
				double bathPixelArea = bathRealWidthPerPixel * bathRealHeightPerPixel; //Area of bath pixel
				double coverageFraction = bathPitOverlapArea / bathPixelArea; //Fraction of bath pixel covered by pit

				//Find average extraction depth of the pit in bathymetry pixel 
				double avgExtractionDepth = 0f;
				//Min and max pixel coordinates of this bath pixel on the sd raster
				int depthStartX = Math.Max(sdStartX, (int)Math.Floor((bathPixelRect.left - a_sandDepthRaster.displayed_bounds[0][0]) / sdRasterRealWidth * sdRaster.Width));
				int depthStartY = Math.Max(sdStartY, (int)Math.Floor((bathPixelRect.bottom - a_sandDepthRaster.displayed_bounds[0][1]) / sdRasterRealHeight * sdRaster.Height));
				int depthEndX = Math.Min(sdEndX, (int)Math.Ceiling((bathPixelRect.right - a_sandDepthRaster.displayed_bounds[0][0]) / sdRasterRealWidth * sdRaster.Width));
				int depthEndY = Math.Min(sdEndY, (int)Math.Ceiling((bathPixelRect.top - a_sandDepthRaster.displayed_bounds[0][1]) / sdRasterRealHeight * sdRaster.Height));
				for (int depthX = depthStartX; depthX < depthEndX; depthX++)
				{
					for (int depthY = depthStartY; depthY < depthEndY; depthY++)
					{
						RectD sdPixelRect = new RectD(
							a_sandDepthRaster.displayed_bounds[0][0] + depthX * sdRealWidthPerPixel,
							a_sandDepthRaster.displayed_bounds[0][1] + (depthY + 1) * sdRealHeightPerPixel,
							a_sandDepthRaster.displayed_bounds[0][0] + (depthX + 1) * sdRealWidthPerPixel,
							a_sandDepthRaster.displayed_bounds[0][1] + depthY * sdRealHeightPerPixel);
						//Find overlap of all 3 (pit∪bath∪sd)
						double threeOverlapArea = Util.GetPolygonArea(Util.GetPixelPolygonOverlap(sdPixelRect, bathPitOverlap));
						//Add pre-normalized extraction depth to bath pixel's avg by multiplying with (bath∪pit) coverage fraction
						avgExtractionDepth += threeOverlapArea / bathPitOverlapArea * volumePerPixel[x - sdStartX, y - sdStartY] / sdAreaPerPixel;
					}
				}

				//Update bathymetry raster with avg extraction depth on pixel multiplied by coverage
				float newDepth = GetBathymeteryDepthForRaster(bathRaster[x, y].R, a_session) - (float)(avgExtractionDepth * coverageFraction);
				bathRaster[x, y] = new Rgba32(GetBathymeteryValueForDepth(newDepth, a_session), 0, 0);
			}
		}
	}

	//Write new depth raster
	using MemoryStream stream = new(16384);
	sdRaster.Save(stream, new PngEncoder());
	a_session.m_newSandDepthRaster = Convert.ToBase64String(stream.ToArray());
	stream.Dispose();
	sdRaster.Dispose();

	//Write new bathymetry raster
	using MemoryStream stream2 = new(16384);
	bathRaster.Save(stream2, new PngEncoder());
	a_session.m_newBathymetryRaster = Convert.ToBase64String(stream2.ToArray());
	stream2.Dispose();
	bathRaster.Dispose();

	double uxoLossFactor = (new Random().NextDouble() * 5d + 10d) / 100d;
	double uxoLossVolume = monthlyExtractedVolume * uxoLossFactor;
	monthlyExtractedVolume -= uxoLossVolume;

	a_session.m_totalDTS += monthlyTotalDTS;
	a_session.m_totalExtractedVolume += monthlyExtractedVolume;
	double monthlyAVGDTS = 0d;
	if(monthlyExtractedVolume > 0d)
	{
		monthlyAVGDTS = monthlyTotalDTS / monthlyExtractedVolume;
	}

	//Set extraction KPIs
	a_session.m_kpis = new List<KPI>() { 
		new KPI() {
			name = "Monthly Volume",
			type = "SandExtraction",
			value = monthlyExtractedVolume,
			unit = "m3",
			month = a_session.CurrentMonth,
			country = -1 // for now, the server only supports showing non-country specific external KPIs
        },
		new KPI() {
			name = "Cumulative Volume",
			type = "SandExtraction",
			value = a_session.m_totalExtractedVolume,
			unit = "m3",
			month = a_session.CurrentMonth,
			country = -1 
        },
		new KPI() {
			name = "Monthly AVG DTS",
			type = "SandExtraction",
			value = monthlyAVGDTS,
			unit = "m",
			month = a_session.CurrentMonth,
			country = -1 
        },
		new KPI() {
			name = "Monthly Loss",
			type = "SandExtraction",
			value = uxoLossVolume,
			unit = "m3",
			month = a_session.CurrentMonth,
			country = -1
		}
		//,
		//new KPI() {
		//	name = "Total DTS",
		//	type = "EXTERNAL",
		//	value = a_session.m_totalDTS,
		//	unit = "km",
		//	month = a_session.CurrentMonth,
		//	country = -1 
  //      }
	};
	Console.WriteLine($"====== Simulation completed for month {a_session.CurrentSimMonth}, {a_pitGeometry.Count} new pits simulated.");
}

float GetBathymeteryDepthForRaster(byte a_value, SimulationSession a_session)
{
	return a_session.m_bathymetryMeta.scale.PixelToValue(a_value);
}

byte GetBathymeteryValueForDepth(float a_depth, SimulationSession a_session)
{
	return a_session.m_bathymetryMeta.scale.ValueToPixel(a_depth);
}

float GetSandDepthForRaster(byte a_value, SimulationSession a_session)
{
	return a_session.m_sandDepthMeta.scale.PixelToValue(a_value);
}

byte GetSandValueForDepth(double a_depth, SimulationSession a_session)
{
	return a_session.m_sandDepthMeta.scale.ValueToPixel((float)a_depth);
}