using System;
using System.Collections.Generic;

namespace MirrorsEdgeTweaks.Services
{
    // Low level UE3 bytecode construction for Mirror's Edge (v536)
    public static class BytecodeBuilder
    {
        public const byte OP_LOCAL_VAR = 0x00;
        public const byte OP_INST_VAR = 0x01;
        public const byte OP_RETURN = 0x04;
        public const byte OP_JUMP = 0x06;
        public const byte OP_JUMP_IF_NOT = 0x07;
        public const byte OP_CASE = 0x0A;
        public const byte OP_NOTHING = 0x0B;
        public const byte OP_LET = 0x0F;
        public const byte OP_LET_BOOL = 0x14;
        public const byte OP_END_FP = 0x16;
        public const byte OP_CONTEXT = 0x19;
        public const byte OP_VIRT_FUNC = 0x1B;
        public const byte OP_FINAL_FUNC = 0x1C;
        public const byte OP_FLOAT_CONST = 0x1E;
        public const byte OP_BYTE_CONST = 0x24;
        public const byte OP_TRUE = 0x27;
        public const byte OP_FALSE = 0x28;
        public const byte OP_NONE = 0x2A;
        public const byte OP_BOOL_VAR = 0x2D;
        public const byte OP_DYNAMIC_CAST = 0x2E;
        public const byte OP_STRUCT_MEMBER = 0x35;
        public const byte OP_DELEGATE_FUNC = 0x42;
        public const byte OP_END_OF_SCRIPT = 0x53;
        public const byte OP_NOT_EQ_OBJ = 0x77;
        public const byte OP_NOT_PRE_BOOL = 0x81;
        public const byte OP_BOOL_AND = 0x82;
        public const byte OP_MULTIPLY_FF = 0xAB;
        public const byte OP_DIVIDE_FF = 0xAC;
        public const byte OP_ADD_FF = 0xAE;
        public const byte OP_SUBTRACT_FF = 0xAF;
        public const byte OP_GREATER_FF = 0xB1;
        public const byte OP_MULTIPLY_EQ_FF = 0xB6;
        public const byte OP_TAN = 0xBD;
        public const byte OP_ATAN = 0xBE;
        public const byte OP_FMIN = 0xF4;
        public const byte OP_FMAX = 0xF5;
        public const byte OP_SKIP = 0x18;

        public const float REF_AR = 16.0f / 9.0f;
        public static readonly double K_DEG_TO_HALFRAD = Math.PI / 360.0;
        public static readonly double K_HALFRAD_TO_DEG = 360.0 / Math.PI;
        public const float K_SENS = 0.01111f;
        public static readonly float K_CLIP_HOR = 10.0f * REF_AR;       // 17.7778
        public static readonly float K_CLIP_VERT = 10.0f / REF_AR;      // 5.625
        public const float K_BASE_SENS = 90.0f * K_SENS;                // 0.9999
        public const float K_VERTIGO_DELTA = 6.0f;
        public const float K_STOCK_ZOOM_DELTA = 20.0f;

        public const int SCRIPT_HDR = 0x2C;
        public const int BSS_REL = 0x28;

        // Primitive token builders

        public static byte[] F32(float val)
        {
            return BitConverter.GetBytes(val);
        }

        public static byte[] U16(ushort val)
        {
            return BitConverter.GetBytes(val);
        }

        public static byte[] I32(int val)
        {
            return BitConverter.GetBytes(val);
        }

        public static byte[] InstVar(int packageIndex)
        {
            var b = new List<byte> { OP_INST_VAR };
            b.AddRange(I32(packageIndex));
            return b.ToArray();
        }

        public static byte[] LocalVar(int packageIndex)
        {
            var b = new List<byte> { OP_LOCAL_VAR };
            b.AddRange(I32(packageIndex));
            return b.ToArray();
        }

        public static byte[] FloatConst(float val)
        {
            var b = new List<byte> { OP_FLOAT_CONST };
            b.AddRange(F32(val));
            return b.ToArray();
        }

