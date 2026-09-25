using HassConnect.Core;

namespace HassConnect.App.Services;

internal sealed class WindowsPcControl : IPcCommandExecutor
{
    public void Execute(PcCommand command)
    {
        switch (command.Kind)
        {
            case PcCommandKind.Lock:
                WindowsPowerControl.Lock();
                break;
            case PcCommandKind.MonitorSleep:
                WindowsPowerControl.TurnOffDisplays();
                break;
            case PcCommandKind.MonitorWake:
                WindowsPowerControl.WakeDisplays();
                break;
            case PcCommandKind.Sleep:
                WindowsPowerControl.Sleep();
                break;
            case PcCommandKind.Shutdown:
                WindowsPowerControl.Shutdown();
                break;
            case PcCommandKind.Restart:
                WindowsPowerControl.Restart();
                break;
            case PcCommandKind.Media:
                WindowsAudioControl.SendMediaCommand(command.Media ??
                    throw new InvalidDataException("The media command is missing."));
                break;
            case PcCommandKind.VolumeMute:
                WindowsAudioControl.ToggleMute();
                break;
            case PcCommandKind.VolumeLevel:
                WindowsAudioControl.SetVolume(command.VolumeLevel ??
                    throw new InvalidDataException("The volume level is missing."));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
