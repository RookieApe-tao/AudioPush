using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace Audio2PhoneApp;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseBarcodeReader()        // 注册 ZXing 扫码服务
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// ---- 全局异常兜底：记录日志 + 弹窗提醒，不闪退 ----
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			Android.Util.Log.Error("A2P", $"UnhandledException: {e.ExceptionObject}");

#if ANDROID
		Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
		{
			Android.Util.Log.Error("A2P", $"UnhandledExceptionRaiser: {e.Exception}");
			ShowCrashAlert(e.Exception?.Message ?? "未知错误");
			e.Handled = true;   // 拦截，阻止闪退
		};
#endif

		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Android.Util.Log.Error("A2P", $"UnobservedTaskException: {e.Exception}");
			e.SetObserved();    // 标记已观察，防止 GC 时触发崩溃
		};

		return builder.Build();
	}

#if ANDROID
	internal static void ShowCrashAlert(string message)
	{
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			try
			{
				var page = Application.Current?.Windows?.FirstOrDefault()?.Page;
				if (page != null)
					await page.DisplayAlert("出现问题", message, "确定");
			}
			catch { }
		});
	}
#endif
}
