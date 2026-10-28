using System.Runtime.InteropServices;

namespace Castor.AI.Providers.Local;

/// <summary>Names DXGI adapters in the same index order used by ONNX Runtime DirectML.</summary>
public static class DxgiAdapterCatalog
{
    private static readonly Guid Factory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static IReadOnlyList<DxgiAdapterInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var id = Factory1Id;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref id, out var factory));
        try
        {
            var enumerate = Method<EnumAdapters1>(factory, 12);
            var adapters = new List<DxgiAdapterInfo>();
            for (var index = 0u; index < 16; index++)
            {
                var status = enumerate(factory, index, out var adapter);
                if (status == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(status);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDesc1>(adapter, 10)(adapter, out var description));
                    adapters.Add(new DxgiAdapterInfo((int)index, description.Description));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }

            return adapters;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr adapter, out AdapterDescription description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}

public sealed record DxgiAdapterInfo(int Index, string Name);
