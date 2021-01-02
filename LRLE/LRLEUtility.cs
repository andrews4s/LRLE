using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LRLE
{
    public static class LRLEUtility
    {
        const uint LRLE = 0x454C524C;
        public enum LRLEFormat
        {
            Default = 0,
            V002 = 842018902
        }
        public class PixelRun
        {
            public int Length { get; set; }
            public int Color { get; set; }
            public override string ToString()
            {
                return $"{Color:X8} x {Length}";
            }
        }
        public class Writer
        {
            public class Mip
            {
                public List<PixelRun> Runs { get; set; } = new List<PixelRun>();
                public int Width { get; set; }
                public int Height { get; set; }
            }
            public List<Mip> Mips { get; set; } = new List<Mip>();
            public void AddMip(int width, int height, IEnumerable<PixelRun> runs)
            {
                this.Mips.Add(new Mip { Width = width, Height = height, Runs = new List<PixelRun>(runs) });
            }
            public void AddMip(int width, int height, byte[] rawARGBPixelData)
            {
                this.Mips.Add(new Mip { Width = width, Height = height, Runs = new List<PixelRun>(ExtractPixelRuns(rawARGBPixelData, width)) });
            }
            IEnumerable<PixelRun[]> GroupPixelRuns(IList<PixelRun> runs)
            {
                var runList = new List<PixelRun>();
                foreach (var run in runs)
                {
                    if (run.Length > 1)
                    {
                        if (runList.Any())
                        {
                            yield return runList.ToArray();
                            runList = new List<PixelRun>();
                        }
                        yield return new[] { run };
                    }
                    else
                    {
                        runList.Add(run);
                    }
                }
                if (runList.Any())
                {
                    yield return runList.ToArray();
                }
            }
            public void Write(Stream fs)
            {
                var s = new BinaryWriter(fs);
                var mipCount = this.Mips.Count;
                var headerSize = 16 + (mipCount * 4);
                var palette =
                    //Select all runs from all mips to get a full palette for the whole image.
                    (from mip in Mips from run in mip.Runs select run)
                    //Group by color and sort by most used palette indexes.
                    //If the ones used the most are at the front,
                    //we can limit the number of 16 bit indexes.
                    .GroupBy(x => x.Color)
                    .OrderByDescending(x => x.Sum(y => (decimal)y.Length))
                    //Finally, select the distinct color list.
                    .Select(x => x.Key)
                    .Take(ushort.MaxValue)
                    .ToList();
                s.Seek(headerSize, SeekOrigin.Begin);
                s.Write(palette.Count);
                var cmdOffsets = new uint[mipCount];
                foreach (var p in palette) s.Write(p);
                int mipMapIndex = 0;
                var start = fs.Position;
                foreach (var mip in this.Mips)
                {
                    cmdOffsets[mipMapIndex++] = (uint)(fs.Position - start);
                    foreach (var runList in GroupPixelRuns(mip.Runs))
                    {
                        if (runList.Length == 1 && runList[0].Length > 1)
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

            private static void WritePixelRepeat(BinaryWriter s, List<int> palette, PixelRun run)
            {
                var paletteIndex = palette.IndexOf(run.Color);
                var flag = paletteIndex < 0 ? 6 //Inline
                    : paletteIndex <= byte.MaxValue ? 2 //Byte index
                    : 4; //Short index
                var runBytes = WritePackedInt(run.Length, flag, 3).ToArray();
                s.Write(runBytes);
                switch (flag)
                {
                    case 2:
                        s.Write((byte)paletteIndex);
                        break;
                    case 4:
                        s.Write((ushort)paletteIndex);
                        break;
                    case 6:
                        s.Write(run.Color);
                        break;
                }
            }

            private static void WritePixelSequence(BinaryWriter s, List<int> palette, PixelRun[] runs)
            {
                var flag = runs.Any(x => palette.IndexOf(x.Color) < 0) ? 3 : 1;
                var runBytes = LRLEUtility.WritePackedInt(runs.Length, (byte)flag, 2).ToArray();
                s.Write(runBytes);
                foreach (var run in runs)
                {
                    switch (flag)
                    {
                        case 1:
                            s.Write(WritePackedInt(palette.IndexOf(run.Color), 0, 0).ToArray());
                            break;
                        case 3:
                            s.Write(run.Color);
                            break;
                    }
                }
            }
        }


        public class Reader
        {
            private readonly Stream stream;
            private long start;

            internal Reader(Stream stream)
            {
                this.stream = stream;
                this.Palette = new int[0];
            }
            internal static Reader FromStream(Stream s)
            {
                var r = new Reader(s);
                r.Init();
                return r;
            }
            public int[] Palette { get; private set; }

            public LRLEFormat Format { get; private set; }
            public int MipCount { get; private set; }
            public short Width { get; private set; }
            public short Height { get; private set; }
            public uint[] CommandOffsets { get; private set; }
            void Init()
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
                this.start = stream.Position;
            }
            public IEnumerable<Mip> Read()
            {
                for (int i = 0; i < MipCount; i++)
                {
                    long start = this.start + CommandOffsets[i];
                    stream.Position = start;
                    long end = i == MipCount - 1 ?
                        stream.Length :
                        this.start + CommandOffsets[i + 1];
                    var mip = new Mip(stream, i, Format, Palette, start, end)
                    {
                        Width = Width >> i,
                        Height = Height >> i
                    };
                    yield return mip;
                }
                if (stream.Position != stream.Length) throw new InvalidDataException();
            }
            public class Chunk
            {
                public byte CommandByte { get; set; }
                public List<PixelRun> Runs { get; set; } = new List<PixelRun>();
                public void AddRun(int length, int color)
                {
                    this.Runs.Add(new PixelRun { Length = length, Color = color });
                }
            }
            public class Mip
            {
                private readonly Stream stream;
                private readonly LRLEFormat version;
                private readonly int[] palette;

                public int Width { get; set; }
                public int Height { get; set; }
                public long Start { get; }
                public long End { get; }
                public long Length => End - Start;

                public int Index { get; }

                public Mip(Stream stream, int index, LRLEFormat version, int[] palette, long start, long end)
                {
                    this.stream = stream;
                    this.version = version;
                    this.palette = palette;

                    this.Index = index;
                    this.Start = start;
                    this.End = end;
                }
                public byte[] GetPixels()
                {
                    var pixels = new byte[Width * Height * 4];
                    int pixelsWritten = 0;
                    byte[] color;
                    foreach (var chunk in Read())
                    {
                        foreach (var run in chunk.Runs)
                        {
                            color = BitConverter.GetBytes(run.Color);
                            for (int j = 0; j < run.Length; j++)
                                Array.Copy(color, 0, pixels, 4 * LRLEUtility.BlockIndexToScanlineIndex(pixelsWritten++, Width), 4);
                        }
                    }
                    return pixels;
                }
                public IEnumerable<Chunk> Read()
                {
                    var s = new BinaryReader(stream);
                    while (s.BaseStream.Position < End)
                    {
                        switch (version)
                        {
                            case LRLEFormat.Default:
                                yield return Read0000Chunk(s);
                                break;
                            case LRLEFormat.V002:
                                yield return ReadV002Chunk(s, palette);
                                break;
                        }
                    }
                }
                public Chunk Read0000Chunk(BinaryReader s)
                {
                    var cmdLengthByte = s.ReadByte();
                    var cmdByte = cmdLengthByte & 3;
                    var chunk = new Chunk { CommandByte = cmdLengthByte };
                    switch (cmdByte)
                    {
                        case 0:
                            ReadBlankPixelRepeat(s, cmdLengthByte, chunk);
                            break;
                        case 1:
                            ReadInlinePixelSequence(s, cmdLengthByte, chunk);
                            break;
                        case 2:
                            ReadInlinePixelRepeat(s, cmdLengthByte, chunk);
                            break;
                        case 3:
                            ReadEmbeddedRLE(s, cmdLengthByte, chunk);
                            break;

                    }
                    return chunk;
                }

                private void ReadPixelSequence(BinaryReader s, int[] palette, List<byte> lengthBytes, byte cmdByte, Chunk chunk)
                {
                    var count = ReadPackedInt(lengthBytes, 2);
                    var flags = (cmdByte & 3) >> 1;
                    var color = 0;
                    for (int j = 0; j < count; j++)
                    {

                        switch (flags)
                        {
                            case 0:
                                color = palette[ReadPackedInt(ReadHighBitSequence(s), 0)];
                                break;
                            case 1:
                                color = s.ReadInt32();
                                break;

                        }
                        chunk.AddRun(1, color);
                    }
                }

                private void ReadPixelRepeat(BinaryReader s, int[] palette, List<byte> lengthBytes, byte cmdByte, Chunk chunk)
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
                    chunk.AddRun(count, color);
                }
                private static void ReadEmbeddedRLE(BinaryReader s, byte lengthByte, Chunk chunk)
                {
                    var count = lengthByte >> 2;
                    var pixelBuffer = new byte[4 * count];
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
                        chunk.AddRun(1, BitConverter.ToInt32(new byte[] {
                            pixelBuffer[i],
                            pixelBuffer[i+ count],
                            pixelBuffer[i+ 2*count],
                            pixelBuffer[i+ 3*count]
                        }, 0));

                    }
                }

                private static void ReadInlinePixelSequence(BinaryReader s, byte lengthByte, Chunk chunk)
                {
                    var count = lengthByte >> 2;
                    for (int j = 0; j < count; j++)
                    {
                        chunk.AddRun(1, s.ReadInt32());
                    }
                }

                private static void ReadInlinePixelRepeat(BinaryReader s, byte lengthByte, Chunk chunk)
                {
                    var lengthBytes = ReadHighBitSequence(s, lengthByte);
                    var count = ReadPackedInt(lengthBytes, 2);
                    chunk.AddRun(count, s.ReadInt32());
                }

                private static void ReadBlankPixelRepeat(BinaryReader s, byte lengthByte, Chunk chunk)
                {
                    var lengthBytes = ReadHighBitSequence(s, lengthByte);
                    var count = ReadPackedInt(lengthBytes, 2);
                    chunk.AddRun(count, 0);
                }

                Chunk ReadV002Chunk(BinaryReader s, int[] palette)
                {

                    var lengthBytes = ReadHighBitSequence(s).ToList();
                    var cmdByte = lengthBytes[0];
                    var commandType = cmdByte & 1;
                    var offset = s.BaseStream.Position;
                    var chunk = new Chunk { CommandByte = cmdByte };
                    switch (commandType)
                    {
                        case 0:
                            ReadPixelRepeat(s, palette, lengthBytes, cmdByte, chunk);
                            break;
                        case 1:
                            ReadPixelSequence(s, palette, lengthBytes, cmdByte, chunk);
                            break;
                    }
                    return chunk;
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
        public static IEnumerable<byte> ReadHighBitSequence(BinaryReader r, byte? start = null)
        {
            byte b;
            if (start.HasValue)
            {
                yield return start.Value;
                if ((start.Value & 0x80) == 0)
                    yield break;
            }
            do
            {
                b = r.ReadByte();
                yield return b;
            } while ((b & 0x80) != 0);
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
            int i = 0;
            foreach (var b in bytes)
                u |= ((uint)b & 0x7F) << (7 * i++);
            u >>= startBit;
            return (int)u;
        }

        public static int BlockIndexToScanlineIndex(int block_index, int stride)
        {
            var block_row_size = stride >> 2 << 4;

            int block_row = block_index / block_row_size;
            int block_column = block_index % block_row_size >> 4;

            int block_index_row = (block_index >> 2) & 3;
            int block_index_column = block_index & 3;

            int scanline_row = (block_row << 2) + block_index_row;
            int scanline_column = (block_column << 2) + block_index_column;
            int scanline_index = (scanline_row * stride) + scanline_column;
            return scanline_index;

        }
        public static List<PixelRun> ExtractPixelRuns(byte[] argbPixels, int stride)
        {
            int? lastColor = null;
            int runLength = 0;
            var runs = new List<PixelRun>();
            var pixels = argbPixels.Length >> 2;
            for (int i = 0; i < pixels; i++)
            {
                var index = BlockIndexToScanlineIndex(i, stride);

                var c = BitConverter.ToInt32(argbPixels, index * 4);
                if (lastColor == null)
                {
                    lastColor = c;
                    runLength++;
                }
                else if (lastColor == c)
                {
                    runLength++;
                }
                else
                {
                    runs.Add(new PixelRun { Color = lastColor.Value, Length = runLength });
                    lastColor = c;
                    runLength = 0;
                    runLength++;
                }
            }
            runs.Add(new PixelRun { Color = lastColor.Value, Length = runLength });
            return runs;
        }
    }
}
