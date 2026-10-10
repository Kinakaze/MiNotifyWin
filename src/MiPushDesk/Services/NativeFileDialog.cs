using System.Runtime.InteropServices;

namespace MiPushDesk.Services;

internal sealed class NativeFileDialog : IDisposable
{
    internal const int Cancelled = unchecked((int)0x800704C7);
    internal IFileDialog Dialog { get; }
    private bool _disposed;

    private NativeFileDialog(bool save, string[] extensions, string? name = null)
    {
        var classId = new Guid(save ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" : "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        Dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(classId, true)!)!;
        try
        {
            Dialog.GetOptions(out var options);
            options |= Options.ForceFileSystem | Options.PathMustExist | Options.NoChangeDirectory;
            options |= save ? Options.OverwritePrompt : Options.FileMustExist;
            Dialog.SetOptions(options);
            Dialog.SetFileTypes(1, [new()
            {
                Name = string.Join(" / ", extensions.Select(extension => extension.TrimStart('.').ToUpperInvariant())),
                Specification = string.Join(";", extensions.Select(extension => "*" + extension))
            }]);
            Dialog.SetFileTypeIndex(1);
            Dialog.SetTitle(save ? "另存为" : "选择文件");
            if (save)
            {
                Dialog.SetDefaultExtension(extensions[0].TrimStart('.'));
                Dialog.SetFileName(name!);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static NativeFileDialog Open(params string[] extensions) => new(false, extensions);
    public static NativeFileDialog Save(string name, string extension) => new(true, [extension], name);

    public string? Show(nint owner)
    {
        var result = Dialog.Show(owner);
        if (result == Cancelled) return null;
        Marshal.ThrowExceptionForHR(result);
        Dialog.GetResult(out var item);
        try
        {
            item.GetDisplayName(0x80058000, out var path);
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.ReleaseComObject(Dialog);
    }

    [Flags]
    internal enum Options : uint
    {
        OverwritePrompt = 0x2,
        NoChangeDirectory = 0x8,
        ForceFileSystem = 0x40,
        PathMustExist = 0x800,
        FileMustExist = 0x1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Filter
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Specification;
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Filter[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(Options options);
        void GetOptions(out Options options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid client);
        void ClearClientData();
        void SetFilter(nint filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
