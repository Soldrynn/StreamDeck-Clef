using System.Runtime.InteropServices;

namespace ClefBridge;

internal static class CoreAudioCallbackSelfTests
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimpleVolumeCallback(IntPtr self, float volume, [MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr eventContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StateCallback(IntPtr self, AudioSessionState state);

    public static void Run()
    {
        var callback = new TestSessionEvents();
        var pointer = Marshal.GetComInterfaceForObject(callback, typeof(IAudioSessionEvents));
        try
        {
            var vtable = Marshal.ReadIntPtr(pointer);
            var simpleVolumePointer = Marshal.ReadIntPtr(vtable, IntPtr.Size * 5);
            var statePointer = Marshal.ReadIntPtr(vtable, IntPtr.Size * 8);
            var simpleVolume = Marshal.GetDelegateForFunctionPointer<SimpleVolumeCallback>(simpleVolumePointer);
            var state = Marshal.GetDelegateForFunctionPointer<StateCallback>(statePointer);

            for (var iteration = 0; iteration < 1_000; iteration++)
            {
                Require(simpleVolume(pointer, 0.5f, false, IntPtr.Zero) == 0, "volume callback HRESULT");
                Require(state(pointer, AudioSessionState.Inactive) == 0, "state callback HRESULT");
            }
            Require(callback.VolumeChanges == 1_000, "volume callback dispatch");
            Require(callback.StateChanges == 1_000, "state callback dispatch");
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class TestSessionEvents : IAudioSessionEvents
    {
        public int VolumeChanges { get; private set; }
        public int StateChanges { get; private set; }

        public int OnDisplayNameChanged(string? value, IntPtr context) => 0;
        public int OnIconPathChanged(string? value, IntPtr context) => 0;
        public int OnSimpleVolumeChanged(float volume, bool muted, IntPtr context) { VolumeChanges++; return 0; }
        public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint channel, IntPtr context) => 0;
        public int OnGroupingParamChanged(IntPtr grouping, IntPtr context) => 0;
        public int OnStateChanged(AudioSessionState state) { StateChanges++; return 0; }
        public int OnSessionDisconnected(AudioSessionDisconnectReason reason) => 0;
    }
}
