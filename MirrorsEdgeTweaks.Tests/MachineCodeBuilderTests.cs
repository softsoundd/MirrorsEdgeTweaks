using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public class MachineCodeBuilderTests
    {
        [Fact]
        public void ShortJumps_EncodeRel8_ForwardAndBackward()
        {
            var mc = new MachineCodeBuilder(0x1000);
            mc.MarkLabel("top");
            mc.EmitJzShort("end");
            mc.EmitNop(3);
            mc.EmitJmpShort("top");
            mc.MarkLabel("end");

            Assert.Equal(new byte[] { 0x74, 0x05, 0x90, 0x90, 0x90, 0xEB, 0xF9 }, mc.Build());
        }

        [Fact]
        public void CallToLabel_EncodesRel32()
        {
            var mc = new MachineCodeBuilder(0x1000);
            mc.EmitCall("sub");
            mc.EmitNop(2);
            mc.MarkLabel("sub");
            mc.Emit(0xC3);

            Assert.Equal(new byte[] { 0xE8, 0x02, 0x00, 0x00, 0x00, 0x90, 0x90, 0xC3 }, mc.Build());
        }

        [Fact]
        public void ShortJump_Throws_WhenTheLabelIsOutOfRange()
        {
            var mc = new MachineCodeBuilder(0x1000);
            mc.EmitJnzShort("far");
            mc.EmitNop(128);
            mc.MarkLabel("far");

            Assert.Throws<InvalidOperationException>(() => mc.Build());
        }
    }
}
