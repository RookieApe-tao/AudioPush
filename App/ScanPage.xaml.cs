using ZXing.Net.Maui;
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
    }

    // XAML 已绑定 BarcodesDetected 事件
    void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        try
        {
            if (_handled) return;

            var first = e.Results?.FirstOrDefault();
            if (first == null || string.IsNullOrEmpty(first.Value)) return;
            _handled = true;

            var value = first.Value;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    _onScanned(value);
                    await Navigation.PopAsync();
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("A2P", $"scan callback: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("A2P", $"barcode handler: {ex}");
        }
    }

    async void OnCancelClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PopAsync(); } catch { }
    }
}
