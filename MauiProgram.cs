using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Media;
using Microsoft.Maui.LifecycleEvents;

namespace ScionCarAssistant;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
				.UseMauiApp<App>()
				.UseMauiCommunityToolkit()
				.ConfigureLifecycleEvents(events =>
				{
#if ANDROID
					events.AddAndroid(android => android
									.OnResume(activity =>
									{
										MainPage.NotifyAppResumed();
									}));
#endif
				});

		builder.Services.AddSingleton<ISpeechToText>(SpeechToText.Default);
		builder.Services.AddTransient<MainPage>();

		return builder.Build();
	}
}