        public static byte[] JumpIfNot(ushort target)
        {
            var b = new List<byte> { OP_JUMP_IF_NOT };
            b.AddRange(U16(target));
            return b.ToArray();
        }

        public static byte[] Jump(ushort target)
        {
            var b = new List<byte> { OP_JUMP };
            b.AddRange(U16(target));
            return b.ToArray();
        }

        public static byte[] VirtualFunc(byte[] nameToken)
        {
            var b = new List<byte> { OP_VIRT_FUNC };
            b.AddRange(nameToken);
            return b.ToArray();
        }

        public static byte[] EndFP()
        {
            return new byte[] { OP_END_FP };
        }

        // Context access builders

        // Context(object_expr)[skip_size, property_type] inner_expr
        // v536: property_type is uint16
        public static byte[] Context(byte[] objectExpr, ushort skipSize, ushort propertyType, byte[] innerExpr)
        {
            var b = new List<byte> { OP_CONTEXT };
            b.AddRange(objectExpr);
            b.AddRange(U16(skipSize));
            b.AddRange(U16(propertyType));
            b.AddRange(innerExpr);
            return b.ToArray();
        }

        // PCOwner.myHUD.property, double nested context access
        public static byte[] ContextHudProperty(byte[] pcOwner, byte[] myHud, byte[] property)
        {
            byte[] inner = Context(myHud, (ushort)property.Length, 4, property);
            return Context(pcOwner, (ushort)inner.Length, 4, inner);
        }

        // StructMember access: struct.field via TPOV/FOV import indices.
        public static byte[] StructMemberFov(byte[] impFov, byte[] impTpov, byte[] localVar)
        {
            var b = new List<byte> { OP_STRUCT_MEMBER };
            b.AddRange(impFov);
            b.AddRange(impTpov);
            b.Add(0x00);
            b.Add(0x00);
            b.AddRange(localVar);
            return b.ToArray();
        }

        public static byte[] BoolAnd(byte[] expr1, byte[] expr2)
        {
            ushort skipSize = (ushort)(expr2.Length + 1);
            var b = new List<byte> { OP_BOOL_AND };
            b.AddRange(expr1);
            b.Add(OP_SKIP);
            b.AddRange(U16(skipSize));
            b.AddRange(expr2);
            b.AddRange(EndFP());
            return b.ToArray();
        }

        // Engine.u blob builders (phase 2)

        // Point A: save/restore original DefaultFOV via DefaultAspectRatio (40 bytes).
        public static byte[] BuildBlobA(int insertBc, byte[] instDefaultFov, byte[] instDefaultAr)
        {
            // if (DefaultAspectRatio > 10.0) DefaultFOV = DefaultAspectRatio;
            // else DefaultAspectRatio = DefaultFOV;
            byte[] ifBody = Concat(new byte[] { OP_LET }, instDefaultFov, instDefaultAr);
            byte[] elseBody = Concat(new byte[] { OP_LET }, instDefaultAr, instDefaultFov);
            byte[] condition = Concat(
                new byte[] { OP_GREATER_FF }, instDefaultAr,
                FloatConst(10.0f), EndFP());

            int jnotTarget = insertBc + 3 + condition.Length + ifBody.Length + 3;
            int jumpTarget = insertBc + 3 + condition.Length + ifBody.Length + 3 + elseBody.Length;

            byte[] blob = Concat(
                JumpIfNot((ushort)jnotTarget),
                condition, ifBody,
                Jump((ushort)jumpTarget),
                elseBody);

            if (blob.Length != 40)
                throw new InvalidOperationException($"Blob A size mismatch: expected 40, got {blob.Length}");
            return blob;
        }

