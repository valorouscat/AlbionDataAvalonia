using Avalonia.Controls;
using Avalonia.Input;
using System;

namespace AlbionDataAvalonia.Views.Controls;

/// <summary>Click, drag and keyboard adjustment; the mouse wheel stays available to the containing scroller.</summary>
public class ScrollSafeSlider : Slider
{
    protected override Type StyleKeyOverride => typeof(Slider);
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { }
}
