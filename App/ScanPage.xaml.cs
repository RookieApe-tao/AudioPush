using ZXing.Net.Maui.Controls;

namespace Audio2PhoneApp;

public partial class ScanPage : ContentPage
{
    readonly Action<string> _onScanned;
    bool _handled;

    public ScanPage(Action<string> onScanned)
    {
        InitializeComponent();
        _onScanned = onScanned;
        BarcodeReader.BarcodesDetected += OnBarcodesDetected;
    }

    void OnBarcodesDetected(object? sender, object e)
    {
        var first = (e as dynamic)?.Results?[0];
        string? value = first?.Value;
        if (value == null || _handled) return;
        _handled = true;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _onScanned(value);
            await Navigation.PopAsync();
        });
    }

    async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }
}