        // Point B: compute AR from HUD, scale FOV if wider than 16:9 (variable size, typically ~120 bytes)
        public static byte[] BuildBlobB(int insertBc,
            byte[] localBlendPct, byte[] localNewPov,
            byte[] instPcOwner, byte[] instMyHud,
            byte[] instSizeX, byte[] instSizeY,
            byte[] instDefaultFov,
            byte[] impFov, byte[] impTpov)
        {
            // BlendPct = PCOwner.myHUD.SizeX / PCOwner.myHUD.SizeY
            byte[] part1 = Concat(
                new byte[] { OP_LET }, localBlendPct,
                new byte[] { OP_DIVIDE_FF },
                ContextHudProperty(instPcOwner, instMyHud, instSizeX),
                ContextHudProperty(instPcOwner, instMyHud, instSizeY),
                EndFP());

            byte[] fovRead = StructMemberFov(impFov, impTpov, localNewPov);

            // NewPOV.FOV = Atan(Tan(FOV * Pi/360) * AR / (16/9), 1.0) * 360/Pi
            byte[] part3 = Concat(
                new byte[] { OP_LET }, StructMemberFov(impFov, impTpov, localNewPov),
                new byte[] { OP_MULTIPLY_FF },
                new byte[] { OP_ATAN },
                new byte[] { OP_DIVIDE_FF },
                new byte[] { OP_MULTIPLY_FF },
                new byte[] { OP_TAN },
                new byte[] { OP_MULTIPLY_FF },
                fovRead,
                FloatConst((float)K_DEG_TO_HALFRAD), EndFP(),
                EndFP(),
                localBlendPct, EndFP(),
                FloatConst(REF_AR), EndFP(),
                FloatConst(1.0f), EndFP(),
                FloatConst((float)K_HALFRAD_TO_DEG), EndFP());

            // DefaultFOV = NewPOV.FOV
            byte[] part4 = Concat(
                new byte[] { OP_LET }, instDefaultFov,
                StructMemberFov(impFov, impTpov, localNewPov));

            // if (BlendPct > 16/9) { part3 + part4 }
            int skipTarget = insertBc + part1.Length + 15 + part3.Length + part4.Length;
            byte[] part2 = Concat(
                JumpIfNot((ushort)skipTarget),
                new byte[] { OP_GREATER_FF },
                localBlendPct,
                FloatConst(REF_AR), EndFP());

            if (part2.Length != 15)
                throw new InvalidOperationException($"Part2 size mismatch: expected 15, got {part2.Length}");

            return Concat(part1, part2, part3, part4);
        }

        // TdGame.u Blob Builders

        // ToggleZoomState: dynamic near clipping plane blob.
        // Replaces SetNearClippingPlane(10) with AR aware formula.
        public static byte[] BuildClipBlob(int insertBc,
            byte[] dcast, byte[] powner, byte[] vfunc,
            int sizexImp, int sizeyImp)
        {
            byte[] sizex = InstVar(sizexImp);
            byte[] sizey = InstVar(sizeyImp);

            byte[] cond = Concat(
                new byte[] { OP_GREATER_FF }, sizex, FloatConst(0.0f), EndFP());

            // FMin(K_HOR * SizeY / SizeX, K_VERT * SizeX / SizeY)
            byte[] fminArgA = Concat(
                new byte[] { OP_DIVIDE_FF },
                new byte[] { OP_MULTIPLY_FF },
                FloatConst(K_CLIP_HOR), sizey, EndFP(),
                sizex, EndFP());
            byte[] fminArgB = Concat(
                new byte[] { OP_DIVIDE_FF },
                new byte[] { OP_MULTIPLY_FF },
                FloatConst(K_CLIP_VERT), sizex, EndFP(),
                sizey, EndFP());
            byte[] fminExpr = Concat(
                new byte[] { OP_FMIN }, fminArgA, fminArgB, EndFP());

            // Then-branch
            byte[] innerThen = Concat(vfunc, fminExpr, EndFP());
            byte[] thenCall = Context(Concat(dcast, powner), (ushort)innerThen.Length, 0, innerThen);

            // Else-branch (original: SetNearClippingPlane(10))
            byte[] innerElse = Concat(vfunc, FloatConst(10.0f), EndFP());
            byte[] elseCall = Context(Concat(dcast, powner), (ushort)innerElse.Length, 0, innerElse);

            int elseTarget = insertBc + 3 + cond.Length + thenCall.Length + 3;
            int endTarget = elseTarget + elseCall.Length;

            return Concat(
                JumpIfNot((ushort)elseTarget),
                cond, thenCall,
                Jump((ushort)endTarget),
                elseCall);
        }

