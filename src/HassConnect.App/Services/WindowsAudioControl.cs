using System.ComponentModel;
using System.Runtime.InteropServices;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal static class WindowsAudioControl
{
    private const uint InputTypeKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const int ComContextAll = 23;
    private const int AudioRenderFlow = 0;
    private const int ConsoleRole = 0;
    private const int MultimediaRole = 1;
    private const byte VirtualKeyMediaNext = 0xB0;
    private const byte VirtualKeyMediaPrevious = 0xB1;
    private const byte VirtualKeyMediaStop = 0xB2;
    private const byte VirtualKeyMediaPlayPause = 0xB3;
    private const byte VirtualKeyVolumeMute = 0xAD;
    private static readonly Guid AudioEndpointVolumeId = typeof(IAudioEndpointVolume).GUID;
    private static readonly Guid DeviceEnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static void SendMediaCommand(MediaCommand command) => PressKey(command switch
    {
        MediaCommand.PlayPause => VirtualKeyMediaPlayPause,
        MediaCommand.Next => VirtualKeyMediaNext,
        MediaCommand.Previous => VirtualKeyMediaPrevious,
        MediaCommand.Stop => VirtualKeyMediaStop,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    });

    public static void ToggleMute() => PressKey(VirtualKeyVolumeMute);

    public static void SetVolume(int percentage)
    {
        if (percentage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorId, throwOnError: true)!;
        object? enumeratorObject = null;
        try
        {
            enumeratorObject = Activator.CreateInstance(enumeratorType)!;
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            SetVolume(enumerator, ConsoleRole, percentage);
            SetVolume(enumerator, MultimediaRole, percentage);
        }
        finally { Release(enumeratorObject); }
    }

    private static void PressKey(byte key)
    {
        Input[] inputs =
        [
            new() { Type = InputTypeKeyboard, Value = new() { Keyboard = new() { VirtualKey = key, Flags = KeyEventExtendedKey } } },
            new() { Type = InputTypeKeyboard, Value = new() { Keyboard = new() { VirtualKey = key, Flags = KeyEventExtendedKey | KeyEventKeyUp } } }
        ];
        var inputSize = Marshal.SizeOf<Input>();
        var expectedSize = nint.Size == 8 ? 40 : 28;
        if (inputSize != expectedSize)
            throw new InvalidOperationException($"Unexpected Windows input structure size: {inputSize}.");
        if (SendInput((uint)inputs.Length, inputs, inputSize) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void SetVolume(IMMDeviceEnumerator enumerator, int role, int percentage)
    {
        IMMDevice? device = null;
        object? endpointObject = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(AudioRenderFlow, role, out device);
            var interfaceId = AudioEndpointVolumeId;
            device.Activate(ref interfaceId, ComContextAll, nint.Zero, out endpointObject);
            ((IAudioEndpointVolume)endpointObject).SetMasterVolumeLevelScalar(percentage / 100f, Guid.Empty);
        }
        finally
        {
            Release(endpointObject);
            Release(device);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputValue Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputValue
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid interfaceId, int context, nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(nint notify);
        void UnregisterControlChangeNotify(nint notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, Guid context);
        void SetMasterVolumeLevelScalar(float level, Guid context);
    }
}
