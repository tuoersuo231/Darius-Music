using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CurrentLyrics.Controls;

/// <summary>
/// Slider whose value only moves when the knob is dragged (or with the keyboard). A plain click on
/// the groove stepped the value by one <c>LargeChange</c> — small enough that the number looked like
/// it was walking by itself ("点一下参数就自己挪一格"), so the config panels now ignore any press
/// that does not land on the knob. Seek and volume bars keep <see cref="Slider"/> on purpose: there a
/// click is meant to jump.
/// </summary>
public sealed class DragOnlySlider : Slider
{
    /// <summary>QA counters: presses ignored because they missed the knob, and presses let through.</summary>
    public static int BlockedPresses { get; private set; }
    public static int AllowedPresses { get; private set; }
    public static void ResetCounters() => BlockedPresses = AllowedPresses = 0;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsOnThumb(e.OriginalSource as DependencyObject))
        {
            BlockedPresses++;
            e.Handled = true;   // no click-to-jump and no click-to-step
            return;
        }
        AllowedPresses++;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private static bool IsOnThumb(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Thumb) return true;
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
