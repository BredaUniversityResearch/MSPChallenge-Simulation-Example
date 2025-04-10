
using DotNetEnv;
using System.ComponentModel;

namespace MSPChallenge_Simulation.Communication.DataModel;

public class ScaleConfig
{
	public enum DensitymapInterpolation { Lin, Log, Quad, LinGrouped }

	public float min_value;
	public float max_value;
	public DensitymapInterpolation interpolation;
	public ScaleConfigGrouping[] groups;

	public float PixelToValue(byte a_pixelValue)
	{
		float p_norm = a_pixelValue / 256f;
		if (interpolation == DensitymapInterpolation.Log)
			return (max_value - min_value) * p_norm * p_norm + min_value;
		else if (interpolation == DensitymapInterpolation.Quad)
		{
			float inv = 1f - p_norm;
			return (max_value - min_value) * (1f - inv * inv) + min_value;
		}
		else if (interpolation == DensitymapInterpolation.LinGrouped)
		{
			for (int i = groups.Length - 2; i >= 0; i--)
			{
				if (p_norm >= groups[i].normalised_input_value)
				{
					float remappedT = (p_norm - groups[i].normalised_input_value) / (groups[i + 1].normalised_input_value - groups[i].normalised_input_value);
					return groups[i].min_output_value + remappedT * (groups[i + 1].min_output_value - groups[i].min_output_value);
				}
			}
			return groups[0].min_output_value;
		}
		return (max_value - min_value) * p_norm + min_value;
	}

	public byte ValueToPixel(float a_value)
	{
		float clamped = Math.Clamp(a_value, min_value, max_value);
		float normValue = 0;
		if (interpolation == DensitymapInterpolation.Log)
		{
			bool negative = clamped < 0;
			normValue = (clamped - min_value) / (max_value - min_value);
			normValue = 1f - normValue * normValue;
			if (negative)
				normValue = -normValue;
		}
		else if (interpolation == DensitymapInterpolation.Quad)
		{
			normValue = (clamped - min_value) / (max_value - min_value);
			normValue = normValue * normValue;
		}
		else if (interpolation == DensitymapInterpolation.LinGrouped)
		{
			for (int i = groups.Length - 2; i >= 0; i--)
			{
				if (clamped >= groups[i].min_output_value)
				{
					float remappedT = (clamped - groups[i].min_output_value) / (groups[i + 1].min_output_value - groups[i].min_output_value);
					normValue = groups[i].normalised_input_value + remappedT * (groups[i + 1].normalised_input_value - groups[i].normalised_input_value);
				}
			}
			return 0;
		}
		else
		{
			normValue = (clamped - min_value) / (max_value - min_value);
		}
		return (byte)(normValue * 256f);
	}

	public float EvaluateT(float a_t)
	{
		if (interpolation == DensitymapInterpolation.Log)
			return a_t * a_t;
		else if (interpolation == DensitymapInterpolation.Quad)
		{
			float inv = 1f - a_t;
			return 1f - inv * inv;
		}
		else if (interpolation == DensitymapInterpolation.LinGrouped)
		{
			for (int i = groups.Length - 2; i >= 0; i--)
			{
				if (a_t >= groups[i].normalised_input_value)
				{
					float remappedT = (a_t - groups[i].normalised_input_value) / (groups[i + 1].normalised_input_value - groups[i].normalised_input_value);
					float output = groups[i].min_output_value + remappedT * (groups[i + 1].min_output_value - groups[i].min_output_value);
					return (output - min_value) / (max_value - min_value);
				}
			}
			return 0f;
		}
		return a_t;
	}
}

public class ScaleConfigGrouping
{
	public float normalised_input_value;
	public float min_output_value;
}