        // PlayerInput: combined sensitivity + nearclip insertion.
        // Blocks are conditionally included based on toggles.
        public static byte[] BuildSensBlob(int insertBc,
            byte[] fovscaleLocal, byte[] outerVar, byte[] getfovVf,
            byte[] sncpVf,
            int myhudImp, int sizexImp, int sizeyImp,
            byte[] instPawn, byte[] instWeapon, byte[] tdweaponDcast, byte[] isZoomingVf,
            byte[] instFovangle,
            bool enableSens, bool enableClip)
        {
            byte[] outer = Concat(new byte[] { OP_INST_VAR }, outerVar.AsSpan(1).ToArray());
            byte[] myhud = InstVar(myhudImp);
            byte[] sizex = InstVar(sizexImp);
            byte[] sizey = InstVar(sizeyImp);

            byte[] CtxOuterMyhudProp(byte[] propVar)
            {
                byte[] inner2 = propVar;
                byte[] inner1 = Context(myhud, (ushort)inner2.Length, 4, inner2);
                return Context(outer, (ushort)inner1.Length, 4, inner1);
            }

            byte[] CtxOuterMyhud()
            {
                return Context(outer, 5, 4, myhud);
            }

            byte[] CtxOuterGetfov()
            {
                byte[] inner = Concat(getfovVf, EndFP());
                return Context(outer, (ushort)inner.Length, 4, inner);
            }

            byte[] CtxOuterSncp(byte[] argExpr)
            {
                byte[] inner = Concat(sncpVf, argExpr, EndFP());
                return Context(outer, (ushort)inner.Length, 0, inner);
            }

            byte[] CtxOuterProp(byte[] propVar, ushort propType = 4)
            {
                return Context(outer, (ushort)propVar.Length, propType, propVar);
            }

            byte[] CtxOuterPawnWeapon()
            {
                byte[] inner = Context(instPawn, 5, 4, instWeapon);
                return Context(outer, (ushort)inner.Length, 4, inner);
            }

            byte[] condHudNotNone = Concat(
                new byte[] { OP_NOT_EQ_OBJ }, CtxOuterMyhud(),
                new byte[] { OP_NONE }, EndFP());

            byte[] CondSizexPositive()
            {
                return Concat(
                    new byte[] { OP_GREATER_FF }, CtxOuterMyhudProp(sizex),
                    FloatConst(0.0f), EndFP());
            }

            var result = new List<byte>();
            int currentBc = insertBc;

            // Block 1: FOVScale = K_BASE_SENS (unconditional, FOV-agnostic)
            if (enableSens)
            {
                byte[] sensBody = Concat(
                    new byte[] { OP_LET }, fovscaleLocal, FloatConst(K_BASE_SENS));

                // Block 1b: weapon zoom override
                byte[] tdwExpr = Concat(tdweaponDcast, CtxOuterPawnWeapon());
                byte[] condPawnNotNone = Concat(
                    new byte[] { OP_NOT_EQ_OBJ }, CtxOuterProp(instPawn),
                    new byte[] { OP_NONE }, EndFP());
                byte[] condWeaponNotNone = Concat(
                    new byte[] { OP_NOT_EQ_OBJ }, CtxOuterPawnWeapon(),
                    new byte[] { OP_NONE }, EndFP());
                byte[] isZoomingInner = Concat(isZoomingVf, EndFP());
                byte[] condIsZooming = Context(tdwExpr, (ushort)isZoomingInner.Length, 4, isZoomingInner);

                byte[] zoomCond = BoolAnd(BoolAnd(condPawnNotNone, condWeaponNotNone), condIsZooming);
                byte[] zoomBody = Concat(
                    new byte[] { OP_LET }, fovscaleLocal,
                    new byte[] { OP_MULTIPLY_FF },
                    CtxOuterProp(instFovangle),
                    FloatConst(K_SENS), EndFP());

                int block1bEnd = currentBc + sensBody.Length + 3 + zoomCond.Length + zoomBody.Length;
                byte[] block1b = Concat(JumpIfNot((ushort)block1bEnd), zoomCond, zoomBody);

                result.AddRange(sensBody);
                result.AddRange(block1b);
                currentBc += sensBody.Length + block1b.Length;
            }

            // Block 2: FOV-aware near clip
            if (enableClip)
            {
                byte[] clipCond = BoolAnd(condHudNotNone, CondSizexPositive());

                byte[] fminPart = Concat(
                    new byte[] { OP_FMIN },
                    FloatConst(10.0f),
                    new byte[] { OP_DIVIDE_FF },
                    new byte[] { OP_MULTIPLY_FF },
                    FloatConst(K_CLIP_VERT),
                    CtxOuterMyhudProp(sizex), EndFP(),
                    CtxOuterMyhudProp(sizey), EndFP(),
                    EndFP());

                byte[] tanFov = Concat(
                    new byte[] { OP_TAN },
                    new byte[] { OP_MULTIPLY_FF },
                    CtxOuterGetfov(),
                    FloatConst((float)K_DEG_TO_HALFRAD), EndFP(),
                    EndFP());

                byte[] clipArg = Concat(new byte[] { OP_DIVIDE_FF }, fminPart, tanFov, EndFP());
                byte[] clipBody = CtxOuterSncp(clipArg);

                int block2End = currentBc + 3 + clipCond.Length + clipBody.Length;
                byte[] block2 = Concat(JumpIfNot((ushort)block2End), clipCond, clipBody);

                result.AddRange(block2);
            }

            return result.ToArray();
        }

