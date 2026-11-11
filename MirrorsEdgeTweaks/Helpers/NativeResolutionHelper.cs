using System.Runtime.InteropServices;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class NativeResolutionHelper
    {
        private const uint QdcOnlyActivePaths = 0x2;
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;
        private const uint ModeInfoTypeSource = 1;
        private const uint DeviceInfoGetTargetPreferredMode = 3;
        private const uint Rotate90 = 2;
        private const uint Rotate270 = 4;
        private const int EnumCurrentSettings = -1;

        // Returns the primary monitor's preferred mode, which is its native resolution, in the desktop's orientation.
        // Returns the desktop's current resolution when the monitor reports no preferred mode, such as on a virtual display.
        // Returns null when neither can be read.
        public static ResolutionHelper.Resolution? GetPrimaryNativeResolution() => ReadPreferredMode() ?? ReadCurrentMode();

        private static ResolutionHelper.Resolution? ReadPreferredMode()
        {
            try
            {
                PathInfo[] paths;
                ModeInfo[] modes;
                uint pathCount, modeCount;
                int error;

                // The display topology can change between the two calls, which leaves the buffers too small.
                do
                {
                    if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount) != ErrorSuccess)
                        return null;
                    paths = new PathInfo[pathCount];
                    modes = new ModeInfo[modeCount];
                    error = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                }
                while (error == ErrorInsufficientBuffer);

                if (error != ErrorSuccess)
                    return null;

                for (int i = 0; i < pathCount; i++)
                {
                    PathInfo path = paths[i];
                    uint sourceIndex = path.SourceInfo.ModeInfoIdx;

                    // The primary monitor's desktop starts at the origin.
                    if (sourceIndex >= modeCount || modes[sourceIndex].InfoType != ModeInfoTypeSource
                        || modes[sourceIndex].SourceMode.Position.X != 0 || modes[sourceIndex].SourceMode.Position.Y != 0)
                        continue;

                    var request = new TargetPreferredMode
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = DeviceInfoGetTargetPreferredMode,
                            Size = (uint)Marshal.SizeOf<TargetPreferredMode>(),
                            AdapterId = path.TargetInfo.AdapterId,
                            Id = path.TargetInfo.Id,
                        },
                    };
                    if (DisplayConfigGetDeviceInfo(ref request) != ErrorSuccess || request.Width == 0 || request.Height == 0)
                        continue;

                    // The preferred mode is in the panel's own orientation, which a portrait desktop turns on its side.
                    return path.TargetInfo.Rotation is Rotate90 or Rotate270
                        ? new ResolutionHelper.Resolution { Width = (int)request.Height, Height = (int)request.Width }
                        : new ResolutionHelper.Resolution { Width = (int)request.Width, Height = (int)request.Height };
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static ResolutionHelper.Resolution? ReadCurrentMode()
        {
            try
            {
                var mode = new ResolutionHelper.DEVMODE { dmSize = (short)Marshal.SizeOf<ResolutionHelper.DEVMODE>() };
                return ResolutionHelper.EnumDisplaySettings(null, EnumCurrentSettings, ref mode) != 0
                    && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0
                    ? new ResolutionHelper.Resolution { Width = mode.dmPelsWidth, Height = mode.dmPelsHeight }
                    : null;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PathInfo[] pathArray,
            ref uint numModeInfoArrayElements, [Out] ModeInfo[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref TargetPreferredMode requestPacket);

        // The structures below must match the DISPLAYCONFIG structures in wingdi.h field for field.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rational
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PathSourceInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIdx;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PathTargetInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIdx;
            public uint OutputTechnology;
            public uint Rotation;
            public uint Scaling;
            public Rational RefreshRate;
            public uint ScanLineOrdering;
            public int TargetAvailable;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PathInfo
        {
            public PathSourceInfo SourceInfo;
            public PathTargetInfo TargetInfo;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Region2D
        {
            public uint Cx;
            public uint Cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct VideoSignalInfo
        {
            public ulong PixelRate;
            public Rational HSyncFreq;
            public Rational VSyncFreq;
            public Region2D ActiveSize;
            public Region2D TotalSize;
            public uint VideoStandard;
            public uint ScanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PointL
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SourceMode
        {
            public uint Width;
            public uint Height;
            public uint PixelFormat;
            public PointL Position;
        }

        // Leaves out the union's desktop image member, which is smaller than the target mode.
        [StructLayout(LayoutKind.Explicit)]
        internal struct ModeInfo
        {
            [FieldOffset(0)] public uint InfoType;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public Luid AdapterId;
            [FieldOffset(16)] public VideoSignalInfo TargetMode;
            [FieldOffset(16)] public SourceMode SourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInfoHeader
        {
            public uint Type;
            public uint Size;
            public Luid AdapterId;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TargetPreferredMode
        {
            public DeviceInfoHeader Header;
            public uint Width;
            public uint Height;
            public VideoSignalInfo TargetMode;
        }
    }
}
