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
        try
        {
            if (_handled) return;

            string? value = null;
            try
            {
                var results = (e as dynamic)?.Results;
                if (results != null && results.Count > 0)
                    value = (string?)results[0].Value;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("A2P", $"barcode dynamic parse: {ex.Message}");
            }

            if (string.IsNullOrEmpty(value) || _handled) return;
            _handled = true;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    _onScanned(value!);
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