        // TdOnlineLoginHandler.StartConnection: skip online login for offline capable modes.
        // Inserts: if (!ConnectionRequired) { OnPlayOffline(0); return; }
        public static byte[] BuildOnlineSkipBlob(int insertBc,
            byte[] connReqBoolvar, byte[] playOfflinePropI32, byte[] playOfflineFnameBytes)
        {
            byte[] condition = Concat(
                new byte[] { OP_NOT_PRE_BOOL }, connReqBoolvar, EndFP());  // 8 bytes

            byte[] delegateCall = Concat(
                new byte[] { OP_DELEGATE_FUNC, 0x00 },
                playOfflinePropI32,
                playOfflineFnameBytes,
                new byte[] { OP_BYTE_CONST, 0x00 },  // ByteConst(0) = STA_None
                EndFP());                              // 17 bytes

            byte[] ret = new byte[] { OP_RETURN, OP_NOTHING };  // 2 bytes

            int jnotHeader = 3;
            int bodySize = delegateCall.Length + ret.Length;
            ushort skipTarget = (ushort)(insertBc + jnotHeader + condition.Length + bodySize);

            byte[] blob = Concat(
                JumpIfNot(skipTarget),
                condition,
                delegateCall,
                ret);

            if (blob.Length != 30)
                throw new InvalidOperationException($"OnlineSkip blob size mismatch: expected 30, got {blob.Length}");
            return blob;
        }

        // StartMove vertigo: replace InstanceVar(ZoomFOV) with Subtract(Context(DefaultFOV), 6.0).
        public static byte[] BuildVertigoReplacement(byte[] controllerCtx, byte[] defaultFovInst)
        {
            byte[] inner = defaultFovInst;
            byte[] ctxDefaultFov = Context(controllerCtx, (ushort)inner.Length, 4, inner);

            return Concat(
                new byte[] { OP_SUBTRACT_FF },
                ctxDefaultFov,
                FloatConst(K_VERTIGO_DELTA), EndFP());
        }

        // UnZoom else-branch: replace FloatConst(20) with FMax(20, DefaultFOV - FOVAngle).
        public static byte[] BuildUnzoomElseReplacement(byte[] instDefaultFov, byte[] instFovangle)
        {
            return Concat(
                new byte[] { OP_FMAX },
                FloatConst(20.0f),
                new byte[] { OP_SUBTRACT_FF },
                instDefaultFov, instFovangle, EndFP(),
                EndFP());
        }

