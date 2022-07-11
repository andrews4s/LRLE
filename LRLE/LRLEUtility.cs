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

        public readonly struct PixelRun
        {
            public PixelRun(int c, int l) { Color = c; Length = l; }
            public readonly int Color;
            public readonly int Length;
            public override string ToString() { return $"{Color:X8} x {Length}"; }
        }
        public class Writer
        {
            public class Mip
            {
                internal List<PixelRun[]> Runs { get; set; } = new List<PixelRun[]>();
                public int Width { get; set; }
                public int Height { get; set; }
            }
            public LRLEFormat Format => this.codec.Format;
            public List<Mip> Mips { get; set; } = new List<Mip>();
            LRLECodec codec;

            public Writer(LRLECodec codec)
            {
                this.codec = codec;
            }

            public void AddMip(int width, int height, byte[] rawARGBPixelData)
            {
                this.Mips.Add(new Mip { Width = width, Height = height, Runs = ExtractPixelRuns(rawARGBPixelData, width, height) });
                if (!this.codec.ProcessMip(this.Mips.Count) && this.codec is CodecV002)
                {
                    this.codec = new Codec0000();
                }
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
                            this.codec.RegisterColor(c);
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
                this.codec.BeginWrite(this, s);

                var cmdOffsets = new uint[mipCount];
                var start = fs.Position;
                for (int mipMapIndex = 0; mipMapIndex < Mips.Count; mipMapIndex++)
                {
                    Mip mip = Mips[mipMapIndex];
                    cmdOffsets[mipMapIndex] = (uint)(fs.Position - start);
                    for (int runIndex = 0; runIndex < mip.Runs.Count; runIndex++)
                    {
                        PixelRun[] runList = mip.Runs[runIndex];
                        this.codec.WritePixelRun(s, runList);
                    }
                }
                fs.Position = 0;
                s.Write(LRLE);
                s.Write((uint)this.Format);
                s.Write((ushort)Mips[0].Width);
                s.Write((ushort)Mips[0].Height);
                s.Write(Mips.Count);
                for (int cmdOffsetIndex = 0; cmdOffsetIndex < cmdOffsets.Length; cmdOffsetIndex++)
                    s.Write(cmdOffsets[cmdOffsetIndex]);
                fs.Seek(0, SeekOrigin.End);
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
            public LRLEFormat Format { get; private set; }
            public int MipCount { get; private set; }
            public short Width { get; private set; }
            public short Height { get; private set; }
            public LRLECodec Codec { get; private set; }

            private uint[] commandOffsets;
            private byte[] mipData;

            void Init(Stream stream)
            {
                var s = new BinaryReader(stream);
                if (s.ReadUInt32() != LRLE) throw new InvalidDataException("Not a LRLE Image");
                this.Format = (LRLEFormat)s.ReadUInt32();
                this.Codec = LRLECodec.Create(this.Format);
                this.Width = s.ReadInt16();
                this.Height = s.ReadInt16();
                this.MipCount = s.ReadInt32();
                this.commandOffsets = new uint[MipCount];
                for (int i = 0; i < MipCount; i++) commandOffsets[i] = s.ReadUInt32();
                this.Codec.BeginRead(this, s);
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
                    yield return new Mip(mipBytes, i, this, start, end, Width >> i, Height >> i);
                }
            }

            public byte[] GetMipPixels(Mip mip)
            {
                var pixels = new byte[Width * Height << 2];
                var ptr = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                ReadMip(ptr.AddrOfPinnedObject(), mip);
                ptr.Free();
                return pixels;
            }
            public unsafe void ReadMip(IntPtr pixels, Mip mip)
            {
                using (var s = new BinaryReader(new MemoryStream(mip.RawBytes)))
                {
                    this.Codec.Read(s, (int*)pixels, mip);
                }
            }
            public class Mip
            {
                private readonly byte[] mipBytes;
                private readonly Reader reader;
                private readonly int blockRowSize;
                private readonly int widthLog2;

                int pixelsRead = 0;

                public int Width { get; }
                public int Height { get; }
                public long Start { get; }
                public long End { get; }
                public int Index { get; }
                public byte[] RawBytes => this.mipBytes;

                public long Length => End - Start;

                internal unsafe void WritePixel(int* pixels, int color)
                {
                    *(pixels + BlockIndexToScanlineIndex(pixelsRead++, Width, blockRowSize, widthLog2)) = color;
                }
                internal unsafe void WritePixel(int count)
                {
                    pixelsRead += count;
                }

                public byte[] GetPixels()
                {
                    return this.reader.GetMipPixels(this);
                }

                public void Read(IntPtr pixels)
                {
                    this.reader.ReadMip(pixels, this);
                }

                public Mip(byte[] mipBytes, int index, Reader reader, long start, long end, int width, int height)
                {
                    this.reader = reader;
                    this.mipBytes = mipBytes;

                    this.Width = width;
                    this.Height = height;
                    this.Index = index;
                    this.Start = start;
                    this.End = end;
                    this.blockRowSize = Width << 2;
                    this.widthLog2 = Log2(Width);
                }
            }
        }
        public static Writer GetWriter() => GetWriter(LRLEFormat.V002);
        public static Writer GetWriter(LRLEFormat format) => GetWriter(LRLECodec.Create(format));
        public static Writer GetWriter(LRLECodec codec) => new Writer(codec);

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

        public abstract class LRLECodec
        {
            public abstract LRLEFormat Format { get; }
            public abstract void BeginRead(Reader reader, BinaryReader bw);
            public abstract bool BeginWrite(Writer writer, BinaryWriter bw);
            public abstract void WritePixelRun(BinaryWriter s, PixelRun[] runList);
            public abstract unsafe void Read(BinaryReader s, int* pixels, Reader.Mip mip);
            public abstract void RegisterColor(int c);
            public static LRLECodec Create(LRLEFormat format)
            {
                switch (format)
                {
                    case LRLEFormat.Default:
                        return new Codec0000();
                    case LRLEFormat.V002:
                        return new CodecV002();
                    default:
                        throw new NotSupportedException($"LRLE format {format} is not supported");
                }
            }

            public abstract bool ProcessMip(int level);
        }
        public class Codec0000 : LRLECodec
        {
            public override LRLEFormat Format => LRLEFormat.Default;

            public override void BeginRead(Reader reader, BinaryReader bw) { }
            public override bool BeginWrite(Writer writer, BinaryWriter bw) => true;
            public override void RegisterColor(int c) { }

            public override void WritePixelRun(BinaryWriter s, PixelRun[] runList)
            {
                if (runList.Length == 1)
                    WritePixelRepeat(s, runList[0]);
                else
                    WritePixelSequence(s, runList);
            }
            public override unsafe void Read(BinaryReader s, int* pixels, Reader.Mip mip)
            {
                while (s.BaseStream.Position < s.BaseStream.Length)
                {
                    var cmdByte = s.ReadByte();
                    int len;
                    switch (cmdByte & 3)
                    {
                        case 0: //Blank pixel repeat
                            len = (int)(ReadPackedInt(s, cmdByte) >> 2);
                            mip.WritePixel(len);
                            break;
                        case 1: //Inline pixel sequence
                            len = cmdByte >> 2;
                            for (int j = 0; j < len; j++)
                            {
                                mip.WritePixel(pixels, s.ReadInt32());
                            }
                            break;
                        case 2: //Inline pixel repeat
                            len = (int)(ReadPackedInt(s, cmdByte) >> 2);
                            int color = s.ReadInt32();
                            for (int i = 0; i < len; i++)
                            {
                                mip.WritePixel(pixels, color);
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
                                    pixelPtr += (int)(cmd >> 2);
                                }
                            }
                            int startA = 0, startB = len, startC = startB + len, startD = startC + len;
                            while (startA < len)
                            {
                                mip.WritePixel(pixels, BitConverter.ToInt32(new byte[] {
                                        pixelBuffer[startA++],
                                        pixelBuffer[startB++],
                                        pixelBuffer[startC++],
                                        pixelBuffer[startD++]
                                    }, 0));
                            }
                            break;
                        default:
                            throw new NotSupportedException($"Unknown command {cmdByte & 3}");
                    }
                }
            }
            private static void WritePixelRepeat(BinaryWriter s, PixelRun run)
            {
                WritePackedInt(run.Length, run.Color == 0 ? 0 : 2, 2, s);
                if (run.Color != 0) s.Write(run.Color);
            }
            private static void WritePixelSequence(BinaryWriter s, PixelRun[] runs)
            {
                const int maxLength = byte.MaxValue >> 2;
                int written = 0;
                while (written < runs.Count())
                {
                    var n = (runs.Count() - written);
                    var offset = written;
                    if (n > maxLength) n = maxLength;
                    var calcInlineSize = 1 + (4 * n);

                    var channelRuns = new List<List<PixelRun>>();
                    if (SplitChannelStreamIntoRuns(GetChannelStream(runs, n, offset), calcInlineSize, channelRuns))
                        EncodePixelSequenceChannels(s, n, channelRuns);
                    else
                        WriteInlinePixelSequence(s, runs, n, offset);

                    written += n;
                }
            }

            private static bool SplitChannelStreamIntoRuns(IEnumerable<byte> channelStream, int maxSize, List<List<PixelRun>> l)
            {
                int sz = 0;
                foreach (var b in channelStream)
                {
                    if (sz > maxSize)
                    {
                        return false;
                    }
                    if (l.Count == 0)
                    {
                        l.Add(new List<PixelRun>() { new PixelRun(b, 1) });
                    }
                    else
                    {
                        var lastRun = l.Last();
                        var lastEntry = lastRun.Last();
                        if (lastEntry.Color == b)
                        {
                            if (lastRun.Count == 1)
                            {

                                lastRun[0] = new PixelRun(b, lastEntry.Length + 1);
                            }
                            else
                            {
                                lastRun.RemoveAt(lastRun.Count - 1);
                                sz += 1 + lastRun.Count;
                                l.Add(new List<PixelRun>() { new PixelRun(b, 2) });
                            }
                        }
                        else
                        {
                            if (lastEntry.Length > 1)
                            {
                                sz += 1 + lastRun.Count;
                                l.Add(new List<PixelRun>() { new PixelRun(b, 1) });
                            }
                            else
                            {
                                lastRun.Add(new PixelRun(b, 1));
                            }
                        }
                    }
                }
                sz += 1 + l[l.Count - 1].Count;
                return sz < maxSize;
            }

            /// <summary>
            /// Arranges a range of argb pixels by their channel.
            /// ex.
            /// ARGB ARGB ARGB => AAA RRR GGG BBB
            /// </summary>
            internal static IEnumerable<byte> GetChannelStream(PixelRun[] runs, int n, int offset)
            {
                return
                from c in Enumerable.Range(0, 4)
                let shift = 8 * c
                let mask = 0xFF << shift
                from p in Enumerable.Range(0, n)
                select (byte)((runs[offset + p].Color & mask) >> shift);
            }

            private static void WriteInlinePixelSequence(BinaryWriter s, PixelRun[] runs, int toWrite, int offset)
            {
                s.Write((byte)(1 | (toWrite << 2)));
                for (int i = 0; i < toWrite; i++)
                    s.Write(runs[i + offset].Color);
            }
            private static void EncodePixelSequenceChannels(BinaryWriter s, int n, List<List<PixelRun>> channelStream2)
            {
                s.Write((byte)(3 | (n << 2)));
                foreach (var channelRun in channelStream2)
                {
                    if (channelRun.Count == 1)
                    {
                        if (channelRun[0].Color == 0)
                            WritePackedInt(channelRun[0].Length, 0, 2, s);
                        else
                        {
                            WritePackedInt(channelRun[0].Length, 2, 2, s);
                            s.Write((byte)channelRun[0].Color);
                        }
                    }
                    else
                    {
                        WritePackedInt(channelRun.Count, 1, 1, s);
                        for (int i = 0; i < channelRun.Count; i++)
                        {
                            s.Write((byte)channelRun[i].Color);
                        }
                    }
                }
            }

            public override bool ProcessMip(int level)
            {
                return true;
            }
        }
        public class CodecV002 : LRLECodec
        {
            public PaletteBuilder PaletteBuilder { get; set; } = new PaletteBuilder(ushort.MaxValue, 20);
            public int[] Palette { get; private set; }

            public override LRLEFormat Format => LRLEFormat.V002;

            public bool HasInlinePixels { get; private set; }

            private Dictionary<int, int> colorToPaletteIndex = new Dictionary<int, int>();

            public override bool BeginWrite(Writer writer, BinaryWriter bw)
            {
                this.Palette = PaletteBuilder.GetPalette().ToArray();
                this.colorToPaletteIndex = PaletteBuilder.GetPaletteIndex();
                bw.Write(Palette.Length);
                for (int i = 0; i < Palette.Length; i++)
                {
                    int p = Palette[i];
                    bw.Write(p);
                }
                return true;
            }
            public override void WritePixelRun(BinaryWriter s, PixelRun[] runList)
            {
                if (runList.Length == 1)
                    WritePixelRepeat(s, colorToPaletteIndex, runList[0]);
                else
                    WritePixelSequence(s, colorToPaletteIndex, runList);
            }

            public override void RegisterColor(int c)
            {
                this.PaletteBuilder.RegisterColor(c);
            }


            public unsafe override void Read(BinaryReader s, int* pixels, Reader.Mip mip)
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
                                    color = Palette[s.ReadByte()];
                                    break;
                                case 2://16bit packed index
                                    color = Palette[s.ReadUInt16()];
                                    break;
                                case 3://Inline color
                                    this.HasInlinePixels = true;
                                    color = s.ReadInt32();
                                    break;
                                default:
                                    throw new NotSupportedException($"Unknown V002 pixel repeat flags {flags}");
                            }
                            for (int i = 0; i < count; i++)
                            {
                                mip.WritePixel(pixels, color);
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
                                        color = Palette[(int)ReadPackedInt(s)];
                                        break;
                                    case 1: //Inline
                                        color = s.ReadInt32();
                                        break;
                                    default:
                                        throw new NotSupportedException($"Unknown pixel sequence flags {flags}");
                                }
                                mip.WritePixel(pixels, color);
                            }
                            break;
                        default:
                            throw new NotSupportedException($"Unknown command {commandType}");
                    }
                }
            }

            public override void BeginRead(Reader reader, BinaryReader s)
            {
                this.Palette = new int[s.ReadInt32()];
                for (int cc = 0; cc < this.Palette.Length; cc++) Palette[cc] = s.ReadInt32();
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

            public override bool ProcessMip(int level)
            {
                this.PaletteBuilder.BuildPalette();
                if (level == 1 && this.PaletteBuilder.Full)
                {
                    return false;
                }
                return true;
            }
        }

        public class PaletteBuilder
        {
            private readonly int maxColors;
            private readonly byte bucketSize;
            private readonly Dictionary<byte, Dictionary<byte, Dictionary<byte, List<int>>>> index;
            private List<int> palette;
            private Dictionary<int, int> paletteDictionary;
            private IDictionary<int, int> writePaletteOccurrences = new Dictionary<int, int>(ushort.MaxValue);

            public bool Full => this.palette.Count >= maxColors;

            public PaletteBuilder(int maxColors, byte interval)
            {
                this.maxColors = maxColors;
                this.bucketSize = interval;
                this.index = new Dictionary<byte, Dictionary<byte, Dictionary<byte, List<int>>>>();
                this.palette = new List<int>();
                this.paletteDictionary = new Dictionary<int, int>();
            }
            public void Add(int c)
            {
                if (paletteDictionary.ContainsKey(c)) return;
                if (this.palette.Count < this.maxColors)
                {
                    int i = palette.Count;
                    palette.Add(c);
                    paletteDictionary[c] = i;
                    IndexColor(c);
                }
                else
                    paletteDictionary[c] = paletteDictionary[LocateColor(c)];
            }

            internal void IndexColor(int c)
            {
                GetKey(c, out var a, out var r, out var g);
                if (!index.ContainsKey(a))
                    index[a] = new Dictionary<byte, Dictionary<byte, List<int>>>();
                if (!index[a].ContainsKey(r))
                    index[a][r] = new Dictionary<byte, List<int>>();
                if (!index[a][r].TryGetValue(g, out var glist))
                {
                    glist = new List<int>();
                    index[a][r][g] = glist;
                }
                glist.Add(c);
            }
            /// <summary>
            /// Returns a stream of numbers starting from the start value +/- 1 within the range of the min/max value.
            /// </summary>
            private IEnumerable<byte> WalkRange(byte start, byte min, byte max)
            {
                yield return start;
                byte h = start, l = start;
                while (h <= max || l >= min)
                {
                    h++; l--;
                    if (h <= max) yield return h; if (l >= min) yield return l;
                }
            }
            private T GetClosest<T>(IDictionary<byte, T> dict, byte target)
            {
                foreach (var b in WalkRange(target, 0, byte.MaxValue)) if (dict.TryGetValue(b, out var val)) return val;
                throw new InvalidOperationException();
            }
            private int LocateColor(int c)
            {
                GetKey(c, out var a, out var r, out var g);
                return GetClosest(GetClosest(GetClosest(index, a), r), g).OrderBy(x => CompareColor(x, c)).First();
            }

            internal void GetKey(int c, out byte a, out byte r, out byte b)
            {
                var u = (uint)c;
                a = (byte)(((u >> 24)) / bucketSize);
                r = (byte)(((u & 0x00FF0000) >> 16) / bucketSize);
                b = (byte)(((u & 0x0000FF00) >> 8) / bucketSize);
            }

            internal static int CompareColor(int a, int b)
            {

                const uint amask = 0xFF000000;
                const uint rmask = 0x00FF0000;
                const uint gmask = 0x0000FF00;
                const uint bmask = 0x000000FF;

                var c1 = (int)(((a & amask) >> 24) - ((b & amask) >> 24));
                if (c1 < 0) c1 = -c1;

                var c2 = ((a & rmask) >> 16) - ((b & rmask) >> 16);
                if (c2 < 0) c2 = -c2;

                var c3 = ((a & gmask) >> 8) - ((b & gmask) >> 8);
                if (c3 < 0) c3 = -c3;

                var c4 = (a & bmask) - (b & bmask);
                if (c4 < 0) c4 = -c4;

                return (int)(c1 + c2 + c3 + c4);
            }

            internal List<int> GetPalette()
            {
                return this.palette;
            }

            internal Dictionary<int, int> GetPaletteIndex()
            {
                return this.paletteDictionary;
            }

            internal void RegisterColor(int c)
            {
                if (paletteDictionary.ContainsKey(c)) return;

                if (writePaletteOccurrences.TryGetValue(c, out int cc))
                    writePaletteOccurrences[c] = cc + 1;
                else
                    writePaletteOccurrences[c] = 1;
            }

            public void BuildPalette()
            {
                foreach (var colorOccurrence in this.writePaletteOccurrences.OrderByDescending(x => x.Value).ToArray())
                {
                    this.Add(colorOccurrence.Key);
                }
            }
        }
    }
}
