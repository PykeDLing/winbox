using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WinBox.Platform;

/// <summary>Creates COM objects behind source-generated interfaces ([GeneratedComInterface]); no built-in COM.</summary>
internal static partial class ComObjects
{
    public static T Create<T>(Guid clsid) where T : class
    {
        var iid = StrategyBasedComWrappers.DefaultIUnknownInterfaceDetailsStrategy
            .GetIUnknownDerivedDetails(typeof(T).TypeHandle)!.Iid;
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, ClsctxAll, iid, out var pointer));
        try
        {
            return (T)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private const uint ClsctxAll = 0x17;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}
