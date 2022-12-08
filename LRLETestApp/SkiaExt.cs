using System;
using System.IO;

namespace LRLETestApp
{
	public static class SkiaExt
	{
		public static void Save(this SkiaSharp.SKBitmap bmp, string path)
		{
			using var fs = File.Create(path);
			bmp.Encode(fs, SkiaSharp.SKEncodedImageFormat.Png, 100);
		}
	}
}

