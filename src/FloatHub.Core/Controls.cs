using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatHub.Core;

public static class Controls
{
    public static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
    public static TextBlock Text(string value, double size = 14, string color = "#182F32") =>
        new() { Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    public static Button Button(string label, Action click, bool primary = false)
    {
        var b = new Button { Content = label };
        if (primary) { b.Background = Brush("#086F64"); b.Foreground = Brushes.White; }
        b.Click += (_, _) => click();
        return b;
    }
    public static CheckBox Toggle(string label, bool value, Action<bool> changed)
    {
        var toggle = new CheckBox { Content = label, IsChecked = value };
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }
    public static ComboBox Choice<T>(T[] items, T selected, Action<T> change)
    {
        var box = new ComboBox { ItemsSource = items, SelectedItem = selected, MinWidth = 116, HorizontalAlignment = HorizontalAlignment.Left };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is T item) change(item); };
        return box;
    }
    public static WrapPanel Row(params UIElement[] items)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }
    public static Border Card(string title, params UIElement[] children)
    {
        var stack = new StackPanel();
        var header = Text(title, 16);
        header.FontWeight = FontWeights.SemiBold;
        header.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(header);
        foreach (var child in children) stack.Children.Add(child);
        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush("#DEE7E3"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 15, 18, 16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }
    public static StackPanel Stack(params UIElement[] items)
    {
        var panel = new StackPanel();
        foreach (var item in items) panel.Children.Add(item);
        return panel;
    }
}
