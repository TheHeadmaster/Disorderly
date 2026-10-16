using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Disorderly.View.Controls;

namespace Disorderly.Input;

public static class PaletteInputOwnership
{
    public static ArgumentParameterEditor? FindEditor(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = Parent(current))
        {
            if (current is ArgumentParameterEditor editor)
            {
                return editor;
            }
        }

        return null;
    }

    public static bool IsInteractive(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = Parent(current))
        {
            if (current is TextBoxBase or PasswordBox or Selector or ButtonBase or ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject item) => item switch
    {
        ContentElement element => ContentOperations.GetParent(element) ?? (element as FrameworkContentElement)?.Parent,
        Visual or Visual3D => VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item)
            ?? (item is FrameworkElement element ? element.TemplatedParent : null)
            ?? (item is UIElement container ? ItemsControl.ItemsControlFromItemContainer(container) : null),
        _ => LogicalTreeHelper.GetParent(item),
    };
}