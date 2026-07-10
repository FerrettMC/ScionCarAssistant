namespace ScionCarAssistant;

public partial class MainPage : ContentPage
{
	bool voiceOn = false;
	bool isPlaying = false;

	public MainPage()
	{
		InitializeComponent();
	}

	private void OnVoiceClicked(object? sender, EventArgs e)
	{
		voiceOn = !voiceOn;
		VoiceBtn.Text = voiceOn ? "🎤 VOICE ON" : "🎤 VOICE OFF";
		VoiceBtn.BackgroundColor = voiceOn ? Colors.DarkGreen : Colors.DarkRed;
	}

	private void OnPlayPauseClicked(object? sender, EventArgs e)
	{
		isPlaying = !isPlaying;
		PlayPauseBtn.Text = isPlaying ? "⏸ PAUSE" : "▶ PLAY";
		// TODO: hook up to spotify
	}

	private void OnVolumeUpClicked(object? sender, EventArgs e)
	{
		// TODO: hook up to real volume control
	}

	private void OnVolumeDownClicked(object? sender, EventArgs e)
	{
		// TODO: hook up to real volume control
	}
	private void OnSkipClicked(object? sender, EventArgs e)
	{
		// TODO: hook up to real Spotify skip
	}
}