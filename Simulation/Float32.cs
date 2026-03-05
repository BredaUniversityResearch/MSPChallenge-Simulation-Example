using System;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

public struct Float32 : IPixel<Float32>
{
	const float scale = 500f;
	const float max = 250f;

	// Single float channel
	public float Value;

	public Float32(float value)
	{
		Value = value;
	}

	public PixelOperations<Float32> CreatePixelOperations()
	{
		return new PixelOperations<Float32>();
	}

	public void FromScaledVector4(Vector4 vector)
	{
		FromVector4(vector);
	}

	public Vector4 ToScaledVector4()
	{
		return ToVector4();
	}

	public void FromVector4(Vector4 vector)
	{
		Value = (vector[0] - 0.5f) * scale; 
	}

	public Vector4 ToVector4()
	{
		float scaled = Math.Clamp(Value / scale + 0.5f, 0f, 1f);
		return new Vector4(scaled, 0f, 0f, 1f);
	}

	public void FromArgb32(Argb32 source)
	{
		throw new NotImplementedException();
	}

	public void FromBgra5551(Bgra5551 source)
	{
		throw new NotImplementedException();
	}

	public void FromBgr24(Bgr24 source)
	{
		throw new NotImplementedException();
	}

	public void FromBgra32(Bgra32 source)
	{
		throw new NotImplementedException();
	}

	public void FromAbgr32(Abgr32 source)
	{
		throw new NotImplementedException();
	}

	public void FromL8(L8 source)
	{
		throw new NotImplementedException();
	}

	public void FromL16(L16 source)
	{
		throw new NotImplementedException();
	}

	public void FromLa16(La16 source)
	{
		throw new NotImplementedException();
	}

	public void FromLa32(La32 source)
	{
		throw new NotImplementedException();
	}

	public void FromRgb24(Rgb24 source)
	{
		throw new NotImplementedException();
	}

	public void FromRgba32(Rgba32 source)
	{
		throw new NotImplementedException();
	}

	public void ToRgba32(ref Rgba32 dest)
	{
		throw new NotImplementedException();
	}

	public void FromRgb48(Rgb48 source)
	{
		throw new NotImplementedException();
	}

	public void FromRgba64(Rgba64 source)
	{
		throw new NotImplementedException();
	}

	public bool Equals(Float32 other)
	{
		return Value.Equals(other.Value);
	}
}