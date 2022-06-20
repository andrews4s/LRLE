using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LRLE.UnitTests")]
namespace LRLE
{
    public static class LRLEUtility
    {
        public const uint LRLE = 0x454C524C;
        public enum LRLEFormat
        {
            Default = 0,
            V002 = 842018902
        }
        public class Writer
        {
            internal readonly struct PixelRun
            {
                public PixelRun(int c, int l) { Color = c; Length = l; }
                public readonly int Color;
                public readonly int Length;
                public override string ToString() { return $"{Color:X8} x {Length}"; }
            }
            public class Mip
            {
                internal List<PixelRun[]> Runs { get; set; } = new List<PixelRun[]>();
                public int Width { get; set; }
                public int Height { get; set; }
            }
            public List<Mip> Mips { get; set; } = new List<Mip>();
            /// <summary>
            /// 0 will allow inline pixels in all mip levels. Otherwise it forces mip maps below that level to use the palette.
            /// </summary>
            public int MinInlinePixelMipLevel { get; set; } = 0;
            /// <summary>
            /// When forcing a pixel into the palette, this is the base ARGB distance allowed
            /// </summary>
            public int PaletteMatchBaseDifference { get; set; } = 10;
            /// <summary>
            /// When forcing a pixel into the palette, this is the increased ARGB distance allowed per mip level.
            /// </summary>
            public int PaletteMatchDifferencePerMipLevel { get; set; } = 8;

            readonly IDictionary<int, int> paletteOccurrences = new Dictionary<int, int>(ushort.MaxValue);
            private List<int> paletteArray = new List<int>();
            private Dictionary<int, int> palette = new Dictionary<int, int>();

