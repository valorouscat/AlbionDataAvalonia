using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AlbionDataAvalonia.Network.Models;
using AlbionDataAvalonia.Network.Services;
using AlbionDataAvalonia.Ui;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlbionDataAvalonia.ViewModels;

// The network adapter picker: automatic, or capture pinned to one adapter such as a VPN tunnel.
public partial class SettingsViewModel
{
    private const string AutomaticAdapterLabel = "Automatic";

    private NetworkListenerService? networkListener;
    private bool isApplyingAdapterOptions;

    public ObservableCollection<Option<string>> CaptureAdapterOptions { get; } = [new(string.Empty, AutomaticAdapterLabel)];

    [ObservableProperty]
    private Option<string>? selectedCaptureAdapter;

    [ObservableProperty]
    private bool isListingAdapters;

    public bool CanChooseCaptureAdapter => networkListener is not null;

    public string CaptureAdapterHint => string.IsNullOrEmpty(UserSettings.CaptureAdapterName)
        ? "Listens on every adapter and keeps the one with game traffic. If the game only works through a VPN such as sing-box, choose its tunnel adapter."
        : "Capture only listens on this adapter. While it's missing, for example before the VPN connects, every adapter is tried.";

    private void InitializeNetwork(NetworkListenerService? listener)
    {
        networkListener = listener;
        ApplyAdapterOptions([]);
        if (listener is not null) _ = RefreshCaptureAdaptersAsync();
    }

    partial void OnSelectedCaptureAdapterChanged(Option<string>? value)
    {
        if (isApplyingAdapterOptions || value is null) return;
        UserSettings.CaptureAdapterName = value.Value;
        OnPropertyChanged(nameof(CaptureAdapterHint));
    }

    [RelayCommand]
    private async Task RefreshCaptureAdaptersAsync()
    {
        if (networkListener is not { } listener || IsListingAdapters) return;
        IsListingAdapters = true;
        try
        {
            var adapters = await Task.Run(listener.ListCaptureAdapters);
            Dispatcher.UIThread.Post(() => ApplyAdapterOptions(adapters));
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsListingAdapters = false);
        }
    }

    private void ApplyAdapterOptions(IReadOnlyList<CaptureAdapterInfo> adapters)
    {
        isApplyingAdapterOptions = true;
        try
        {
            var chosen = UserSettings.CaptureAdapterName;
            CaptureAdapterOptions.Clear();
            CaptureAdapterOptions.Add(new Option<string>(string.Empty, AutomaticAdapterLabel));
            foreach (var adapter in adapters)
            {
                CaptureAdapterOptions.Add(new Option<string>(adapter.Key, adapter.IsTunnel ? $"{adapter.Label} (VPN tunnel)" : adapter.Label));
            }

            if (chosen.Length > 0 && CaptureAdapterOptions.All(option => !string.Equals(option.Value, chosen, StringComparison.OrdinalIgnoreCase)))
            {
                CaptureAdapterOptions.Add(new Option<string>(chosen, $"{chosen} (not connected)"));
            }

            SelectedCaptureAdapter = CaptureAdapterOptions.FirstOrDefault(option =>
                string.Equals(option.Value, chosen, StringComparison.OrdinalIgnoreCase)) ?? CaptureAdapterOptions[0];
        }
        finally
        {
            isApplyingAdapterOptions = false;
        }
    }
}
