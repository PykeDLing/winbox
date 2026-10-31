using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WinBox.Platform.Guard;

/// <summary>
/// The Task Scheduler API (taskschd.h) as source-generated COM. Only the slots the host guard calls are
/// typed; the others before them keep the vtable order and are never called. The interfaces are dual, so
/// each starts with IDispatch's four slots.
/// </summary>
internal static class ScheduledTasks
{
    /// <summary>TASK_ENUM_HIDDEN: hidden tasks are listed too.</summary>
    public const int EnumHidden = 1;

    /// <summary>The local Task Scheduler, connected as the current user.</summary>
    public static ITaskService Connect()
    {
        var service = ComObjects.Create<ITaskService>(new Guid("0F87369F-A4E5-4CFC-BD3E-73E6154572DD"));
        service.Connect(default, default, default, default);
        return service;
    }
}

/// <summary>A VARIANT (oaidl.h; 24 bytes on 64-bit Windows) that is empty or holds a 32-bit integer.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal readonly struct Variant
{
    [FieldOffset(0)] private readonly ushort _type;
    [FieldOffset(8)] private readonly int _int32;

    private Variant(ushort type, int value) => (_type, _int32) = (type, value);

    /// <summary>VT_I4, which the collections are indexed with (from 1).</summary>
    public static Variant Int32(int value) => new(3, value);
}

[GeneratedComInterface]
[Guid("00020400-0000-0000-C000-000000000046")]
internal partial interface IDispatchSlots
{
    [PreserveSig] int GetTypeInfoCount();
    [PreserveSig] int GetTypeInfo();
    [PreserveSig] int GetIDsOfNames();
    [PreserveSig] int Invoke();
}

[GeneratedComInterface]
[Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
internal partial interface ITaskService : IDispatchSlots
{
    ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);
    [PreserveSig] int GetRunningTasks();
    [PreserveSig] int NewTask();
    void Connect(Variant serverName, Variant user, Variant domain, Variant password);
}

[GeneratedComInterface]
[Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
internal partial interface ITaskFolder : IDispatchSlots
{
    [PreserveSig] int GetName();
    [PreserveSig] int GetPath();
    [PreserveSig] int GetFolder();
    ITaskFolderCollection GetFolders(int flags);
    [PreserveSig] int CreateFolder();
    [PreserveSig] int DeleteFolder();
    [PreserveSig] int GetTask();
    IRegisteredTaskCollection GetTasks(int flags);
    void DeleteTask([MarshalAs(UnmanagedType.BStr)] string name, int flags);
}

[GeneratedComInterface]
[Guid("79184A66-8664-423F-97F1-637356A5D812")]
internal partial interface ITaskFolderCollection : IDispatchSlots
{
    int GetCount();
    ITaskFolder GetItem(Variant index);
}

[GeneratedComInterface]
[Guid("86627EB4-42A7-41E4-A4D9-AC33A72F2D52")]
internal partial interface IRegisteredTaskCollection : IDispatchSlots
{
    int GetCount();
    IRegisteredTask GetItem(Variant index);
}

[GeneratedComInterface]
[Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E")]
internal partial interface IRegisteredTask : IDispatchSlots
{
    [return: MarshalAs(UnmanagedType.BStr)] string GetName();
    [return: MarshalAs(UnmanagedType.BStr)] string GetPath();
    [PreserveSig] int GetState();
    [PreserveSig] int GetEnabled();
    [PreserveSig] int PutEnabled();
    [PreserveSig] int Run();
    [PreserveSig] int RunEx();
    [PreserveSig] int GetInstances();
    [PreserveSig] int GetLastRunTime();
    [PreserveSig] int GetLastTaskResult();
    [PreserveSig] int GetNumberOfMissedRuns();
    [PreserveSig] int GetNextRunTime();
    [PreserveSig] int GetDefinition();
    [return: MarshalAs(UnmanagedType.BStr)] string GetXml();
}
