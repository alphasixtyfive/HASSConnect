using HassConnect.Core;

namespace HassConnect.App.Services;

internal interface IPcCommandExecutor
{
    void Execute(PcCommand command);
}
