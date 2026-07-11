using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Media;

namespace ScionCarAssistant;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
				.UseMauiApp<App>()
				.UseMauiCommunityToolkit();

		builder.Services.AddSingleton<ISpeechToText>(SpeechToText.Default);
		builder.Services.AddTransient<MainPage>();

		return builder.Build();
	}
}