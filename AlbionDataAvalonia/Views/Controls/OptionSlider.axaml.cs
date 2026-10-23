using AlbionDataAvalonia.Ui;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using System;
using System.Collections;
using System.Linq;

namespace AlbionDataAvalonia.Views.Controls;

/// <summary>A discrete horizontal choice with its label always visible; wheel events scroll its containing panel.</summary>
public partial class OptionSlider : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<OptionSlider, IEnumerable?>(nameof(ItemsSource));
    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<OptionSlider, object?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);
    private object[] choices = [];
    private bool updating;

    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    public OptionSlider()
    {
        InitializeComponent();
        ChoiceSlider.PropertyChanged += (_, change) =>
        {
            if (updating || change.Property != RangeBase.ValueProperty || choices.Length == 0) return;
            SetCurrentValue(SelectedItemProperty, choices[Math.Clamp((int)Math.Round(ChoiceSlider.Value), 0, choices.Length - 1)]);
        };
        UpdateChoice();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (ChoiceSlider is null) return;
        if (change.Property == ItemsSourceProperty) choices = ItemsSource?.Cast<object>().ToArray() ?? [];
        if (change.Property == ItemsSourceProperty || change.Property == SelectedItemProperty
            || change.Property == AutomationProperties.NameProperty) UpdateChoice();
    }

    private void UpdateChoice()
    {
        updating = true;
        try
        {
            var index = Math.Max(0, Array.IndexOf(choices, SelectedItem));
            ChoiceSlider.Maximum = Math.Max(1, choices.Length - 1);
            ChoiceSlider.IsEnabled = choices.Length > 1;
            ChoiceSlider.Value = index;
            ChoiceLabel.Text = choices.Length > 0 ? (choices[index] as IOption)?.Label ?? choices[index].ToString() : UiText.None;
            AutomationProperties.SetName(ChoiceSlider, AutomationProperties.GetName(this) ?? "Choice");
            AutomationProperties.SetHelpText(ChoiceSlider, ChoiceLabel.Text);
        }
        finally { updating = false; }
    }
}
