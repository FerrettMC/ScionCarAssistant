using CommunityToolkit.Maui.Media;
using System.Globalization;
using Android.Media;
using Android.Content;


namespace ScionCarAssistant;

public partial class MainPage : ContentPage
{

	bool isPlaying = false;
	bool isListening = false;

	readonly SpotifyAuthService _authService = new();
	readonly ISpeechToText _speechToText;
	CancellationTokenSource? _pollingCts;
	CancellationTokenSource? _speechCts;
	string? _accessToken;

	bool _suppressPoll = false;

	List<(string name, string uri)> _playlists = new();

	private List<string> _numbers = [
		"one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
		"eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
		"1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
		"11", "12", "13", "14", "15", "16", "17", "18", "19", "20"
];

	public MainPage(ISpeechToText speechToText)
	{
		InitializeComponent();
		_speechToText = speechToText;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await EnsureLoggedInAsync();
		await FetchPlaylistsAsync();
		_speechToText.RecognitionResultCompleted -= OnSpeechCompleted; // avoid double-subscribe
		_speechToText.RecognitionResultCompleted += OnSpeechCompleted;
		StartPolling();
	}

	void StartPolling()
	{
		_pollingCts = new CancellationTokenSource();
		var token = _pollingCts.Token;

		Task.Run(async () =>
{
	while (!token.IsCancellationRequested)
	{
		await Task.Delay(8000, token).ContinueWith(_ => { });
		if (token.IsCancellationRequested) break;
		if (!isListening && !_suppressPoll)
		{
			try { await RefreshPlaybackStateAsync(); }
			catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Poll] FAILED: {ex}"); }
		}
	}
}, token);
	}
	async Task EnsureLoggedInAsync()
	{
		try
		{
			var storedToken = await SecureStorage.Default.GetAsync("spotify_access_token");
			if (!string.IsNullOrEmpty(storedToken))
			{
				_accessToken = storedToken;
				NowPlayingLabel.Text = "Logged in";
				await RefreshPlaybackStateAsync();
				return;
			}

			var code = await _authService.LoginAndGetAuthCodeAsync();
			if (code == null)
			{
				NowPlayingLabel.Text = "Login failed";
				return;
			}

			var tokens = await _authService.ExchangeCodeForTokenAsync(code);
			if (tokens == null)
			{
				NowPlayingLabel.Text = "Token exchange failed";
				return;
			}

			_accessToken = tokens.Value.accessToken;
			await SecureStorage.Default.SetAsync("spotify_access_token", tokens.Value.accessToken);
			await SecureStorage.Default.SetAsync("spotify_refresh_token", tokens.Value.refreshToken);
			NowPlayingLabel.Text = "Logged in";
		}
		catch (Exception ex)
		{
			NowPlayingLabel.Text = $"ERROR: {ex.Message}";
		}
	}


	async Task FetchPlaylistsAsync()
	{
		try
		{
			var response = await SendSpotifyRequestAsync(http =>
							http.GetAsync("https://api.spotify.com/v1/me/playlists?limit=15"));

			if (!response.IsSuccessStatusCode)
			{
				NowPlayingLabel.Text = $"Playlist fetch error: {(int)response.StatusCode}";
				return;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var items = doc.RootElement.GetProperty("items");

			_playlists.Clear();
			foreach (var item in items.EnumerateArray())
			{
				if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

				var name = item.GetProperty("name").GetString() ?? "Unknown";
				var uri = item.GetProperty("uri").GetString() ?? "";
				_playlists.Add((name, uri));
			}

			System.Diagnostics.Debug.WriteLine($"[Playlists] Loaded {_playlists.Count} playlists");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Playlists] FETCH FAILED: {ex}");
			NowPlayingLabel.Text = $"Playlist fetch failed: {ex.Message}";
		}
	}

	async void ShowPlaylistsPopup()
	{
		System.Diagnostics.Debug.WriteLine($"[Popup] _playlists.Count = {_playlists.Count}");
		PlaylistStack.Children.Clear();

		for (int i = 0; i < _playlists.Count; i++)
		{
			var label = new Label
			{
				Text = $"{i + 1}. {_playlists[i].name}",
				FontSize = 32,
				TextColor = Colors.White,
				HorizontalTextAlignment = TextAlignment.Center
			};
			PlaylistStack.Children.Add(label);
		}

		PlaylistPopup.IsVisible = true;
		await Task.Delay(10000);
		PlaylistPopup.IsVisible = false;
	}

	private int StrToInt(string str)
	{
		return str switch
		{
			"1" or "one" => 1,
			"2" or "two" => 2,
			"3" or "three" => 3,
			"4" or "four" => 4,
			"5" or "five" => 5,
			"6" or "six" => 6,
			"7" or "seven" => 7,
			"8" or "eight" => 8,
			"9" or "nine" => 9,
			"10" or "ten" => 10,
			"11" or "eleven" => 11,
			"12" or "twelve" => 12,
			"13" or "thirteen" => 13,
			"14" or "fourteen" => 14,
			"15" or "fifteen" => 15,
			"16" or "sixteen" => 16,
			"17" or "seventeen" => 17,
			"18" or "eighteen" => 18,
			"19" or "nineteen" => 19,
			"20" or "twenty" => 20,
			_ => 1 // fallback for anything that doesn't match
		};
	}

	async Task<HttpResponseMessage> SendSpotifyRequestAsync(Func<HttpClient, Task<HttpResponseMessage>> request)
	{
		using var http = new HttpClient();
		http.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

		var response = await request(http);

		if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
		{
			var refreshToken = await SecureStorage.Default.GetAsync("spotify_refresh_token");
			if (!string.IsNullOrEmpty(refreshToken))
			{
				var newToken = await _authService.RefreshAccessTokenAsync(refreshToken);
				if (newToken != null)
				{
					_accessToken = newToken;
					await SecureStorage.Default.SetAsync("spotify_access_token", newToken);

					using var retryHttp = new HttpClient();
					retryHttp.DefaultRequestHeaders.Authorization =
							new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
					response = await request(retryHttp);
				}
			}
		}

		return response;
	}

	private async void OnPlayPauseClicked(object? sender, EventArgs e)
	{
		if (_accessToken == null)
		{
			NowPlayingLabel.Text = "Not logged in yet";
			return;
		}

		var endpoint = isPlaying ? "pause" : "play";
		var response = await SendSpotifyRequestAsync(http =>
				http.PutAsync($"https://api.spotify.com/v1/me/player/{endpoint}", null));

		if (response.IsSuccessStatusCode)
		{
			await Task.Delay(300);
			await RefreshPlaybackStateAsync();
		}
		else
		{
			NowPlayingLabel.Text = $"Spotify error: {(int)response.StatusCode}";
		}
	}

	private async void OnVoiceClicked(object? sender, EventArgs e)
	{
		if (isListening) return;

		try
		{
			isListening = true;
			VoiceBtn.Text = "LISTENING...";
			VoiceBtn.BackgroundColor = Colors.OrangeRed;

			var micStatus = await Permissions.RequestAsync<Permissions.Microphone>();
			var sttGranted = await _speechToText.RequestPermissions(CancellationToken.None);

			if (micStatus != PermissionStatus.Granted || !sttGranted)
			{
				NowPlayingLabel.Text = "Mic permission denied";
				ResetVoiceButton();
				return;
			}

			_speechCts = new CancellationTokenSource();

			await _speechToText.StartListenAsync(new SpeechToTextOptions
			{
				Culture = CultureInfo.CurrentCulture,
				ShouldReportPartialResults = false
			}, _speechCts.Token);
		}
		catch (Exception ex)
		{
			NowPlayingLabel.Text = "VOICE ERROR: " + ex.Message;
			ResetVoiceButton();
		}
	}

	void ResetVoiceButton()
	{
		isListening = false;
		VoiceBtn.Text = "PRESS TO TALK";
		VoiceBtn.BackgroundColor = Colors.DarkGreen;
	}

	async void OnSpeechCompleted(object? sender, SpeechToTextRecognitionResultCompletedEventArgs args)
	{
		var heard = args.RecognitionResult.Text?.ToLowerInvariant() ?? "";


		await _speechToText.StopListenAsync(CancellationToken.None);

		if (heard.Contains("open") && heard.Contains("spotify"))
		{
			OpenSpotifyApp();
		}
		else if (heard.Contains("skip") || heard.Contains("next"))
		{
			OnSkipClicked(null, EventArgs.Empty);
		}
		else if (heard.Contains("show") && heard.Contains("playlist"))
		{
			ShowPlaylistsPopup();
		}
		else if (heard.Contains("reload"))
		{
			Reload(null, EventArgs.Empty);
		}
		else if (heard.Contains("playlist"))
		{
			var words = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);

			int number = 1;
			foreach (var word in words)
			{
				if (_numbers.Contains(word))
				{
					number = StrToInt(word);
					break;
				}
			}

			await PlayPlaylistAsync(number);
		}
		else if ((heard.Contains("pause") && isPlaying) || (heard.Contains("stop") && isPlaying) || (heard.Contains("play") && !isPlaying) || (heard.Contains("start") && !isPlaying))
		{
			OnPlayPauseClicked(null, EventArgs.Empty);
		}
		else if (heard.Contains("volume") && (heard.Contains("up") || heard.Contains("down")))
		{
			if (heard.Contains("up"))
			{
				var words = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);

				int amt = 1;
				foreach (var word in words)
				{
					if (_numbers.Contains(word))
					{
						amt = StrToInt(word);
						break;
					}
				}

				for (int x = 0; x < amt; x++)
				{
					OnVolumeUpClicked(null, EventArgs.Empty);
				}
			}
			else
			{
				var words = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);

				int amt = 1;
				foreach (var word in words)
				{
					if (_numbers.Contains(word))
					{
						amt = StrToInt(word);
						break;
					}
				}
				for (int x = 0; x < amt; x++)
				{
					OnVolumeDownClicked(null, EventArgs.Empty);
				}
			}
		}



		else
		{
			NowPlayingLabel.Text = "Reload please";
		}

		ResetVoiceButton();
	}

	private void OpenSpotifyApp()
	{
		try
		{
			var packageManager = Android.App.Application.Context.PackageManager;
			var launchIntent = packageManager?.GetLaunchIntentForPackage("com.spotify.music");

			if (launchIntent != null)
			{
				launchIntent.AddFlags(Android.Content.ActivityFlags.NewTask);
				Android.App.Application.Context.StartActivity(launchIntent);
			}
			else
			{
				NowPlayingLabel.Text = "Spotify not installed";
			}
		}
		catch (Exception ex)
		{
			NowPlayingLabel.Text = "Open Spotify error: " + ex.Message;
		}
	}

	private void OnVolumeUpClicked(object? sender, EventArgs e)
	{
		var audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
		audioManager?.AdjustStreamVolume(Android.Media.Stream.Music, Adjust.Raise, VolumeNotificationFlags.ShowUi);
	}

	private void OnVolumeDownClicked(object? sender, EventArgs e)
	{
		var audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
		audioManager?.AdjustStreamVolume(Android.Media.Stream.Music, Adjust.Lower, VolumeNotificationFlags.ShowUi);
	}

	private async void Reload(object? sender, EventArgs e)
	{
		await RefreshPlaybackStateAsync();
	}

	async Task PlayPlaylistAsync(int number)
	{
		if (_accessToken == null)
		{
			NowPlayingLabel.Text = "Not logged in yet";
			return;
		}

		var index = number - 1;
		if (index < 0 || index >= _playlists.Count)
		{
			NowPlayingLabel.Text = $"No playlist #{number}";
			await Task.Delay(2000);
			await RefreshPlaybackStateAsync();
			return;
		}

		var (name, uri) = _playlists[index];

		var body = new System.Text.Json.Nodes.JsonObject
		{
			["context_uri"] = uri
		};

		var response = await SendSpotifyRequestAsync(http =>
						http.PutAsync("https://api.spotify.com/v1/me/player/play",
								new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")));

		if (response.IsSuccessStatusCode)
		{
			for (int attempt = 0; attempt < 3; attempt++)
			{
				await Task.Delay(400);
				await RefreshPlaybackStateAsync();
				if (isPlaying) break;
			}

			_suppressPoll = true;
			NowPlayingLabel.Text = $"Playing: {name}";
			await Task.Delay(3000);
			_suppressPoll = false;
			await RefreshPlaybackStateAsync();
		}
	}

	private async void OnSkipClicked(object? sender, EventArgs e)
	{
		try
		{
			if (_accessToken == null)
			{
				NowPlayingLabel.Text = "Not logged in yet";
				return;
			}

			var response = await SendSpotifyRequestAsync(http =>
					http.PostAsync("https://api.spotify.com/v1/me/player/next", null));

			if (!response.IsSuccessStatusCode)
			{
				NowPlayingLabel.Text = $"Skip error: {(int)response.StatusCode}";
				return;
			}

			await Task.Delay(500);
			await RefreshPlaybackStateAsync();
		}
		catch (Exception ex)
		{
			NowPlayingLabel.Text = "SKIP ERROR: " + ex.Message;
		}
	}

	async Task RefreshPlaybackStateAsync()
	{
		if (_accessToken == null) return;

		var response = await SendSpotifyRequestAsync(http =>
						http.GetAsync("https://api.spotify.com/v1/me/player/currently-playing"));

		if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
		{
			NowPlayingLabel.Text = "Nothing playing";
			isPlaying = false;
			PlayPauseBtn.Text = "▶ PLAY";
			return;
		}

		if (!response.IsSuccessStatusCode)
			return;

		var json = await response.Content.ReadAsStringAsync();
		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var root = doc.RootElement;

		isPlaying = root.TryGetProperty("is_playing", out var playingProp) && playingProp.GetBoolean();
		PlayPauseBtn.Text = isPlaying ? "PAUSE" : "▶ PLAY";

		if (!root.TryGetProperty("item", out var track) || track.ValueKind != System.Text.Json.JsonValueKind.Object)
		{
			NowPlayingLabel.Text = isPlaying ? "Playing..." : "Nothing playing";
			return;
		}

		var title = track.GetProperty("name").GetString();
		var artist = track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0
				? artists[0].GetProperty("name").GetString()
				: "Unknown artist";

		NowPlayingLabel.Text = $"{title} — {artist}";
	}
}