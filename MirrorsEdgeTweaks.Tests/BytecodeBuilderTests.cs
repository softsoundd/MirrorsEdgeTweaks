using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class BytecodeBuilderTests
    {
        [Fact]
        public void F32_EmitsLittleEndianFloat()
        {
            Assert.Equal(new byte[] { 0x00, 0x00, 0xB4, 0x42 }, BytecodeBuilder.F32(90f));
        }

        [Fact]
        public void U16_EmitsLittleEndian()
        {
            Assert.Equal(new byte[] { 0x34, 0x12 }, BytecodeBuilder.U16(0x1234));
        }

        [Fact]
        public void I32_EmitsLittleEndian()
        {
            Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, BytecodeBuilder.I32(0x12345678));
        }

        [Fact]
        public void InstVar_EmitsOpcodePlusIndex()
        {
            byte[] token = BytecodeBuilder.InstVar(0x0102);

            Assert.Equal(BytecodeBuilder.VAR_TOKEN_SIZE, token.Length);
            Assert.Equal(BytecodeBuilder.OP_INST_VAR, token[0]);
            Assert.Equal(new byte[] { 0x02, 0x01, 0x00, 0x00 }, token[1..]);
        }

        [Fact]
        public void LocalVar_EmitsOpcodePlusIndex()
        {
            byte[] token = BytecodeBuilder.LocalVar(7);

            Assert.Equal(BytecodeBuilder.OP_LOCAL_VAR, token[0]);
            Assert.Equal(7, BitConverter.ToInt32(token, 1));
        }

        [Fact]
        public void FloatConst_EmitsOpcodePlusValue()
        {
            byte[] token = BytecodeBuilder.FloatConst(1.5f);

            Assert.Equal(BytecodeBuilder.OP_FLOAT_CONST, token[0]);
            Assert.Equal(1.5f, BitConverter.ToSingle(token, 1));
        }

        [Fact]
        public void JumpTokens_EmitOpcodePlusTarget()
        {
            Assert.Equal(new byte[] { BytecodeBuilder.OP_JUMP, 0x10, 0x00 }, BytecodeBuilder.Jump(0x10));
            Assert.Equal(new byte[] { BytecodeBuilder.OP_JUMP_IF_NOT, 0x22, 0x11 }, BytecodeBuilder.JumpIfNot(0x1122));
        }

        [Fact]
        public void Context_LayoutIsOpcodeObjectSkipTypeInner()
        {
            byte[] obj = { 0xAA };
            byte[] inner = { 0xBB, 0xCC };

            byte[] token = BytecodeBuilder.Context(obj, skipSize: 0x0002, propertyType: 4, inner);

            Assert.Equal(BytecodeBuilder.OP_CONTEXT, token[0]);
            Assert.Equal(0xAA, token[1]);
            Assert.Equal(0x0002, BitConverter.ToUInt16(token, 2));
            Assert.Equal(4, BitConverter.ToUInt16(token, 4));
            Assert.Equal(new byte[] { 0xBB, 0xCC }, token[6..]);
        }

        [Fact]
        public void ContextHudProperty_NestsSkipSizes()
        {
            byte[] pcOwner = BytecodeBuilder.InstVar(1);
            byte[] myHud = BytecodeBuilder.InstVar(2);
            byte[] property = BytecodeBuilder.InstVar(3);

            byte[] token = BytecodeBuilder.ContextHudProperty(pcOwner, myHud, property);

            // Outer: 0x19 + pcOwner + skip(u16) + type(u16); inner starts after that.
            Assert.Equal(BytecodeBuilder.OP_CONTEXT, token[0]);
            int innerLength = 1 + myHud.Length + 4 + property.Length;
            ushort outerSkip = BitConverter.ToUInt16(token, 1 + pcOwner.Length);
            Assert.Equal(innerLength, outerSkip);
        }

        [Fact]
        public void BoolAnd_EmitsShortCircuitSkipOverSecondExpression()
        {
            byte[] expr1 = { 0x27 }; // OP_TRUE
            byte[] expr2 = { 0x28 }; // OP_FALSE

            byte[] token = BytecodeBuilder.BoolAnd(expr1, expr2);

            Assert.Equal(BytecodeBuilder.OP_BOOL_AND, token[0]);
            Assert.Equal(0x27, token[1]);
            Assert.Equal(BytecodeBuilder.OP_SKIP, token[2]);
            // Skip = second expression + the trailing EndFP marker.
            Assert.Equal(expr2.Length + 1, BitConverter.ToUInt16(token, 3));
            Assert.Equal(BytecodeBuilder.OP_END_FP, token[^1]);
        }

        [Fact]
        public void Concat_JoinsArraysInOrder()
        {
            byte[] joined = BytecodeBuilder.Concat(
                new byte[] { 1, 2 },
                Array.Empty<byte>(),
                new byte[] { 3 });

            Assert.Equal(new byte[] { 1, 2, 3 }, joined);
        }

        [Fact]
        public void FName_EmitsIndexAndZeroNumber()
        {
            byte[] token = BytecodeBuilder.FName(0x0405);

            Assert.Equal(8, token.Length);
            Assert.Equal(0x0405, BitConverter.ToInt32(token, 0));
            Assert.Equal(0, BitConverter.ToInt32(token, 4));
        }

        [Fact]
        public void FindPattern_DelegatesWithWindow()
        {
            byte[] data = { 0x00, 0x01, 0x02, 0x01, 0x02 };

            Assert.Equal(1, BytecodeBuilder.FindPattern(data, new byte[] { 0x01, 0x02 }));
            Assert.Equal(3, BytecodeBuilder.FindPattern(data, new byte[] { 0x01, 0x02 }, start: 2));
            Assert.Equal(-1, BytecodeBuilder.FindPattern(data, new byte[] { 0x01, 0x02 }, start: 2, end: 4));
        }

        static byte[] BuildSensBlobTestTokens()
        {
            byte[] fovscaleLocal = BytecodeBuilder.LocalVar(1);
            byte[] outerVar = BytecodeBuilder.InstVar(2);
            byte[] getfovVf = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_VIRT_FUNC },
                BytecodeBuilder.FName(100));
            byte[] sncpVf = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_VIRT_FUNC },
                BytecodeBuilder.FName(101));
            byte[] instPawn = BytecodeBuilder.InstVar(10);
            byte[] instWeapon = BytecodeBuilder.InstVar(11);
            byte[] tdweaponDcast = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_DYNAMIC_CAST },
                BytecodeBuilder.I32(12));
            byte[] isZoomingVf = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_VIRT_FUNC },
                BytecodeBuilder.FName(200));
            byte[] instFovangle = BytecodeBuilder.InstVar(13);
            byte[] instDefaultFov = BytecodeBuilder.InstVar(14);

            return BytecodeBuilder.BuildSensBlob(
                insertBc: 0,
                fovscaleLocal, outerVar, getfovVf, sncpVf,
                myhudImp: 20, sizexImp: 21, sizeyImp: 22,
                instPawn, instWeapon, tdweaponDcast, isZoomingVf, instFovangle, instDefaultFov,
                enableSens: false, enableClip: true);
        }

        [Fact]
        public void BuildSensBlob_clipOnly_requiresDefaultFovTokens()
        {
            Assert.Throws<InvalidOperationException>(() => BytecodeBuilder.BuildSensBlob(
                insertBc: 0,
                BytecodeBuilder.LocalVar(1), BytecodeBuilder.InstVar(2),
                new byte[] { BytecodeBuilder.OP_VIRT_FUNC }, new byte[] { BytecodeBuilder.OP_VIRT_FUNC },
                myhudImp: 20, sizexImp: 21, sizeyImp: 22,
                BytecodeBuilder.InstVar(10), BytecodeBuilder.InstVar(11),
                new byte[] { BytecodeBuilder.OP_DYNAMIC_CAST, 0, 0, 0, 0 },
                new byte[] { BytecodeBuilder.OP_VIRT_FUNC },
                BytecodeBuilder.InstVar(13), Array.Empty<byte>(),
                enableSens: false, enableClip: true));
        }

        [Fact]
        public void BuildStockZoomTargetFovExpr_closesEveryNestedOp()
        {
            byte[] defaultFov = BytecodeBuilder.InstVar(1);
            byte[] fovAngle = BytecodeBuilder.InstVar(2);

            byte[] zoomDelta = BytecodeBuilder.BuildStockZoomDeltaExpr(defaultFov, fovAngle);
            byte[] zoomTarget = BytecodeBuilder.BuildStockZoomTargetFovExpr(defaultFov, fovAngle);

            Assert.Equal(2, zoomDelta.Count(b => b == BytecodeBuilder.OP_END_FP));
            Assert.Equal(3, zoomTarget.Count(b => b == BytecodeBuilder.OP_END_FP));
        }

        [Fact]
        public void BuildSensBlob_clipOnly_includesZoomGuard()
        {
            byte[] blob = BuildSensBlobTestTokens();
            byte[] zoomNearClip = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_FLOAT_CONST },
                BytecodeBuilder.F32(BytecodeBuilder.K_STOCK_ZOOM_DELTA));
            byte[] isZoomingVf = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_VIRT_FUNC },
                BytecodeBuilder.FName(200));

            Assert.NotEqual(-1, BytecodeBuilder.FindPattern(blob, zoomNearClip));
            Assert.NotEqual(-1, BytecodeBuilder.FindPattern(blob, isZoomingVf));
            Assert.Contains(BytecodeBuilder.OP_LESS_EQUAL_FF, blob);
            Assert.Contains(BytecodeBuilder.OP_FMAX, blob);
        }

        [Fact]
        public void BuildSensBlob_clipBranch_usesGetFovOnlyWhenNotZoomed()
        {
            byte[] blob = BuildSensBlobTestTokens();
            byte[] isZoomingVf = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_VIRT_FUNC },
                BytecodeBuilder.FName(200));

            int zoomIdx = BytecodeBuilder.FindPattern(blob, isZoomingVf);
            Assert.NotEqual(-1, zoomIdx);

            int jumpIdx = Array.IndexOf(blob, BytecodeBuilder.OP_JUMP, zoomIdx);
            Assert.True(jumpIdx > zoomIdx, "Expected Jump after zoom near-clip path");

            int tanIdx = Array.IndexOf(blob, BytecodeBuilder.OP_TAN, jumpIdx);
            Assert.True(tanIdx > jumpIdx, "Expected Tan(GetFOVAngle()) only on non-zoom path");
        }
    }
}
