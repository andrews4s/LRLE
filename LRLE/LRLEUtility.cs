using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
                public List<PixelRun[]> Runs { get; set; } = new List<PixelRun[]>();
                public int Width { get; set; }
                public int Height { get; set; }
            }
            public List<Mip> Mips { get; set; } = new List<Mip>();

            readonly IDictionary<int, int> paletteOccurrences = new Dictionary<int, int>(ushort.MaxValue);
            public void AddMip(int width, int height, byte[] rawARGBPixelData)
            {
                this.Mips.Add(new Mip { Width = width, Height = height, Runs = ExtractPixelRuns(rawARGBPixelData, width, height) });
            }
            public List<PixelRun[]> ExtractPixelRuns(byte[] argbPixels, int width, int height)
            {
                int lastColor = 0;
                int runLength = 0;
                var allRuns = new List<PixelRun[]>();
                var runList = new List<PixelRun>();
                var pixels = argbPixels.Length >> 2;
                for (int i = 0; i < pixels; i++)
                {
                    var index = BlockIndexToScanlineIndex(i, width, height) << 2;
                    var c = BitConverter.ToInt32(argbPixels, index);
                    if (paletteOccurrences.ContainsKey(c))
                        paletteOccurrences[c]++;
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
                            if (runList.Any())
                            {
                                allRuns.Add(runList.ToArray());
                                runList = new List<PixelRun>();
                            }
                            allRuns.Add(new[] { new PixelRun(lastColor, runLength) });
                        }
                        else
                        {
                            runList.Add(new PixelRun(lastColor, 1));
                        }
                        lastColor = c;
                        runLength = 1;
                    }
                }
                if (runLength > 1)
                {
                    if (runList.Any())
                    {
                        allRuns.Add(runList.ToArray());
                    }
                    allRuns.Add(new[] { new PixelRun(lastColor, runLength) });
                }
                else
                {
                    allRuns.Add(runList.ToArray());
                }
                return allRuns;
            }
            public void Write(Stream fs)
            {
                var s = new BinaryWriter(fs);
                var mipCount = this.Mips.Count;
                var headerSize = 16 + (mipCount << 2);

                var paletteArray =
                    paletteOccurrences
                    .OrderByDescending(x => x.Value)
                    .Take(ushort.MaxValue)
                    .Select(x => x.Key)
                    .ToArray();
                var palette =
                    Enumerable
                        .Range(0, paletteArray.Length)
                        .ToDictionary(x => paletteArray[x], x => x);

                s.Seek(headerSize, SeekOrigin.Begin);
                s.Write(paletteArray.Length);
                var cmdOffsets = new uint[mipCount];
                foreach (var p in paletteArray) s.Write(p);
                int mipMapIndex = 0;
                var start = fs.Position;
                foreach (var mip in Mips)
                {
                    cmdOffsets[mipMapIndex++] = (uint)(fs.Position - start);
                    foreach (var runList in mip.Runs)
                    {
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
                foreach (var cmd in cmdOffsets)
                    s.Write(cmd);
                fs.Seek(0, SeekOrigin.End);

            }

            private static void WritePixelRepeat(BinaryWriter s, Dictionary<int, int> palette, PixelRun run)
            {
                var paletteIndex = palette.ContainsKey(run.Color) ? palette[run.Color] : -1;
                var flag = paletteIndex < 0 ? 3 //Inline
                    : paletteIndex <= byte.MaxValue ? 1 //Byte index
                    : 2; //Short index
                var runBytes = WritePackedInt(run.Length, flag << 1, 3).ToArray();
                s.Write(runBytes);
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
                }
            }

            private static void WritePixelSequence(BinaryWriter s, Dictionary<int, int> palette, PixelRun[] runs)
            {
                int i;
                PixelRun run;
                int flag, lastFlag = -1, lastIndex = 0;
                for (i = 0; i < runs.Length; i++)
                {
                    run = runs[i];
                    flag = palette.ContainsKey(run.Color) ? 0 : 1;
                    if (lastFlag == -1)
                    {
                        lastFlag = flag;
                        lastIndex = i;

                    }
                    else if (flag != lastFlag)
                    {
                        WritePixelSequence(s, palette, runs, lastIndex, i, lastFlag);

                        lastFlag = flag;
                        lastIndex = i;
                    }

                }
                WritePixelSequence(s, palette, runs, lastIndex, i, lastFlag);
            }
            private static void WritePixelSequence(BinaryWriter s, Dictionary<int, int> palette, PixelRun[] runs, int start, int end, int flag)
            {
                var runBytes = WritePackedInt(end - start, 1 | (byte)flag << 1, 2).ToArray();
                s.Write(runBytes);
                for (int i = start; i < end; i++)
                {
                    var run = runs[i];
                    switch (flag)
                    {
                        case 0:
                            s.Write(WritePackedInt(palette[run.Color], 0, 0).ToArray());
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
            public byte[] MipData { get; private set; }
            internal Reader()
            {
                this.Palette = new int[0];
            }
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
            public uint[] CommandOffsets { get; private set; }
            void Init(Stream stream)
            {
                var s = new BinaryReader(stream);
                if (s.ReadUInt32() != LRLE) throw new InvalidDataException("Not a LRLE Image");
                this.Format = (LRLEFormat)s.ReadUInt32();
                this.Width = s.ReadInt16();
                this.Height = s.ReadInt16();
                this.MipCount = s.ReadInt32();
                this.CommandOffsets = new uint[MipCount];
                for (int i = 0; i < MipCount; i++) CommandOffsets[i] = s.ReadUInt32();

                if (Format == LRLEFormat.V002)
                {
                    this.Palette = new int[s.ReadInt32()];
                    for (int cc = 0; cc < Palette.Length; cc++) Palette[cc] = s.ReadInt32();
                }
                this.MipData = new byte[stream.Length - stream.Position];
                stream.Read(MipData, 0, MipData.Length);
            }
            public IEnumerable<Mip> Read()
            {
                for (int i = 0; i < MipCount; i++)
                {
                    long start = CommandOffsets[i];
                    long end = i == MipCount - 1 ?
                        (uint)MipData.LongLength : CommandOffsets[i + 1];
                    byte[] mipBytes = new byte[end - start];
                    Array.Copy(MipData, start, mipBytes, 0, mipBytes.Length);
                    var mip = new Mip(mipBytes, i, Format, Palette, start, end)
                    {
                        Width = Width >> i,
                        Height = Height >> i
                    };
                    yield return mip;
                }
            }
            public class Mip
            {
                private readonly byte[] mipBytes;
                private readonly LRLEFormat version;
                private readonly int[] palette;

                public int Width { get; set; }
                public int Height { get; set; }
                public long Start { get; }
                public long End { get; }
                public long Length => End - Start;

                public int Index { get; }

                public Mip(byte[] mipBytes, int index, LRLEFormat version, int[] palette, long start, long end)
                {
                    this.mipBytes = mipBytes;
                    this.version = version;
                    this.palette = palette;

                    this.Index = index;
                    this.Start = start;
                    this.End = end;
                }
                public byte[] GetPixels()
                {
                    var pixels = new byte[Width * Height << 2];
                    int pixelsWritten = 0;
                    byte[] color;
                    foreach (var run in Read())
                    {
                        color = BitConverter.GetBytes(run.Color);
                        for (int j = 0; j < run.Length; j++)
                            Array.Copy(color, 0, pixels, LRLEUtility.BlockIndexToScanlineIndex(pixelsWritten++, Width, Height) << 2, 4);
                    }
                    return pixels;
                }
                public IEnumerable<PixelRun> Read()
                {
                    using (var s = new BinaryReader(new MemoryStream(mipBytes)))
                    {
                        while (s.BaseStream.Position < s.BaseStream.Length)
                        {
                            switch (version)
                            {
                                case LRLEFormat.Default:
                                    foreach (var run in Read0000Chunk(s))
                                        yield return run;
                                    break;
                                case LRLEFormat.V002:
                                    foreach (var run in ReadV002Chunk(s, palette))
                                        yield return run;
                                    break;
                            }
                        }
                    }
                }
                public IEnumerable<PixelRun> Read0000Chunk(BinaryReader s)
                {
                    var cmdLengthByte = s.ReadByte();
                    var cmdByte = cmdLengthByte & 3;
                    switch (cmdByte)
                    {
                        case 0:
                            yield return ReadBlankPixelRepeat(s, cmdLengthByte);
                            break;
                        case 1:
                            foreach (var run in ReadInlinePixelSequence(s, cmdLengthByte))
                                yield return run;
                            break;
                        case 2:
                            yield return ReadInlinePixelRepeat(s, cmdLengthByte);
                            break;
                        case 3:
                            foreach (var run in ReadEmbeddedRLE(s, cmdLengthByte))
                                yield return run;
                            break;

                    }
                }

                readonly byte[] ReadHighBitSequenceBuffer = new byte[10];
                public byte[] ReadHighBitSequence(BinaryReader r, byte? start = null)
                {
                    int i = 0;
                    byte b;
                    if (start.HasValue)
                    {
                        if ((start.Value & 0x80) == 0)
                            return new byte[] { start.Value };
                        ReadHighBitSequenceBuffer[i++] = start.Value;
                    }
                    do
                    {
                        b = r.ReadByte();
                        ReadHighBitSequenceBuffer[i++] = b;
                    } while ((b & 0x80) != 0);
                    byte[] rtnVal = new byte[i];
                    Array.Copy(ReadHighBitSequenceBuffer, rtnVal, i);
                    return rtnVal;
                }

                private IEnumerable<PixelRun> ReadPixelSequence(BinaryReader s, int[] palette, byte[] lengthBytes, byte cmdByte)
                {
                    var count = ReadPackedInt(lengthBytes, 2);
                    var flags = (cmdByte & 3) >> 1;
                    var color = 0;
                    for (int j = 0; j < count; j++)
                    {
                        switch (flags)
                        {
                            case 0:
                                var ix = ReadPackedInt(ReadHighBitSequence(s), 0);
                                color = palette[ix];
                                break;
                            case 1:
                                color = s.ReadInt32();
                                break;
                        }
                        yield return new PixelRun(color, 1);
                    }
                }

                private PixelRun ReadPixelRepeat(BinaryReader s, int[] palette, byte[] lengthBytes, byte cmdByte)
                {
                    var count = ReadPackedInt(lengthBytes, 3);
                    var flags = (cmdByte & 7) >> 1;
                    int color = 0;
                    switch (flags)
                    {
                        case 1:
                            color = palette[s.ReadByte()];
                            break;
                        case 2:
                            color = palette[s.ReadUInt16()];
                            break;
                        case 3:
                            color = s.ReadInt32();
                            break;
                    }
                    return new PixelRun(color, count);
                }
                private IEnumerable<PixelRun> ReadEmbeddedRLE(BinaryReader s, byte lengthByte)
                {
                    var count = lengthByte >> 2;
                    var pixelBuffer = new byte[count << 2];
                    var pixelPtr = 0;
                    while (pixelPtr < pixelBuffer.Length)
                    {
                        var lengthBytes2 = ReadHighBitSequence(s).ToList();
                        var cmd = lengthBytes2[0];
                        if ((cmd & 1) != 0)
                        {
                            var byteCount = ReadPackedInt(lengthBytes2, 1);
                            s.Read(pixelBuffer, pixelPtr, byteCount);
                            pixelPtr += byteCount;
                        }
                        else if ((cmd & 2) != 0)
                        {
                            var repeatCount = ReadPackedInt(lengthBytes2, 2);
                            var b = s.ReadByte();
                            for (int j = 0; j < repeatCount; j++)
                            {
                                pixelBuffer[pixelPtr++] = b;
                            }
                        }
                        else
                        {
                            var blankCount = ReadPackedInt(lengthBytes2, 2);
                            pixelPtr += blankCount;
                        }

                    }
                    for (int i = 0; i < count; i++)
                    {
                        yield return new PixelRun(BitConverter.ToInt32(new byte[] {
                                pixelBuffer[i],
                                pixelBuffer[i+ count],
                                pixelBuffer[i+ count + count],
                                pixelBuffer[i+ count + count + count]
                            }, 0)
                        , 1);

                    }
                }

                private static IEnumerable<PixelRun> ReadInlinePixelSequence(BinaryReader s, byte lengthByte)
                {
                    var count = lengthByte >> 2;
                    for (int j = 0; j < count; j++)
                    {
                        yield return new PixelRun(s.ReadInt32(), 1);
                    }
                }

                private PixelRun ReadInlinePixelRepeat(BinaryReader s, byte lengthByte)
                {
                    int len = ReadPackedInt(ReadHighBitSequence(s, lengthByte), 2);
                    int color = s.ReadInt32();
                    return new PixelRun(color, len);
                }

                private PixelRun ReadBlankPixelRepeat(BinaryReader s, byte lengthByte)
                {
                    int len = ReadPackedInt(ReadHighBitSequence(s, lengthByte), 2);
                    return new PixelRun(0, len);
                }

                IEnumerable<PixelRun> ReadV002Chunk(BinaryReader s, int[] palette)
                {
                    var lengthBytes = ReadHighBitSequence(s);
                    var cmdByte = lengthBytes[0];
                    var commandType = cmdByte & 1;
                    switch (commandType)
                    {
                        case 0:
                            yield return ReadPixelRepeat(s, palette, lengthBytes, cmdByte);
                            break;
                        case 1:
                            foreach (var run in ReadPixelSequence(s, palette, lengthBytes, cmdByte))
                                yield return run;
                            break;
                    }
                }
            }
        }
        public static Writer GetWriter()
        {
            return new Writer();
        }
        public static Reader GetReader(Stream s)
        {
            return Reader.FromStream(s);
        }
        public static IEnumerable<byte> WritePackedInt(int number, int flag, int startBit)
        {
            ulong u = (ulong)flag;
            u |= (ulong)number << startBit;
            byte b;
            do
            {
                b = (byte)(u & 0xFF);
                u >>= 7;
                if (u > 0) b |= 0x80;
                yield return b;
            } while (u > 0);
        }
        public static int ReadPackedInt(IEnumerable<byte> bytes, int startBit)
        {
            ulong u = 0;
            int shift = 0;
            foreach (var b in bytes) { u |= ((uint)b & 0x7F) << shift; shift += 7; }
            u >>= startBit;
            return (int)u;
        }
        public static int BlockIndexToScanlineIndex(int block_index, int width, int height)
        {
            var block_row_size = width << 2;
            int block_total_index = block_index & (block_row_size - 1);

            int block_row = block_index / block_row_size;
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
