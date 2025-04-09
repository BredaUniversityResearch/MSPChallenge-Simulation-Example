using System;
using ClipperLib;
using System.Numerics;

namespace MSPChallenge_Simulation.Simulation
{
	public static class Util
	{
		const float INT_CONVERSION = 100000000000000.0f;


		public static float[,] GetPolygonOverlap(float[][] a_polygon1, float[,] a_polygon2)
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
			return new float[0, 0];
		}

		public static float GetRectangleOverlapArea(float[,] a_rectA, float[,] a_rectB)
		{
			//Good explanation here: https://stackoverflow.com/questions/9324339/how-much-do-two-rectangles-overlap
			return Math.Max(0, Math.Min(a_rectA[2, 0], a_rectB[2, 0]) - Math.Max(a_rectA[0, 0], a_rectB[0, 0])) *
				Math.Max(0, Math.Min(a_rectA[2, 1], a_rectB[2, 1]) - Math.Max(a_rectA[0, 1], a_rectB[0, 1]));
		}

		public static float GetPolygonOverlapArea(float[][] a_polygon1, float[,] a_polygon2)
		{
			return GetPolygonArea(GetPolygonOverlap(a_polygon1, a_polygon2));
		}

		public static float GetPolygonArea(float[,] polygon)
		{
			float area = 0;
			for (int i = 0; i < polygon.Length; ++i)
			{
				int j = (i + 1) % polygon.Length;
				area += polygon[i, 0] * polygon[j, 1] - polygon[i, 1] * polygon[j, 0];
			}
			return Math.Abs(area * 0.5f);
		}

		public static float GetPolygonAreaJagged(float[][] polygon)
		{
			float area = 0;
			for (int i = 0; i < polygon.Length; ++i)
			{
				int j = (i + 1) % polygon.Length;
				area += polygon[i][0] * polygon[j][1] - polygon[i][1] * polygon[j][0];
			}
			return Math.Abs(area * 0.5f);
		}

		public static float[,] IntPointToVector(List<IntPoint> points)
		{
			float[,] verts = new float[points.Count, 2];

			for (int i = 0; i < points.Count; i++)
			{
				verts[i, 0] = points[i].X / INT_CONVERSION;
				verts[i, 1] = points[i].Y / INT_CONVERSION;
			}

			return verts;
		}

		public static List<IntPoint> FloatSparseToIntPoint(float[][] points)
		{
			List<IntPoint> verts = new List<IntPoint>();

			for (int i = 0; i < points.Length; i++)
			{
				verts.Add(new IntPoint(points[i][0] * INT_CONVERSION, points[i][1] * INT_CONVERSION));
			}

			return verts;
		}

		public static List<IntPoint> Float2DToIntPoint(float[,] points)
		{
			List<IntPoint> verts = new List<IntPoint>();

			for (int i = 0; i < points.Length; i++)
			{
				verts.Add(new IntPoint(points[i, 0] * INT_CONVERSION, points[i, 1] * INT_CONVERSION));
			}

			return verts;
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


