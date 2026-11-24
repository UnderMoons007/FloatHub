using System;
using System.Windows;
using System.Windows.Controls;

namespace FloatHub.StockTracker;

public partial class StockView : UserControl
{
    private readonly Action manage;
    public StockView(StockViewModel vm, Action manage)
    {
        InitializeComponent(); DataContext = vm; this.manage = manage;
    }
    private void Manage(object sender, RoutedEventArgs e) => manage();
}
