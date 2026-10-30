using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace FesticalDoomsday.Client.Services;

/// <summary>
/// The side forms (Transport Resource, Bag, Information) have no window border
/// of their own: they carry their own close button instead. Without a title bar
/// there is nothing left to grab, so the whole form moves them - a left press
/// anywhere that is not an interactive control starts a window move drag.
/// </summary>
internal static class WindowDrag
{
    /// <summary>Lets the form itself drag the window.</summary>
    public static void Attach(Window window)
    {
        window.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
                return;
            // A press that landed on a control keeps its own meaning: the ✕
            // button closes, the slider drags, the receiver list selects.
            if (IsInteractive(e.Source))
                return;
            window.BeginMoveDrag(e);
        };
    }

    /// <summary>True when something under the pointer uses the press itself.</summary>
    private static bool IsInteractive(object? source)
    {
        for (var control = source as Control;
             control is not null and not Window;
             control = control.Parent as Control)
        {
            if (control is Button or ToggleButton or TextBox or Slider or Thumb
                or ScrollBar or ComboBox or CheckBox or RadioButton or ListBox
                or ListBoxItem or NumericUpDown or Menu or MenuItem)
            {
                return true;
            }
        }

        return false;
    }
}
