using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class FeedbackWindow : Window {
    public FeedbackWindow() => InitializeComponent();

    void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
