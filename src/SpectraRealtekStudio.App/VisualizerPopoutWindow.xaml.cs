using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace SpectraRealtekStudio.App;

public partial class VisualizerPopoutWindow : Window
{
    public event Action? DockRequested;
    public event Action? EscapePressed;
    public event Action? WindowStateUpdated;

    public VisualizerPopoutWindow()
    {
        InitializeComponent();
    }

    public void AttachVisualizerView(UIElement view)
    {
        PopoutContentHost.Children.Clear();
        PopoutContentHost.Children.Add(view);
    }

    public void DetachVisualizerView()
    {
        PopoutContentHost.Children.Clear();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke();
            e.Handled = true;
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        WindowStateUpdated?.Invoke();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        DockRequested?.Invoke();
    }
}
