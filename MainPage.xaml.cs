using CommunityToolkit.Maui.Media;
using System.Globalization;
using Android.Media;
using Android.Content;


namespace ScionCarAssistant;

public partial class MainPage : ContentPage
{

	bool isPlaying = false;
	bool isListening = false;

	string popupType = "";

	readonly SpotifyAuthService _authService = new();
	readonly ISpeechToText _speechToText;
	CancellationTokenSource? _pollingCts;
	CancellationTokenSource? _speechCts;
	public static event Action? AppResumed;
	public static void NotifyAppResumed() => AppResumed?.Invoke();
	string? _accessToken;

	bool _suppressPoll = false;
	IDispatcherTimer? _visualizerTimer;
	List<BoxView> _bars = new();
	Random _rand = new();

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
		AppResumed -= OnAppResumed;
		AppResumed += OnAppResumed;
		InitVisualizer();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_visualizerTimer?.Stop();
	}

	async void OnAppResumed()
	{
		if (_accessToken == null) return;
		await Task.Delay(1000);
		await RefreshPlaybackStateAsync();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		_speechToText.RecognitionResultCompleted -= OnSpeechCompleted;
		_speechToText.RecognitionResultCompleted += OnSpeechCompleted;

		await EnsureLoggedInAsync();
		await FetchPlaylistsAsync();
		StartPolling();

		_ = AutoHideQuickOpenButton();
	}

	async Task AutoHideQuickOpenButton()
	{
		await Task.Delay(2000);
		QuickOpenSpotifyBtn.IsVisible = false;
	}

	void SetNowPlaying(string text) =>
			MainThread.BeginInvokeOnMainThread(() => NowPlayingLabel.Text = text);

	void SetPlayPauseText(string text) =>
			MainThread.BeginInvokeOnMainThread(() => PlayPauseBtn.Text = text);

	void StartPolling()
	{
		_pollingCts = new CancellationTokenSource();
		var token = _pollingCts.Token;

		Task.Run(async () =>
{
	while (!token.IsCancellationRequested)
	{
		await Task.Delay(4000, token).ContinueWith(_ => { });
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
				await Task.Delay(5000);
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
		popupType = "showplaylists";
		PlaylistPopup.IsVisible = true;
		await Task.Delay(10000);
		if (popupType == "showplaylists")
		{
			PlaylistPopup.IsVisible = false;
		}
	}

	async void ShowCommandsPopup()
	{
		PlaylistStack.Children.Clear();

		var commands = new[]
		{
				"\"Open Spotify\"",
				"\"Play __ by __\"",
				"\"Add __ by __ to the queue\"",
				"\"Skip\" / \"Next\"",
				"\"Play\" / \"Pause\" / \"Stop\" / \"Start\"",
				"\"Show playlists\"",
				"\"Playlist [number]\"",
				"\"Volume up/down [number]\"",
				"\"Reload\"",
				"\"Info\""
		};

		foreach (var cmd in commands)
		{
			var label = new Label
			{
				Text = cmd,
				FontSize = 28,
				TextColor = Colors.White,
				HorizontalTextAlignment = TextAlignment.Center
			};
			PlaylistStack.Children.Add(label);
		}
		popupType = "showcommands";
		PlaylistPopup.IsVisible = true;
		await Task.Delay(10000);
		if (popupType == "showcommands")
		{
			PlaylistPopup.IsVisible = false;
		}
	}

	async void showInfo()
	{
		if (_accessToken == null) return;
		try
		{
			PlaylistStack.Children.Clear();
			var response = await SendSpotifyRequestAsync(http =>
											http.GetAsync("https://api.spotify.com/v1/me/player/currently-playing"));

			if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
			{
				return;
			}
			if (!response.IsSuccessStatusCode)
			{
				System.Diagnostics.Debug.WriteLine($"[Refresh] Non-success: {(int)response.StatusCode}");
				return;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var root = doc.RootElement;

			isPlaying = root.TryGetProperty("is_playing", out var playingProp) && playingProp.GetBoolean();

			if (!isPlaying) return;

			if (!root.TryGetProperty("item", out var track) || track.ValueKind != System.Text.Json.JsonValueKind.Object)
			{
				return;
			}


			var title = track.GetProperty("name").GetString();
			var artist = track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0
											? artists[0].GetProperty("name").GetString()
											: "Unknown artist";

			string? playlistName = null;
			if (root.TryGetProperty("context", out var context) &&
					context.ValueKind == System.Text.Json.JsonValueKind.Object &&
					context.TryGetProperty("type", out var ctxType) &&
					ctxType.GetString() == "playlist" &&
					context.TryGetProperty("uri", out var ctxUri))
			{
				var uri = ctxUri.GetString();
				playlistName = _playlists.FirstOrDefault(p => p.uri == uri).name;
			}
			string info = $"{title}\n\nby {artist}\n\n-{playlistName}-";
			var label = new Label
			{
				Text = info,
				FontSize = 35,
				TextColor = Colors.White,
				HorizontalTextAlignment = TextAlignment.Center
			};

			PlaylistStack.Children.Add(label);
			popupType = "info";
			PlaylistPopup.IsVisible = true;
			await Task.Delay(10000);
			if (popupType == "info")
			{
				PlaylistPopup.IsVisible = false;
			}

		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[info] FAILED: {ex}");
			SetNowPlaying($"info error: {ex.Message}");
		}
	}

	async void playSong(string song, string artist)
	{
		if (_accessToken == null) return;
		try
		{
			string query = string.IsNullOrWhiteSpace(artist)
		? Uri.EscapeDataString($"track:{song}")
		: Uri.EscapeDataString($"track:{song} artist:{artist}");
			string url = $"https://api.spotify.com/v1/search?q={query}&type=track&limit=1";
			var response = await SendSpotifyRequestAsync(http => http.GetAsync(url));
			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var tracks = doc.RootElement.GetProperty("tracks").GetProperty("items");

			if (tracks.GetArrayLength() == 0)
			{
				SetNowPlaying("Couldn't find song.");
				return;
			}

			var trackUri = tracks[0].GetProperty("uri").GetString();

			var body = new System.Text.Json.Nodes.JsonObject
			{
				["uris"] = new System.Text.Json.Nodes.JsonArray { trackUri }
			};

			var playResponse = await SendSpotifyRequestAsync(http =>
					http.PutAsync("https://api.spotify.com/v1/me/player/play",
							new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")));

			if (playResponse.IsSuccessStatusCode)
			{
				for (int attempt = 0; attempt < 3; attempt++)
				{
					await Task.Delay(400);
					await RefreshPlaybackStateAsync();
					if (isPlaying) break;
				}
			}
			else
			{
				SetNowPlaying($"Play error: {(int)playResponse.StatusCode}");
			}
		}

		catch (Exception ex)
		{
			{
				System.Diagnostics.Debug.WriteLine($"[play song] FAILED: {ex}");
				SetNowPlaying($"play song error: {ex.Message}");
			}
		}
	}


	async void queueSong(string song, string artist)
	{
		if (_accessToken == null) return;
		try
		{
			string query = string.IsNullOrWhiteSpace(artist)
		? Uri.EscapeDataString($"track:{song}")
		: Uri.EscapeDataString($"track:{song} artist:{artist}");
			string url = $"https://api.spotify.com/v1/search?q={query}&type=track&limit=1";
			var response = await SendSpotifyRequestAsync(http => http.GetAsync(url));
			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var tracks = doc.RootElement.GetProperty("tracks").GetProperty("items");

			if (tracks.GetArrayLength() == 0)
			{
				SetNowPlaying("Couldn't find song.");
				return;
			}

			var trackUri = tracks[0].GetProperty("uri").GetString();
			var trackName = tracks[0].GetProperty("name").GetString();

			if (trackUri == null) return;

			string escapedUri = Uri.EscapeDataString(trackUri);
			var queueResponse = await SendSpotifyRequestAsync(http =>
					http.PostAsync($"https://api.spotify.com/v1/me/player/queue?uri={escapedUri}", null));

			if (queueResponse.IsSuccessStatusCode)
			{
				SetNowPlaying($"Queued: {trackName}");
			}
			else
			{
				SetNowPlaying($"Queue error: {(int)queueResponse.StatusCode}");
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[queue song] FAILED: {ex}");
			SetNowPlaying($"queue song error: {ex.Message}");
		}
	}

	void OnPlaylistPopupTapped(object? sender, TappedEventArgs e)
	{
		popupType = "";
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
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
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

					using var retryHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
					retryHttp.DefaultRequestHeaders.Authorization =
									new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
					response = await request(retryHttp);
				}
			}
		}

		return response;
	}

	private void OnQuickOpenSpotifyClicked(object? sender, EventArgs e)
	{
		OpenSpotifyApp();
		QuickOpenSpotifyBtn.IsVisible = false;
	}

	void HideQuickOpenButton() => QuickOpenSpotifyBtn.IsVisible = false;

	void HideScreenText() => PlaylistPopup.IsVisible = false;

	private async void OnPlayPauseClicked(object? sender, EventArgs e)
	{
		HideQuickOpenButton();
		HideScreenText();
		if (_accessToken == null)
		{
			NowPlayingLabel.Text = "Not logged in yet";
			return;
		}

		try
		{
			await RefreshPlaybackStateAsync();

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
				var errorBody = await response.Content.ReadAsStringAsync();
				System.Diagnostics.Debug.WriteLine($"[PlayPause] 403 body: {errorBody}");
				NowPlayingLabel.Text = $"Spotify error: {(int)response.StatusCode}";
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[PlayPause] FAILED: {ex}");
			NowPlayingLabel.Text = $"Error: {ex.Message}";
		}
	}

	private async void OnVoiceClicked(object? sender, EventArgs e)
	{
		HideQuickOpenButton();
		HideScreenText();
		if (isListening)
		{
			// Second tap while listening = force stop
			_speechCts?.Cancel();
			await _speechToText.StopListenAsync(CancellationToken.None);
			ResetVoiceButton();
			return;
		}

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
			_speechCts.CancelAfter(TimeSpan.FromSeconds(8));

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
		HideQuickOpenButton();
		HideScreenText();
		var heard = args.RecognitionResult.Text?.ToLowerInvariant() ?? "";
		System.Diagnostics.Debug.WriteLine($"[Heard] \"{heard}\"");
		await _speechToText.StopListenAsync(CancellationToken.None);
		await Task.Delay(600);

		string[] words = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		bool hasQueueWord = words.Contains("queue") || words.Contains("q");

		if (heard.Contains("open") && heard.Contains("spotify"))
		{
			OpenSpotifyApp();
		}
		if (heard.Contains("info"))
		{
			showInfo();
		}
		else if (heard.Contains("command"))
		{
			ShowCommandsPopup();
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
		else if (heard.Contains("play") && !heard.Contains("playlist") && heard.Length != 4)
		{
			if (words[0] != "play") return;

			int byIndex = Array.IndexOf(words, "by");

			string songName;
			string artistName = "";

			if (byIndex > 1 && byIndex < words.Length - 1)
			{
				songName = string.Join(" ", words[1..byIndex]);
				artistName = string.Join(" ", words[(byIndex + 1)..]);
			}
			else
			{
				songName = string.Join(" ", words[1..]);
			}

			if (artistName == "geo") artistName = "gio.";
			if (artistName == "halsey") artistName = "hulvey";

			NowPlayingLabel.Text = artistName == ""
					? $"Searching {songName}"
					: $"Searching {songName} - {artistName}";

			playSong(songName, artistName);
		}


		else if (heard.Contains("add") && heard.Contains("by") && heard.Contains("to") && hasQueueWord)
		{
			if (words[0] != "add") return;

			int byIndex = Array.IndexOf(words, "by");
			int toIndex = Array.LastIndexOf(words, "to");

			if (byIndex <= 1 || toIndex <= byIndex + 1 || toIndex >= words.Length) return;

			string songName = string.Join(" ", words[1..byIndex]);
			string artistName = string.Join(" ", words[(byIndex + 1)..toIndex]);

			if (artistName == "geo") artistName = "gio.";
			if (artistName == "halsey") artistName = "hulvey";

			NowPlayingLabel.Text = $"Searching {songName} - {artistName}";

			queueSong(songName, artistName);
		}
		else if (heard.Contains("playlist"))
		{

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
			NowPlayingLabel.Text = "Sorry, I didn't get that.";
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
		HideQuickOpenButton();
		HideScreenText();
		var audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
		audioManager?.AdjustStreamVolume(Android.Media.Stream.Music, Adjust.Raise, VolumeNotificationFlags.ShowUi);
	}

	private void OnVolumeDownClicked(object? sender, EventArgs e)
	{
		HideQuickOpenButton();
		HideScreenText();
		var audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
		audioManager?.AdjustStreamVolume(Android.Media.Stream.Music, Adjust.Lower, VolumeNotificationFlags.ShowUi);
	}

	private async void Reload(object? sender, EventArgs e)
	{
		HideQuickOpenButton();
		HideScreenText();
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
		HideQuickOpenButton();
		HideScreenText();
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

		try
		{
			var response = await SendSpotifyRequestAsync(http =>
											http.GetAsync("https://api.spotify.com/v1/me/player/currently-playing"));

			if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
			{
				SetNowPlaying("Nothing playing");
				isPlaying = false;
				SetPlayPauseText("▶ PLAY");
				return;
			}

			if (!response.IsSuccessStatusCode)
			{
				System.Diagnostics.Debug.WriteLine($"[Refresh] Non-success: {(int)response.StatusCode}");
				return;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var root = doc.RootElement;

			isPlaying = root.TryGetProperty("is_playing", out var playingProp) && playingProp.GetBoolean();
			SetPlayPauseText(isPlaying ? "PAUSE" : "▶ PLAY");

			if (!root.TryGetProperty("item", out var track) || track.ValueKind != System.Text.Json.JsonValueKind.Object)
			{
				SetNowPlaying(isPlaying ? "Playing..." : "Nothing playing");
				return;
			}

			var title = track.GetProperty("name").GetString();
			var artist = track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0
											? artists[0].GetProperty("name").GetString()
											: "Unknown artist";


			SetNowPlaying($"{title} — {artist}");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Refresh] FAILED: {ex}");
			SetNowPlaying($"Refresh error: {ex.Message}");
		}
	}
	void InitVisualizer()
	{
		_bars = new List<BoxView> { Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8, Bar9 };

		_visualizerTimer = Dispatcher.CreateTimer();
		_visualizerTimer.Interval = TimeSpan.FromMilliseconds(280);
		_visualizerTimer.Tick += (s, e) => AnimateBars();
		_visualizerTimer.Start();
	}

	void AnimateBars()
	{
		for (int i = 0; i < _bars.Count; i++)
		{
			var bar = _bars[i];
			double target = isPlaying ? _rand.Next(6, 38) : 6;

			new Animation(v => bar.HeightRequest = v, bar.HeightRequest, target)
					.Commit(this, $"BarAnim{i}", 16, 260, Easing.SinInOut);
		}
	}
}