            public void AddMip(int width, int height, byte[] rawARGBPixelData)
            {
                this.Mips.Add(new Mip { Width = width, Height = height, Runs = ExtractPixelRuns(rawARGBPixelData, width, height) });
                //Determine if settings allow this mip level to use inline pixels.
                var disallowInlinePixel = this.Mips.Count <= this.MinInlinePixelMipLevel;
                //Determine the maximum difference allowed for a palette match at thi mip level.
                var maxDiff = this.PaletteMatchBaseDifference + (PaletteMatchDifferencePerMipLevel * Mips.Count);
                foreach (var colorOccurrence in paletteOccurrences.OrderByDescending(x => x.Value).ToArray()
                    )
                {
                    //If the palette is full and this mip level does not require a palette match, break.
                    if (!disallowInlinePixel && paletteArray.Count >= ushort.MaxValue)
                        break;
                    //If an exact match exists in the palette, move along.
                    if (palette.ContainsKey(colorOccurrence.Key))
                        continue;
                    //If there is room in the palette, add this color.
                    if (paletteArray.Count < ushort.MaxValue)
                    {
                        this.palette[colorOccurrence.Key] = paletteArray.Count;
                        this.paletteArray.Add(colorOccurrence.Key);
                        //Now that this color made it into the palette, we don't need to check it.
                        paletteOccurrences.Remove(colorOccurrence.Key);
                    }
                    else
                    {
                        //Find an appropriate color within the filled palette for this pixel at this mip level.
                        int newColor = -1;
                        int best = int.MaxValue;
                        int i = 0;
                        for (i = 0; i < paletteArray.Count; i++)
                        {

                            var c = paletteArray[i];
                            int diff = CompareColor(c, colorOccurrence.Key, best);
                            if (diff < best)
                            {
                                best = diff;
                                newColor = c;
                            }
                            if (best <= maxDiff)
                            {
                                break;
                            }
                        }
                        palette[colorOccurrence.Key] = palette[newColor];
                    }

                }
            }
            /// <summary>
            /// Determines how close 2 colors are.
            /// </summary>
            int CompareColor(int a, int b, int best)
            {
                var c1 = (int)(((a & 0xFF000000) >> 24) - ((b & 0xFF000000) >> 24));
                if (c1 < 0) c1 = -c1;
                if (c1 > best) return int.MaxValue;

                var c2 = ((a & 0x00FF0000) >> 16) - ((b & 0x00FF0000) >> 16);
                if (c2 < 0) c2 = -c2;
                if (c2 > best) return int.MaxValue;

                var c3 = ((a & 0x0000FF00) >> 8) - ((b & 0x0000FF00) >> 8);
                if (c3 < 0) c3 = -c3;
                if (c3 > best) return int.MaxValue;

                var c4 = (a & 0x000000FF) - (b & 0x000000FF);
                if (c4 < 0) c4 = -c4;

                return c1 + c2 + c3 + c4;
            }
            internal List<PixelRun[]> ExtractPixelRuns(byte[] argbPixels, int width, int height)
            {
                int lastColor = 0;
                int runLength = 0;
                var allRuns = new List<PixelRun[]>(0x80000);
                var runList = new PixelRun[255];
                PixelRun[] l;
                int listLength = 0;
                var pixels = argbPixels.Length >> 2;
                var block_row_size = width << 2;
                var width_log2 = Log2(width);
                unsafe
                {
                    fixed (byte* pixelPtr = &argbPixels[0])
                    {

                        int* argbPixelPtr = (int*)pixelPtr;
                        for (int i = 0; i < pixels; i++)
                        {
                            var index = BlockIndexToScanlineIndex(i, width, block_row_size, width_log2);

                            var c = *(argbPixelPtr + index);
                            if (paletteOccurrences.TryGetValue(c, out int cc))
                                paletteOccurrences[c] = cc + 1;
                            else
                                paletteOccurrences[c] = 1;
                            if (i == 0)
                            {
                                lastColor = c;
                                runLength++;
                            }
                            else if (c == lastColor)
                            {
                                runLength++;
                            }
                            else
                            {
                                if (runLength > 1)
                                {
                                    if (listLength > 0)
                                    {
                                        l = new PixelRun[listLength];
                                        Array.Copy(runList, l, l.Length);
                                        listLength = 0;
                                        allRuns.Add(l);
                                    }
                                    allRuns.Add(new[] { new PixelRun(lastColor, runLength) });
                                }
                                else
                                {
                                    if (runList.Length <= listLength)
                                    {
                                        PixelRun[] newRunList = new PixelRun[runList.Length << 1];
                                        Array.Copy(runList, newRunList, runList.Length);
                                        runList = newRunList;
                                    }
                                    runList[listLength++] = new PixelRun(lastColor, 1);
                                }
                                lastColor = c;
                                runLength = 1;
                            }
                        }
                    }
                }
                if (listLength > 0)
                {
                    l = new PixelRun[listLength];
                    Array.Copy(runList, l, l.Length);
                    allRuns.Add(l);
                }
                if (runLength > 1)
                {
                    allRuns.Add(new[] { new PixelRun(lastColor, runLength) });
                }
                return allRuns;
            }
            public void Write(Stream fs)
            {
                var s = new BinaryWriter(fs);
                var mipCount = this.Mips.Count;
                var headerSize = 16 + (mipCount << 2);
                s.Seek(headerSize, SeekOrigin.Begin);
                s.Write(paletteArray.Count);
                var cmdOffsets = new uint[mipCount];
                for (int i = 0; i < paletteArray.Count; i++)
                {
                    int p = paletteArray[i];
                    s.Write(p);
                }
                var start = fs.Position;
                for (int mipMapIndex = 0; mipMapIndex < Mips.Count; mipMapIndex++)
                {
                    Mip mip = Mips[mipMapIndex];
                    cmdOffsets[mipMapIndex] = (uint)(fs.Position - start);
                    for (int runIndex = 0; runIndex < mip.Runs.Count; runIndex++)
                    {
                        PixelRun[] runList = mip.Runs[runIndex];
                        if (runList.Length == 1)
                            WritePixelRepeat(s, palette, runList[0]);
                        else
                            WritePixelSequence(s, palette, runList);
                    }
                }
                fs.Position = 0;
                s.Write(LRLEUtility.LRLE);
                s.Write((uint)LRLEFormat.V002);
                s.Write((ushort)Mips[0].Width);
                s.Write((ushort)Mips[0].Height);
                s.Write(Mips.Count);
                for (int cmdOffsetIndex = 0; cmdOffsetIndex < cmdOffsets.Length; cmdOffsetIndex++)
                    s.Write(cmdOffsets[cmdOffsetIndex]);
                fs.Seek(0, SeekOrigin.End);
            }

