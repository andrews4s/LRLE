using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LRLE;
using Microsoft.Extensions.Configuration;
namespace LREParser
{
    class MainClass
    {
        static string outDir = "out";
        static bool logText = false;
        static bool roundtrip = true;
        static bool saveMips = true;
        static int maxMips = 10;
        public static void Main(string[] args)
        {
            var conf = new ConfigurationBuilder()
                .AddCommandLine(args)
                .Build();
            outDir = conf[nameof(outDir)] ?? "out";
            maxMips = int.Parse(conf[nameof(maxMips)] ?? "10");
            logText = (conf[nameof(logText)] ?? "false").ToLower() == "true";
            roundtrip = (conf[nameof(roundtrip)] ?? "true").ToLower() == "true";
            saveMips = (conf[nameof(saveMips)] ?? "true").ToLower() == "true";
            var f = args.Length == 0 ?
                Path.GetDirectoryName(typeof(MainClass).Assembly.Location) :
                args[0];
            if (!Directory.Exists(f))
            {
                Console.WriteLine($"Directory '{f}' does not exist.");
                return;
            }
            var files = Directory.EnumerateFiles(f, "*.lrle").ToArray();
            if (!files.Any())
            {
                Console.WriteLine($"No .lrle files found in {f}");
                return;
            }

            var o = Path.Combine(f, outDir);
            if (Directory.Exists(o)) Directory.Delete(o, true);
            Directory.CreateDirectory(o);
            using (new ConsoleStopWatch("All files"))
            {
                foreach (var lrle in files)
                {
                    using (new ConsoleStopWatch(Path.GetFileName(lrle)))
                    {
                        ProcessLRLE(lrle, Path.Combine(o, Path.GetFileName(lrle)), roundtrip);
                    }
                }
            }
        }

        static string FormatColor(byte[] ci) => FormatColor(BitConverter.ToUInt32(ci, 0));
        static string FormatColor(int c) => FormatColor((uint)c);
        static string FormatColor(uint c) => $"#{c & 0x00FFFFFF:X6}{(c >> 24):X2}";

        static byte[] GetPixelData(Bitmap bmp)
        {
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var buffer = new byte[bmp.Width * bmp.Height << 2];
            Marshal.Copy(bits.Scan0, buffer, 0, buffer.Length);
            bmp.UnlockBits(bits);
            return buffer;
        }
        private static void ProcessLRLE(string inputFile, string outputFile, bool convertBack)
        {
            using (var fs = File.OpenRead(inputFile))
            {
                var lrleReader = LRLEUtility.GetReader(fs);
                var lrleWriter = LRLEUtility.GetWriter();
                if (logText) WritePaletteText(outputFile, lrleReader);
                foreach (var mip in lrleReader.Read().Take(maxMips))
                {
                    var bmp = ExtractMipMapData(outputFile, mip);
                    if (convertBack)
                        lrleWriter.AddMip(mip.Width, mip.Height, GetPixelData(bmp));
                }
                if (convertBack)
                {
                    var outFile = outputFile + $".out";
                    //Re-encode the pixel runs as a new lrle file
                    ReEncode(lrleWriter, outFile);
                    //Attempt to re-parse the newly written lrle file
                    ReDecode(outFile);
                }
            }
        }

        private static void ReEncode(LRLEUtility.Writer lrleWriter, string outFile)
        {
            using (var rt = File.Create(outFile))
            {
                lrleWriter.Write(rt);
            }
        }
        private static void ReDecode(string outFile)
        {
            try
            {
                ProcessLRLE(outFile, outFile, false);
                Debug.WriteLine($"Re-parsed {outFile}!");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Failed to re-parse {outFile}\n{e}");
            }
        }


        private static Bitmap ExtractMipMapData(string outputFile, LRLEUtility.Reader.Mip mip)
        {
            byte[] color = null;
            StreamWriter mipText = null;
            if (logText) mipText = File.CreateText(outputFile + $".mip{mip.Index}.txt");


            var bmp = new Bitmap(mip.Width, mip.Height);
            var bits = bmp.LockBits(new Rectangle(0, 0, mip.Width, mip.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (logText)
            {
                mipText.WriteLine($"{mip.Start:X16} - {mip.End:X16}");
                mipText.WriteLine($"Mip [{mip.Index}] {mip.Width}x{mip.Height}={(mip.Width * mip.Height):X8}|{mip.Width * mip.Height} ({mip.Length})");
            }
            var pixels = new byte[mip.Width * mip.Height << 2];
            int pixelsWritten = 0;
            foreach (var chunk in mip.Read())
            {
                foreach (var run in chunk.Runs)
                {
                    if (saveMips) color = BitConverter.GetBytes(run.Color);
                    if (logText) mipText.WriteLine($"{Convert.ToString(chunk.CommandByte & 7, 2).PadLeft(3, '0')} {FormatColor(color)} * {run.Length} {Convert.ToString(run.Length, 2)}");
                    if (saveMips) for (int j = 0; j < run.Length; j++) Array.Copy(color, 0, pixels, LRLEUtility.BlockIndexToScanlineIndex(pixelsWritten++, mip.Width) << 2, 4);
                    if (logText) mipText.Flush();
                }
            }
            if (logText) mipText.Close();
            if (saveMips)
            {
                Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
                bmp.UnlockBits(bits);
            }
            if (saveMips) bmp.Save(outputFile + $".mip{mip.Index}.png");
            return bmp;

        }
        class ConsoleStopWatch : IDisposable
        {
            private readonly string subject;
            private readonly Stopwatch stopwatch;

            public ConsoleStopWatch(string subject)
            {
                this.subject = subject;
                this.stopwatch = new Stopwatch();
                this.stopwatch.Start();
            }

            public void Dispose()
            {
                this.Stop();
            }

            private void Stop()
            {
                if (this.stopwatch.IsRunning)
                {
                    this.stopwatch.Stop();
                }
                Debug.WriteLine($"{subject} finished in {(stopwatch.ElapsedMilliseconds) / 1000.0} s");
            }
        }

        private static void WritePaletteText(string outputFile, LRLEUtility.Reader lrleReader)
        {
            if (lrleReader.Palette.Any())
            {
                using (var paletteTextWriter = File.CreateText(outputFile + ".palette.txt"))
                {
                    for (int cc = 0; cc < lrleReader.Palette.Length; cc++)
                    {
                        paletteTextWriter.WriteLine($"{cc}\t{cc.ToString("X4")}\t{FormatColor(lrleReader.Palette[cc])}");
                    }
                }
            }
        }
    }
}


