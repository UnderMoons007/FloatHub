using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatHub.Core;
using Ui = FloatHub.Core.Controls;

namespace FloatHub;

public partial class SettingsWindow : Window
{
    private readonly List<SettingsPage> pages;
    private readonly Dictionary<string, Button> buttons = new();
    public string Current => Heading.Text;
    public SettingsWindow(List<SettingsPage> pages)
    {
        InitializeComponent();
        this.pages = pages;
        string group = "";
        foreach (var page in pages)
        {
            if (group != page.Section)
            {
                group = page.Section;
                var label = Ui.Text(group, 11, "#A8CCC1");
                label.Margin = new Thickness(12, 13, 0, 5);
                Navigation.Children.Add(label);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = page.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Width = 27, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = page.Title, Foreground = Brushes.White });
            var b = Ui.Button(page.Title, () => Select(page.Title));
            b.Content = row;
            System.Windows.Automation.AutomationProperties.SetName(b, page.Title);
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(10, 8, 10, 8);
            buttons.Add(page.Title, b);
            Navigation.Children.Add(b);
        }
    }

    public void Select(string name)
    {
        var page = pages.FirstOrDefault(p => p.Title == name) ?? pages[0];
        Heading.Text = page.Title;
        foreach (var (title, button) in buttons) button.Background = Ui.Brush(title == page.Title ? "#286057" : "#153336");
        Page.Content = page.Create();
        Scroll.ScrollToHome();
    }
}
