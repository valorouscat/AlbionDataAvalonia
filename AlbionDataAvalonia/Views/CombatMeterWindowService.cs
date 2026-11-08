using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Ui.Services;
using AlbionDataAvalonia.ViewModels;

namespace AlbionDataAvalonia.Views;

public sealed class CombatMeterWindowService
{
    private readonly SingleWindowHost<CombatMeterWindow, CombatMeterViewModel> host;

    public CombatMeterWindowService(ActivityTrackerService tracker, SettingsManager settings, IClipboardService clipboard)
    {
        var user = settings.UserSettings;
        host = new(() => new CombatMeterViewModel(tracker, user, clipboard), model => model.Start(),
            new WindowMemory(() => user.CombatMeterWindowOpen, open => user.CombatMeterWindowOpen = open,
                () => user.CombatMeterWindowPlacement, placement => user.CombatMeterWindowPlacement = placement), restoreSize: true);
    }

    public void RestoreIfOpen() => host.RestoreIfOpen();
    public void Show() { host.ViewModel?.RevealCommand.Execute(null); host.Show(); }
}