        // SetFOV: Rate = Rate * (DefaultFOV - NewFOV) / 20.0.
        public static byte[] BuildSetFovRateInsert(int insertBc,
            byte[] localRate, byte[] localNewFov,
            byte[] dcast, byte[] controllerVar, byte[] defaultFovInst)
        {
            byte[] ctxDefaultFov = Context(
                Concat(dcast, controllerVar),
                5, 4, defaultFovInst);

            return Concat(
                new byte[] { OP_LET }, localRate,
                new byte[] { OP_DIVIDE_FF },
                new byte[] { OP_MULTIPLY_FF },
                localRate,
                new byte[] { OP_SUBTRACT_FF },
                ctxDefaultFov, localNewFov, EndFP(),
                EndFP(),
                FloatConst(K_STOCK_ZOOM_DELTA), EndFP());
        }

        // Stock pattern builders

        // Original 11-byte: Let ConstrainedAspectRatio = FloatConst(16/9).
        public static byte[] BuildStockArAssignment(byte[] arPropertyToken)
        {
            return Concat(
                new byte[] { OP_LET }, arPropertyToken,
                FloatConst(REF_AR));
        }

        // Original context call: Context(DynamicCast(X)(PawnOwner)).SetNearClippingPlane(10.0).
        public static byte[] BuildStockClipCall(byte[] dcast, byte[] powner, byte[] vfunc)
        {
            byte[] inner = Concat(vfunc, FloatConst(10.0f), EndFP());
            return Context(Concat(dcast, powner), (ushort)inner.Length, 0, inner);
        }

        // Original 5-byte: FloatConst(20.0) for UnZoom rate.
        public static byte[] BuildStockUnzoomRate()
        {
            return FloatConst(20.0f);
        }

        // Idempotency signatures

        // Engine.u phase 2 signature: GreaterThan + DefaultAspectRatio + FloatConst.
        public static byte[] GetP2Signature(byte[] instDefaultAr)
        {
            return Concat(new byte[] { OP_GREATER_FF }, instDefaultAr, new byte[] { OP_FLOAT_CONST });
        }

        // TdGame.u clip signature: FMin + Divide + Multiply + FloatConst(K_CLIP_HOR)
        public static readonly byte[] ClipSignature = Concat(
            new byte[] { OP_FMIN, OP_DIVIDE_FF, OP_MULTIPLY_FF, OP_FLOAT_CONST },
            F32(K_CLIP_HOR));

        // TdGame.u sensitivity signature: FloatConst(K_BASE_SENS).
        public static readonly byte[] SensSignature = Concat(
            new byte[] { OP_FLOAT_CONST }, F32(K_BASE_SENS));

        // TdGame.u vertigo signature: Subtract + Context opcode.
        public static readonly byte[] VertigoSignature = new byte[] { OP_SUBTRACT_FF, OP_CONTEXT };

        // TdGame.u zoom rate signature: FMax opcode.
        public static readonly byte[] ZoomRateSignature = new byte[] { OP_FMAX, OP_FLOAT_CONST };

        // TdGame.u online skip signature: Not_PreBool + BoolVar + InstVar (never appears in unpatched StartConnection).
        public static readonly byte[] OnlineSkipSignature = new byte[] { OP_NOT_PRE_BOOL, OP_BOOL_VAR, OP_INST_VAR };

        // Utility

        public static byte[] Concat(params byte[][] arrays)
        {
            int totalLen = 0;
            foreach (var a in arrays) totalLen += a.Length;
            var result = new byte[totalLen];
            int pos = 0;
            foreach (var a in arrays)
            {
                Buffer.BlockCopy(a, 0, result, pos, a.Length);
                pos += a.Length;
            }
            return result;
        }

        public static int FindPattern(byte[] data, byte[] pattern, int start = 0, int end = -1)
        {
            if (end < 0) end = data.Length;
            for (int i = start; i <= end - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