            private static void WritePixelRepeat(BinaryWriter s, Dictionary<int, int> palette, in PixelRun run)
            {
                int flag;
                if (palette.TryGetValue(run.Color, out int paletteIndex))
                {
                    flag = paletteIndex <= byte.MaxValue ? 1 : 2;
                }
                else
                {
                    flag = 3;
                }
                WritePackedInt(run.Length, flag << 1, 3, s);
                switch (flag)
                {
                    case 1:
                        s.Write((byte)paletteIndex);
                        break;
                    case 2:
                        s.Write((ushort)paletteIndex);
                        break;
                    case 3:
                        s.Write(run.Color);
                        break;
                    default:
                        throw new Exception($"Unknown flags {flag}");
                }
            }

            private static void WritePixelSequence(BinaryWriter s, Dictionary<int, int> palette, PixelRun[] runs)
            {
                int i, currentFlag, previousFlag = -1, previousIndex = 0;
                PixelRun run;
                for (i = 0; i < runs.Length; i++)
                {
                    run = runs[i];
                    currentFlag = palette.ContainsKey(run.Color) ? 0 : 1;
                    if (i == 0) { previousFlag = currentFlag; previousIndex = i; }
                    else if (currentFlag != previousFlag)
                    {
                        WritePixelSequence(s, runs, previousIndex, i, previousFlag, palette);
                        previousFlag = currentFlag;
                        previousIndex = i;
                    }
                }
                WritePixelSequence(s, runs, previousIndex, i, previousFlag, palette);
            }
            private static void WritePixelSequence(BinaryWriter s, PixelRun[] runs, int start, int end, int flag, Dictionary<int, int> palette)
            {
                WritePackedInt(end - start, 1 | (byte)flag << 1, 2, s);
                for (int i = start; i < end; i++)
                {
                    var run = runs[i];
                    switch (flag)
                    {
                        case 0:
                            WritePackedInt(palette[run.Color], 0, 0, s);
                            break;
                        case 1:
                            s.Write(run.Color);
                            break;
                    }
                }
            }
        }
        public class Reader
        {
            internal Reader() { }
            internal static Reader FromStream(Stream s)
            {
                var r = new Reader();
                r.Init(s);
                return r;
            }
            public int[] Palette { get; private set; }

            public LRLEFormat Format { get; private set; }
            public int MipCount { get; private set; }
            public short Width { get; private set; }
            public short Height { get; private set; }

            private uint[] commandOffsets;
            private byte[] mipData;
            void Init(Stream stream)
            {
                var s = new BinaryReader(stream);
                if (s.ReadUInt32() != LRLE) throw new InvalidDataException("Not a LRLE Image");
                this.Format = (LRLEFormat)s.ReadUInt32();
                this.Width = s.ReadInt16();
                this.Height = s.ReadInt16();
                this.MipCount = s.ReadInt32();
                this.commandOffsets = new uint[MipCount];
                for (int i = 0; i < MipCount; i++) commandOffsets[i] = s.ReadUInt32();

                if (Format == LRLEFormat.V002)
                {
                    this.Palette = new int[s.ReadInt32()];
                    for (int cc = 0; cc < Palette.Length; cc++) Palette[cc] = s.ReadInt32();
                }
                this.mipData = new byte[stream.Length - stream.Position];
                stream.Read(mipData, 0, mipData.Length);
            }
            public IEnumerable<Mip> Read()
            {
                for (int i = 0; i < MipCount; i++)
                {
                    long start = commandOffsets[i];
                    long end = i == MipCount - 1 ?
                        (uint)mipData.LongLength : commandOffsets[i + 1];
                    byte[] mipBytes = new byte[end - start];
                    Array.Copy(mipData, start, mipBytes, 0, mipBytes.Length);
                    yield return new Mip(mipBytes, i, Format, Palette, start, end, Width >> i, Height >> i);
                }
            }
            public class Mip
            {
                private readonly byte[] mipBytes;
                private readonly LRLEFormat version;
                private readonly int[] palette;
                private readonly int blockRowSize;
                private readonly int widthLog2;

