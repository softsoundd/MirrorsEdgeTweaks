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

            // The outer context is 0x19, pcOwner, a 2-byte skip size and a 2-byte property type, and the inner context follows.
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
            // The skip size covers the second expression and the trailing EndFunctionParms.
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

        static readonly byte[] DefaultFovToken = BytecodeBuilder.InstVar(0x0B05);
        static readonly byte[] DefaultArToken = BytecodeBuilder.InstVar(0x0AFC);

        // Builds the CameraCache.POV.FOV read with the object references of a stock Engine.u.
        static byte[] CameraCacheFovRead() => BytecodeBuilder.StructMember(
            BytecodeBuilder.I32(-57), BytecodeBuilder.I32(-151),
            BytecodeBuilder.StructMember(BytecodeBuilder.I32(0x0B08), BytecodeBuilder.I32(0x0B0A), BytecodeBuilder.InstVar(0x0AF3)));

        [Fact]
        public void StructMember_matchesStockCameraCacheFovRead()
        {
            byte[] stock =
            {
                0x35, 0xC7, 0xFF, 0xFF, 0xFF, 0x69, 0xFF, 0xFF, 0xFF, 0x00, 0x00,
                0x35, 0x08, 0x0B, 0x00, 0x00, 0x0A, 0x0B, 0x00, 0x00, 0x00, 0x00,
                0x01, 0xF3, 0x0A, 0x00, 0x00,
            };

            Assert.Equal(stock, CameraCacheFovRead());
        }

        [Fact]
        public void BuildBlobA_jumpsLandOnElseBranchAndEnd()
        {
            const int insertBc = 0x6F;
            byte[] blob = BytecodeBuilder.BuildBlobA(insertBc, DefaultFovToken, DefaultArToken, CameraCacheFovRead());

            byte[] elseBody = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_LET }, DefaultArToken,
                new byte[] { BytecodeBuilder.OP_SUBTRACT_PRE_F }, DefaultFovToken, BytecodeBuilder.EndFP());
            Assert.Equal(elseBody, blob[^elseBody.Length..]);
            int elseBc = insertBc + blob.Length - elseBody.Length;

            Assert.Equal(BytecodeBuilder.OP_JUMP_IF_NOT, blob[0]);
            Assert.Equal(elseBc, BitConverter.ToUInt16(blob, 1));

            int jumpIdx = elseBc - insertBc - 3;
            Assert.Equal(BytecodeBuilder.OP_JUMP, blob[jumpIdx]);
            Assert.Equal(insertBc + blob.Length, BitConverter.ToUInt16(blob, jumpIdx + 1));
        }

        [Fact]
        public void BuildBlobA_restoresOnlyWhileDefaultFovMatchesCameraCache()
        {
            byte[] cameraCacheFov = CameraCacheFovRead();
            byte[] blob = BytecodeBuilder.BuildBlobA(0, DefaultFovToken, DefaultArToken, cameraCacheFov);

            byte[] comparison = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_EQUAL_EQUAL_FF }, DefaultFovToken, cameraCacheFov, BytecodeBuilder.EndFP());
            byte[] restore = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_LET }, DefaultFovToken,
                new byte[] { BytecodeBuilder.OP_SUBTRACT_PRE_F }, DefaultArToken, BytecodeBuilder.EndFP());
            int comparisonIdx = BytecodeBuilder.FindPattern(blob, comparison);
            int restoreIdx = BytecodeBuilder.FindPattern(blob, restore);

            Assert.NotEqual(-1, comparisonIdx);
            Assert.True(restoreIdx > comparisonIdx, "Expected the restore to follow the CameraCache comparison");
        }

        [Fact]
        public void PhaseTwoSignatures_matchOnlyTheirOwnBlobA()
        {
            // Holds blob A as releases up to the 4.5.0 betas inserted it at script offset 0x6F.
            byte[] legacyBlobA =
            {
                0x07, 0x8C, 0x00, 0xB1, 0x01, 0xFC, 0x0A, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x20, 0x41, 0x16,
                0x0F, 0x01, 0x05, 0x0B, 0x00, 0x00, 0x01, 0xFC, 0x0A, 0x00, 0x00,
                0x06, 0x97, 0x00,
                0x0F, 0x01, 0xFC, 0x0A, 0x00, 0x00, 0x01, 0x05, 0x0B, 0x00, 0x00,
            };
            byte[] blobA = BytecodeBuilder.BuildBlobA(0x6F, DefaultFovToken, DefaultArToken, CameraCacheFovRead());
            byte[] signature = BytecodeBuilder.GetP2Signature(DefaultArToken);
            byte[] legacySignature = BytecodeBuilder.GetLegacyP2Signature(DefaultArToken);

            Assert.Equal(3, BytecodeBuilder.FindPattern(blobA, signature));
            Assert.Equal(-1, BytecodeBuilder.FindPattern(blobA, legacySignature));
            Assert.Equal(3, BytecodeBuilder.FindPattern(legacyBlobA, legacySignature));
            Assert.Equal(-1, BytecodeBuilder.FindPattern(legacyBlobA, signature));
        }

        [Fact]
        public void ShiftJumpTargets_movesOnlyTargetsAtOrAfterTheThreshold()
        {
            // The float's payload starts with 07 0D 00, which a byte scan would read as a JumpIfNot past the insertion point.
            byte[] looksLikeJump = BytecodeBuilder.FloatConst(BitConverter.ToSingle(new byte[] { 0x07, 0x0D, 0x00, 0x40 }));

            byte[] script = BytecodeBuilder.Concat(
                BytecodeBuilder.JumpIfNot(15), new byte[] { BytecodeBuilder.OP_TRUE },
                BytecodeBuilder.Jump(0),
                looksLikeJump,
                new byte[] { BytecodeBuilder.OP_CASE, 0xFF, 0xFF },
                new byte[] { BytecodeBuilder.OP_END_OF_SCRIPT });
            int[] jumpPositions = { 0, 4, 12 };
            const int insertBc = 12;
            byte[] inserted = { BytecodeBuilder.OP_NOTHING, BytecodeBuilder.OP_NOTHING };

            PackageSplicer.ShiftJumpTargets(script, 0, jumpPositions, insertBc, inserted.Length);
            byte[] patched = PackageSplicer.InsertBytes(script, insertBc, inserted);

            Assert.Equal(15 + inserted.Length, BitConverter.ToUInt16(patched, 1));
            Assert.Equal(BytecodeBuilder.OP_END_OF_SCRIPT, patched[15 + inserted.Length]);
            Assert.Equal(0, BitConverter.ToUInt16(patched, 5));
            Assert.Equal(looksLikeJump, patched[7..12]);
            Assert.Equal(0xFFFF, BitConverter.ToUInt16(patched, insertBc + inserted.Length + 1));
        }

        [Fact]
        public void ShiftJumpTargets_rejectsAnOffsetThatIsNotAJump()
        {
            byte[] script = BytecodeBuilder.Concat(BytecodeBuilder.FloatConst(1.0f), new byte[] { BytecodeBuilder.OP_END_OF_SCRIPT });

            Assert.Throws<InvalidOperationException>(() =>
                PackageSplicer.ShiftJumpTargets(script, 0, new[] { 0 }, 0, 4));
        }

        [Fact]
        public void BuildStockArAssignment_usesTheStockLiteral()
        {
            byte[] assignment = BytecodeBuilder.BuildStockArAssignment(BytecodeBuilder.InstVar(0x0AFD));

            Assert.Equal(new byte[] { 0xF3, 0x8E, 0xE3, 0x3F }, assignment[^4..]);
        }
    }
}
