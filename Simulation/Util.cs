using System;
using Clipper2Lib;
using System.Numerics;

namespace MSPChallenge_Simulation.Simulation
{
	public static class Util
	{
		public static PathsD GetPixelPolygonOverlap(RectD a_pixel, PathsD a_polygon)
		{
			return Clipper.RectClip(a_pixel, a_polygon);
		}

		public static PathsD GetPixelPolygonOverlap(RectD a_pixel, PathD a_polygon)
		{		
			return Clipper.RectClip(a_pixel, a_polygon);
		}

		public static double GetPixelPolygonOverlapArea(RectD a_pixel, PathD a_polygon)
		{
			return GetPolygonArea(Clipper.RectClip(a_pixel, a_polygon));
		}

		public static float GetRectangleOverlapArea(float[,] a_rectA, float[,] a_rectB)
		{
			//Good explanation here: https://stackoverflow.com/questions/9324339/how-much-do-two-rectangles-overlap
			return Math.Max(0, Math.Min(a_rectA[2, 0], a_rectB[2, 0]) - Math.Max(a_rectA[0, 0], a_rectB[0, 0])) *
				Math.Max(0, Math.Min(a_rectA[2, 1], a_rectB[2, 1]) - Math.Max(a_rectA[0, 1], a_rectB[0, 1]));
		}

		public static double GetPolygonArea(PathsD a_polygons)
		{
			double area = 0;
			foreach (PathD poly in a_polygons)
			{
				area += GetPolygonArea(poly);
			}
			return area;
		}

		public static double GetPolygonArea(PathD a_polygon)
		{
			double area = 0;
			for (int i = 0; i < a_polygon.Count; ++i)
			{
				int j = (i + 1) % a_polygon.Count;
				area += a_polygon[i].x * a_polygon[j].y - a_polygon[i].y * a_polygon[j].x;
			}
			return Math.Abs(area * 0.5d);
		}

		public static float GetSquaredDistanceToLine(Vector2 point, Vector2 a_lineStart, Vector2 a_lineEnd)
		{
			// Uses Vector2 to make use of LengthSquared and Dot functions
			// Algorithm based on first answer from http://stackoverflow.com/questions/849211/shortest-distance-between-a-point-and-a-line-segment
			float lineLengthSquared = (a_lineEnd - a_lineStart).LengthSquared();
			if (lineLengthSquared == 0f)
				return (point - a_lineStart).LengthSquared();
			float t = Math.Max(0, Math.Min(1, Vector2.Dot(point - a_lineStart, a_lineEnd - a_lineStart) / lineLengthSquared));
			Vector2 projection = a_lineStart + t * (a_lineEnd - a_lineStart);
			return (projection - point).LengthSquared();
		}

		public static float PointDistanceFromLineString(float a_pointX, float a_pointY, float[][] a_lineString)
		{
			float result = float.MaxValue;
			for (int i = 0; i < a_lineString.Length - 1; ++i)
				result = Math.Min(result, GetSquaredDistanceToLine(
					new Vector2(a_pointX, a_pointY),
					new Vector2(a_lineString[i][0], a_lineString[i][1]),
					new Vector2(a_lineString[i + 1][0], a_lineString[i + 1][1])));
			return result;
		}
	}
}