                int pixelsRead = 0;

                public int Width { get; }
                public int Height { get; }
                public long Start { get; }
                public long End { get; }
                public int Index { get; }

                public long Length => End - Start;

                unsafe void WritePixel(int* pixels, int color)
                {
                    *(pixels + BlockIndexToScanlineIndex(pixelsRead++, Width, blockRowSize, widthLog2)) = color;
                }
                public Mip(byte[] mipBytes, int index, LRLEFormat version, int[] palette, long start, long end, int width, int height)
                {
                    this.mipBytes = mipBytes;
                    this.version = version;
                    this.palette = palette;

                    this.Width = width;
                    this.Height = height;
                    this.Index = index;
                    this.Start = start;
                    this.End = end;
                    this.blockRowSize = Width << 2;
                    this.widthLog2 = Log2(Width);
                }
                public byte[] GetPixels()
                {
                    var pixels = new byte[Width * Height << 2];
                    var ptr = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    Read(ptr.AddrOfPinnedObject());
                    ptr.Free();
                    return pixels;
                }
                public unsafe void Read(IntPtr pixels)
                {
                    using (var s = new BinaryReader(new MemoryStream(mipBytes)))
                    {
                        switch (version)
                        {
                            case LRLEFormat.Default:
                                Read0000(s, (int*)pixels.ToPointer());
                                break;
                            case LRLEFormat.V002:
                                ReadV002(s, palette, (int*)pixels.ToPointer());
                                break;
                            default: throw new NotSupportedException($"Unsuported version: {version}");
                        }
                    }
                }
                private unsafe void Read0000(BinaryReader s, int* pixels)
                {
                    while (s.BaseStream.Position < s.BaseStream.Length)
                    {
                        var cmdByte = s.ReadByte();
                        int len;
                        switch (cmdByte & 3)
                        {
                            case 0: //Blank pixel repeat
                                len = (int)(ReadPackedInt(s, cmdByte) >> 2);
                                pixelsRead += len;
                                break;
                            case 1: //Inline pixel sequence
                                len = cmdByte >> 2;
                                for (int j = 0; j < len; j++)
                                {
                                    WritePixel(pixels, s.ReadInt32());
                                }
                                break;
                            case 2: //Inline pixel repeat
                                len = (int)(ReadPackedInt(s, cmdByte) >> 2);
                                int color = s.ReadInt32();
                                for (int i = 0; i < len; i++)
                                {
                                    WritePixel(pixels, color);
                                }
                                break;
                            case 3: //Embedded RLE
                                len = cmdByte >> 2;
                                var pixelBuffer = new byte[len << 2];
                                var pixelPtr = 0;
                                while (pixelPtr < pixelBuffer.Length)
                                {
                                    var cmd = ReadPackedInt(s);
                                    if ((cmd & 1) != 0)
                                    {
                                        var byteCount = (int)(cmd >> 1);
                                        s.Read(pixelBuffer, pixelPtr, byteCount);
                                        pixelPtr += byteCount;
                                    }
                                    else if ((cmd & 2) != 0)
                                    {
                                        var repeatCount = (int)(cmd >> 2);
                                        var b = s.ReadByte();
                                        for (int j = 0; j < repeatCount; j++)
                                        {
                                            pixelBuffer[pixelPtr++] = b;
                                        }
                                    }
                                    else
                                    {
                                        var blankCount = (int)(cmd >> 2);
                                        pixelPtr += blankCount;
                                    }

                                }
                                int startA = 0, startB = len, startC = startB + len, startD = startC + len;
                                while (startA < len)
                                {
                                    WritePixel(pixels, BitConverter.ToInt32(new byte[] {
                                        pixelBuffer[startA++],
                                        pixelBuffer[startB++],
                                        pixelBuffer[startC++],
                                        pixelBuffer[startD++]
                                    }, 0));
                                }
                                break;
                            default:
                                throw new Exception($"Unknown comand {cmdByte & 3}");
                        }
                    }
                }
                private unsafe void ReadV002(BinaryReader s, int[] palette, int* pixels)
                {
                    while (s.BaseStream.Position < s.BaseStream.Length)
                    {
                        var cmdByte = ReadPackedInt(s);
                        var commandType = cmdByte & 1;
                        int count, flags, color;
                        switch (commandType)
                        {
                            case 0: //Pixel repeat
                                count = (int)(cmdByte >> 3);
                                flags = ((int)cmdByte & 7) >> 1;
                                switch (flags)
                                {
                                    case 1://8bit packed index
                                        color = palette[s.ReadByte()];
                                        break;
                                    case 2://16bit packed index
                                        color = palette[s.ReadUInt16()];
                                        break;
                                    case 3://Inline color
                                        color = s.ReadInt32();
                                        break;
                                    default:
                                        throw new Exception($"Unknown flags {flags}");
                                }
                                for (int i = 0; i < count; i++)
                                {
                                    WritePixel(pixels, color);
                                }
                                break;
                            case 1: //Pixel sequence
                                count = (int)(cmdByte >> 2);
                                flags = ((int)cmdByte & 3) >> 1;
                                for (int j = 0; j < count; j++)
                                {
                                    switch (flags)
                                    {
                                        case 0: //Packed index
                                            color = palette[(int)ReadPackedInt(s)];
                                            break;
                                        case 1: //Inline
                                            color = s.ReadInt32();
                                            break;
                                        default:
                                            throw new Exception($"Unknown flags {flags}");
                                    }
                                    WritePixel(pixels, color);
                                }
                                break;

                            default:
                                throw new Exception($"Unknown command {commandType}");
                        }
                    }
                }
            }
        }
        public static Writer GetWriter() => new Writer();
        public static Reader GetReader(Stream s) => Reader.FromStream(s);
        internal static ulong ReadPackedInt(BinaryReader s, byte start)
        {
            if ((start & 0x80) == 0) return start;
            return ((uint)start & 0x7f) | (ReadPackedInt(s) << 7);
        }
        internal static ulong ReadPackedInt(BinaryReader s)
        {
            ulong u = 0;
            int shift = 0;
            byte b;
            do
            {
                b = s.ReadByte();
                u |= ((uint)b & 0x7F) << shift; shift += 7;

            } while ((b & 0x80) != 0);
            return u;
        }
        internal static void WritePackedInt(int number, int flag, int startBit, BinaryWriter s)
        {
            ulong u = (ulong)flag;
            u |= (ulong)number << startBit;
            byte b;
            do
            {
                b = (byte)(u & 0xFF);
                u >>= 7;
                if (u > 0) b |= 0x80;
                s.Write(b);
            } while (u > 0);
        }
        internal static int Log2(int n) { int i = 0; while (n > 0) { n >>= 1; i++; } return i; }
        internal static int BlockIndexToScanlineIndex(int block_index, int width, int block_row_size, int width_log2)
        {
            int block_total_index = block_index & (block_row_size - 1);
            int block_row = block_index >> width_log2 >> 1;
            int block_column = block_total_index >> 4;
            int block_index_row = (block_index >> 2) & 3;
            int block_index_column = block_index & 3;
            int scanline_row = (block_row << 2) + block_index_row;
            int scanline_column = (block_column << 2) + block_index_column;
            int scanline_index = (scanline_row * width) + scanline_column;
            return scanline_index;
        }
    }
}
