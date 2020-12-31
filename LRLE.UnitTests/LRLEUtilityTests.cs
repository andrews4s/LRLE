using Xunit;
using static LRLE.LRLEUtility;
namespace LRLETests
{
    public class LRLEUtilityTests
    {
        [Theory]
        [InlineData(new byte[] { 0xDA, 0x90, 0x05, },2, 10507,3)]
        [InlineData(new byte[] { 0x12, }, 2,  2, 3)]
        [InlineData(new byte[] { 0xEA, 0x84, 0xC7, 0x02 },2, 669773, 3)]
        [InlineData(new byte[] { 0x00 }, 0, 0, 0)]
        public void TestWritePackedInt(byte[] expectedBytes, int command, int count, int startBit)
        {
            var result = WritePackedInt(count, command, startBit);
            Assert.Equal(expectedBytes, result);
        }
        [Theory]
        [InlineData(new byte[] { 0xDA, 0x90, 0x05, }, 10507, 3)]
        [InlineData(new byte[] { 0x12, }, 2, 3)]
        public void TestReadPackedInt(byte[] bytes, int expectedCount, int startBit)
        {
            var result = ReadPackedInt(bytes, startBit);
            Assert.Equal(expectedCount, result);
        }

        [Theory]
        [InlineData(128, 0, (0 * 128) + (0 * 4) + 0)]
        [InlineData(128, 1, (0 * 128) + (0 * 4) + 1)]
        [InlineData(128, 2, (0 * 128) + (0 * 4) + 2)]
        [InlineData(128, 3, (0 * 128) + (0 * 4) + 3)]
        [InlineData(128, 4, (1 * 128) + (0 * 4) + 0)]
        [InlineData(128, 5, (1 * 128) + (0 * 4) + 1)]
        [InlineData(128, 6, (1 * 128) + (0 * 4) + 2)]
        [InlineData(128, 7, (1 * 128) + (0 * 4) + 3)]
        [InlineData(128, 8, (2 * 128) + (0 * 4) + 0)]
        [InlineData(128, 9, (2 * 128) + (0 * 4) + 1)]
        [InlineData(128, 10, (2 * 128) + (0 * 4) + 2)]
        [InlineData(128, 11, (2 * 128) + (0 * 4) + 3)]
        [InlineData(128, 12, (3 * 128) + (0 * 4) + 0)]
        [InlineData(128, 13, (3 * 128) + (0 * 4) + 1)]
        [InlineData(128, 14, (3 * 128) + (0 * 4) + 2)]
        [InlineData(128, 15, (3 * 128) + (0 * 4) + 3)]
        [InlineData(128, 16, (0 * 128) + (1 * 4) + 0)]
        [InlineData(128, (128 * 4) + 1, (4 * 128) + (0 * 4) + 1)]
        public void TestBlockIndexToScanLineIndex(int stride, int block_index, int scanline_index)
        {
            Assert.Equal(scanline_index, BlockIndexToScanlineIndex(block_index, stride));
        }
    }
}
