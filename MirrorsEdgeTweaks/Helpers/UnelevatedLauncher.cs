using System.Runtime.InteropServices;

namespace MirrorsEdgeTweaks.Helpers
{
    // Asks the desktop shell (Explorer) to start a program, so that it runs without Tweaks' administrator rights.
    // Microsoft documents the approach at https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643.
    internal static class UnelevatedLauncher
    {
        private const int CsidlDesktop = 0;
        private const int SwcDesktop = 8;
        private const int SwfoNeedDispatch = 1;
        private const uint SvgioBackground = 0;
        private const int SwShowNormal = 1;
        private const string OpenVerb = "open";

        private static readonly Guid ShellWindowsClassId = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid TopLevelBrowserServiceId = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid DispatchInterfaceId = new("00020400-0000-0000-C000-000000000046");

        // Throws when the shell was reached but the request failed, because the program may have started.
        // Explorer shows its own message when it cannot start the program.
        public static bool TryStart(string fileName, string arguments, string workingDirectory)
        {
            var comObjects = new List<object>();
            try
            {
                IShellDispatch2? shell = FindDesktopShell(comObjects);
                if (shell == null)
                    return false;

                PassForegroundTo(shell);
                shell.ShellExecute(fileName, arguments, workingDirectory, OpenVerb, SwShowNormal);
                return true;
            }
            finally
            {
                foreach (object comObject in comObjects)
                    Marshal.ReleaseComObject(comObject);
            }
        }

        private static IShellDispatch2? FindDesktopShell(List<object> comObjects)
        {
            try
            {
                Type? shellWindowsType = Type.GetTypeFromCLSID(ShellWindowsClassId);
                object? shellWindows = shellWindowsType == null ? null : Activator.CreateInstance(shellWindowsType);
                if (shellWindows == null)
                    return null;
                comObjects.Add(shellWindows);

                object location = CsidlDesktop;
                object? root = null;
                object? desktop = ((IShellWindows)shellWindows).FindWindowSW(
                    ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
                if (desktop == null)
                    return null;
                comObjects.Add(desktop);

                Guid serviceId = TopLevelBrowserServiceId;
                Guid browserId = typeof(IShellBrowser).GUID;
                object browser = ((IOleServiceProvider)desktop).QueryService(ref serviceId, ref browserId);
                comObjects.Add(browser);

                IShellView view = ((IShellBrowser)browser).QueryActiveShellView();
                comObjects.Add(view);

                Guid dispatchId = DispatchInterfaceId;
                object background = view.GetItemObject(SvgioBackground, ref dispatchId);
                comObjects.Add(background);

                object application = ((IShellFolderViewDual)background).Application;
                comObjects.Add(application);

                return (IShellDispatch2)application;
            }
            catch (Exception)
            {
                // The lookup starts nothing, so any failure in it only means that there is no shell to ask.
                return null;
            }
        }

        // Lets the program that Explorer starts open in front of Tweaks.
        // Windows refuses when Tweaks is not the foreground window, which only leaves the program behind other windows.
        private static void PassForegroundTo(IShellDispatch2 shell)
        {
            if (shell is IForegroundTransfer transfer)
                transfer.AllowForegroundTransfer(IntPtr.Zero);
        }

        // COM finds the members of the IDispatch interfaces below by name, so they must keep the names that COM gives them.
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellWindows
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object? FindWindowSW(
                [MarshalAs(UnmanagedType.Struct)] ref object location,
                [MarshalAs(UnmanagedType.Struct)] ref object? root,
                int windowClass,
                out int windowHandle,
                int options);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleServiceProvider
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid serviceId, ref Guid interfaceId);
        }

        // The methods before QueryActiveShellView are never called, and are declared only to fill their slots in the method table.
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow();
            void ContextSensitiveHelp();
            void InsertMenusSB();
            void SetMenuSB();
            void RemoveMenusSB();
            void SetStatusTextSB();
            void EnableModelessSB();
            void TranslateAcceleratorSB();
            void BrowseObject();
            void GetViewStateStream();
            void GetControlWindow();
            void SendControlMsg();

            IShellView QueryActiveShellView();
        }

        // The methods before GetItemObject are never called, and are declared only to fill their slots in the method table.
        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void GetWindow();
            void ContextSensitiveHelp();
            void TranslateAccelerator();
            void EnableModeless();
            void UIActivate();
            void Refresh();
            void CreateViewWindow();
            void DestroyViewWindow();
            void GetCurrentInfo();
            void AddPropertySheetPages();
            void SaveViewState();
            void SelectItem();

            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetItemObject(uint item, ref Guid interfaceId);
        }

        [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellFolderViewDual
        {
            object Application
            {
                [return: MarshalAs(UnmanagedType.IDispatch)]
                get;
            }
        }

        [ComImport, Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellDispatch2
        {
            void ShellExecute(
                [MarshalAs(UnmanagedType.BStr)] string file,
                [MarshalAs(UnmanagedType.Struct)] object arguments,
                [MarshalAs(UnmanagedType.Struct)] object directory,
                [MarshalAs(UnmanagedType.Struct)] object operation,
                [MarshalAs(UnmanagedType.Struct)] object show);
        }

        // COM's own proxy for an object in another process implements this interface.
        [ComImport, Guid("00000145-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IForegroundTransfer
        {
            [PreserveSig]
            int AllowForegroundTransfer(IntPtr reserved);
        }
    }
}
