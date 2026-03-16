using System;
using Clipper2Lib;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

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

		public static double GetPixelPolygonOverlapArea(RectD a_pixel, PathsD a_polygon)
		{
			return GetPolygonArea(Clipper.RectClip(a_pixel, a_polygon));
		}

		public static PathsD OffsetPolygon(PathsD a_polygon1, double a_offset)
		{
			return Clipper.InflatePaths(a_polygon1, a_offset, JoinType.Square, EndType.Polygon);
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

		public static PathsD ClipFromPolygon(PathsD a_polygon1, PathD a_clip)
		{
			PathsD csolution = new PathsD();
			ClipperD co = new ClipperD();
			co.AddSubject(a_polygon1);
			co.AddClip(a_clip);
			co.Execute(ClipType.Difference, FillRule.EvenOdd, csolution);
			return csolution;
		}

		public static double GetPolygonPerimeter(PathD a_polygon)
		{
			double x = a_polygon[0].x - a_polygon[a_polygon.Count-1].x;
			double y = a_polygon[0].y - a_polygon[a_polygon.Count-1].y;
			double result = Math.Sqrt(x * x + y * y);
			for(int i = 1; i < a_polygon.Count; i++)
			{
				x = a_polygon[i].x - a_polygon[i-1].x;
				y = a_polygon[i].y - a_polygon[i-1].y;
				result += Math.Sqrt(x * x + y * y);
			}
			return result;
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

		public static double GetSquaredDistanceToLine2(float px, float py, float lx1, float ly1, float lx2, float ly2)
		{
			double A = px - lx1;
			double B = py - ly1;
			double C = lx2 - lx1;
			double D = ly2 - ly1;

			double dot = A * C + B * D;
			double len_sq = C * C + D * D;
			double param = -1f;
			if (len_sq != 0f) //in case of 0 length line
				param = dot / len_sq;

			double xx, yy;

			if (param < 0f)
			{
				xx = lx1;
				yy = ly1;
			}
			else if (param > 1f)
			{
				xx = lx2;
				yy = ly2;
			}
			else
			{
				xx = lx1 + param * C;
				yy = ly1 + param * D;
			}

			double dx = px - xx;
			double dy = py - yy;
			return dx * dx + dy * dy; //Still squared
		}

		public static float PointDistanceFromLineString(float a_pointX, float a_pointY, float[][] a_lineString)
		{
			double result = double.MaxValue;
			//for (int i = 0; i < a_lineString.Length - 1; ++i)
			//	result = Math.Min(result, GetSquaredDistanceToLine(
			//		new Vector2(a_pointX, a_pointY),
			//		new Vector2(a_lineString[i][0], a_lineString[i][1]),
			//		new Vector2(a_lineString[i + 1][0], a_lineString[i + 1][1])));

			for (int i = 0; i < a_lineString.Length - 1; ++i)
				result = Math.Min(result, GetSquaredDistanceToLine2(a_pointX, a_pointY, a_lineString[i][0], a_lineString[i][1], a_lineString[i + 1][0], a_lineString[i + 1][1]));

			return (float)Math.Sqrt(result);
		}

		public static void LogAppLevel(string a_text)
		{
			Console.WriteLine(a_text);
		}

		public static void LogSessionLevel(string a_text)
		{
			Console.WriteLine("   " + a_text);
		}

		public static void LogSimLevel0(string a_text)
		{
			Console.WriteLine("      " + a_text);
		}

		public static void LogSimLevel1(string a_text)
		{
			Console.WriteLine("         " + a_text);
		}

		public static void LogSimLevel2(string a_text)
		{
			Console.WriteLine("            " + a_text);
		}

		public static void LogSimLevel3(string a_text)
		{
			Console.WriteLine("               " + a_text);
		}
	}
}


