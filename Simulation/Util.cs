using System;
using System.Numerics;

namespace MSPChallenge_Simulation.Simulation
{
	public static class Util
	{

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


