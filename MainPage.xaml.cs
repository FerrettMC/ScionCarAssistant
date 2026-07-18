using CommunityToolkit.Maui.Media;
using System.Globalization;
using Android.Media;
using Android.Content;
using Android.Util;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Controls;
using Microsoft.Maui;
using Microsoft.Maui.Storage;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace ScionCarAssistant;



public partial class MainPage : ContentPage
{

	private static string path = Path.Combine(FileSystem.AppDataDirectory, "data.json");

	public class SongCountType
	{
		public SongCountType(string name, string artist, int count)
		{
			Name = name;
			Artist = artist;
			Count = count;
		}

		public string Name { get; set; }
		public string Artist { get; set; }
		public int Count { get; set; }
	}

	private static SongCountType _prevSong = new SongCountType("", "", 0);

	volatile bool isPlaying = false;
	bool isListening = false;
	private bool _compactMode = false;
	static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

	IDispatcherTimer? _progressTimer;
	double _currentProgressMs = 0;
	double _currentDurationMs = 0;
	DateTime _lastProgressUpdate = DateTime.UtcNow;

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
	System.Random _rand = new();

	List<(string name, string uri)> _playlists = new();

	private List<string> _numbers = [
		"one", "two", "to", "three", "four", "for",  "five", "six", "seven", "eight", "nine", "ten",
		"eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
		"1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
		"11", "12", "13", "14", "15", "16", "17", "18", "19", "20"
	];

	const string TAG = "Scion";

	public MainPage(ISpeechToText speechToText)
	{
		InitializeComponent();

		_speechToText = speechToText;
		AppResumed -= OnAppResumed;
		AppResumed += OnAppResumed;
		InitVisualizer();
	}



	void OnTalkReleased(object? sender, EventArgs e)
	{
		if (isListening)
		{
			_speechCts?.Cancel();
			_speechToText.StopListenAsync(CancellationToken.None).ContinueWith(_ =>
				MainThread.BeginInvokeOnMainThread(ResetVoiceButton));
		}
	}


	public void SetCompactMode()
	{
		_compactMode = !_compactMode;
		TransportRow.IsVisible = !_compactMode;
		VolumeRow.IsVisible = !_compactMode;

		if (_compactMode)
		{
			VoiceBtn.HeightRequest = -1;
			VoiceBtn.VerticalOptions = LayoutOptions.Fill;
			VoiceBtn.Margin = new Thickness(0, 0, 0, 330);
		}
		else
		{
			VoiceBtn.Margin = new Thickness(0);
			VoiceBtn.HeightRequest = -1;
			VoiceBtn.VerticalOptions = LayoutOptions.Fill;
		}
	}


	public static void OpenGoogleMapsNavigateTo(string query)
	{
		if (query == "nolocation")
		{
			var context = Android.App.Application.Context;
			var intent = new Intent(Intent.ActionView);
			intent.SetPackage("com.google.android.apps.maps");
			intent.AddFlags(ActivityFlags.NewTask);
			context.StartActivity(intent);
		}
		else
		{
			var context = Android.App.Application.Context;
			var uri = Android.Net.Uri.Parse(
					$"google.navigation:q={Uri.EscapeDataString(query)}&mode=d");
			var intent = new Intent(Intent.ActionView, uri);
			intent.SetPackage("com.google.android.apps.maps");
			intent.AddFlags(ActivityFlags.NewTask);
			context.StartActivity(intent);
		}
	}

	private async void AddSong(string name, string artist)
	{
		if (name is null || artist is null) return;
		try
		{
			if (!File.Exists(path))
			{
				File.WriteAllText(path, new JObject { ["Songs"] = new JArray() }.ToString());
			}

			var json = JObject.Parse(File.ReadAllText(path));

			if (json["Songs"] == null)
			{
				json["Songs"] = new JArray();
			}

			JArray songs = (JArray)json["Songs"]!;

			songs.Add(new JObject
			{
				["Name"] = name,
				["Artist"] = artist
			});

			while (songs.Count > 1000)
			{
				songs.Remove(1000);
			}

			File.WriteAllText(path, json.ToString());
		}
		catch (Exception ex)
		{
			Log.Error(TAG, $"[AddSong] FAILED: {ex}");
		}
	}
	private SongCountType[] GetSongs()
	{
		if (!File.Exists(path))
		{
			File.WriteAllText(path, new JObject { ["Songs"] = new JArray() }.ToString());
		}

		var json = JObject.Parse(File.ReadAllText(path));

		if (json["Songs"] == null)
		{
			json["Songs"] = new JArray();
		}

		JArray songs = (JArray)json["Songs"]!;

		SongCountType[] songArray = songs
				.Select(s => new
				{
					Name = (string)((JObject)s)["Name"]!,
					Artist = (string)((JObject)s)["Artist"]!
				})
				.GroupBy(s => new { s.Name, s.Artist })
				.Select(g => new SongCountType(g.Key.Name, g.Key.Artist, g.Count()))
				.OrderByDescending(s => s.Count)
				.ToArray();

		return songArray;
	}




	// ─── LIFECYCLE ────────────────────────────────────────────────
	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_visualizerTimer?.Stop();
		_progressTimer?.Stop();
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

	// ─── HELPERS ──────────────────────────────────────────────────
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
				await Task.Delay(2000, token).ContinueWith(_ => { });
				if (token.IsCancellationRequested) break;
				if (!isListening && !_suppressPoll)
				{
					try { await RefreshPlaybackStateAsync(); }
					catch (Exception ex) { Log.Error(TAG, $"[Poll] FAILED: {ex}"); }
				}
			}
		}, token);
	}

	static string FormatTime(double ms)
	{
		if (ms < 0) ms = 0;
		var ts = TimeSpan.FromMilliseconds(ms);
		return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
	}

	// ─── SPOTIFY AUTH ─────────────────────────────────────────────
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

	// ─── PLAYLISTS ────────────────────────────────────────────────
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
				var ownerId = item.TryGetProperty("owner", out var owner) && owner.TryGetProperty("id", out var ownerIdProp)
					? ownerIdProp.GetString()
					: null;

				if (ownerId == "spotify") continue;

				_playlists.Add((name, uri));
			}

			Log.Debug(TAG, $"[Playlists] Loaded {_playlists.Count} playlists");
		}
		catch (Exception ex)
		{
			Log.Error(TAG, $"[Playlists] FETCH FAILED: {ex}");
			NowPlayingLabel.Text = $"Playlist fetch failed: {ex.Message}";
		}
	}

	async void ShowPlaylistsPopup()
	{
		Log.Debug(TAG, $"[Popup] _playlists.Count = {_playlists.Count}");
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
			"\"Play\" / \"Pause\" / \"Stop\" / \"Start\"",
			"\"Skip\" / \"Next\"",
			"\"Play __ by __\"",
			"\"Volume up/down [number]\"",
			"\"Add __ by __ to the queue\"",
			"\"Random\"",
			"\"Show Queue\"",
			"\"Show playlists\"",
			"\"Playlist [number]\"",
			"\"Info\"",
			"\"Open Spotify\"",
			"\"Maps\" / \"Open maps (location)\"",
			"\"Reload\"",
			"\"Recommend\"",
			"\"Clear recent\""
		};

		foreach (var cmd in commands)
		{
			var label = new Label
			{
				Text = cmd,
				FontSize = 24,
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
				return;

			if (!response.IsSuccessStatusCode)
			{
				Log.Warn(TAG, $"[Info] Non-success: {(int)response.StatusCode}");
				return;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var root = doc.RootElement;

			isPlaying = root.TryGetProperty("is_playing", out var playingProp) && playingProp.GetBoolean();

			if (!isPlaying) return;

			if (!root.TryGetProperty("item", out var track) || track.ValueKind != System.Text.Json.JsonValueKind.Object)
				return;

			var title = track.GetProperty("name").GetString();
			var artist = track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0
				? artists[0].GetProperty("name").GetString()
				: "Unknown artist";

			string? imageUrl = null;
			if (track.TryGetProperty("album", out var album) &&
				album.ValueKind == System.Text.Json.JsonValueKind.Object &&
				album.TryGetProperty("images", out var images) &&
				images.GetArrayLength() > 0)
			{
				var imgIndex = images.GetArrayLength() > 1 ? 1 : 0;
				if (images[imgIndex].TryGetProperty("url", out var urlProp))
					imageUrl = urlProp.GetString();
			}

			string? albumName = null;
			if (track.TryGetProperty("album", out var albumObj) &&
				albumObj.ValueKind == System.Text.Json.JsonValueKind.Object &&
				albumObj.TryGetProperty("name", out var albumNameProp))
			{
				albumName = albumNameProp.GetString();
			}

			string? releaseDate = null;
			if (track.TryGetProperty("album", out var albumForDate) &&
				albumForDate.ValueKind == System.Text.Json.JsonValueKind.Object &&
				albumForDate.TryGetProperty("release_date", out var rdProp))
			{
				var raw = rdProp.GetString();
				if (DateTime.TryParse(raw, out var parsed))
					releaseDate = parsed.ToString("MMMM d, yyyy");
				else
					releaseDate = raw;
			}

			string? duration = null;
			if (track.TryGetProperty("duration_ms", out var durProp))
			{
				var ts = TimeSpan.FromMilliseconds(durProp.GetInt32());
				duration = ts.TotalHours >= 1
					? ts.ToString(@"h\:mm\:ss")
					: ts.ToString(@"m\:ss");
			}

			bool isExplicit = track.TryGetProperty("explicit", out var explProp) && explProp.GetBoolean();

			if (imageUrl != null)
			{
				var albumImage = new Microsoft.Maui.Controls.Image
				{
					Source = new UriImageSource
					{
						Uri = new Uri(imageUrl),
						CachingEnabled = true,
						CacheValidity = TimeSpan.FromHours(1)
					},
					WidthRequest = 100,
					HeightRequest = 100,
					Aspect = Aspect.AspectFill,
					HorizontalOptions = LayoutOptions.Center
				};
				PlaylistStack.Children.Add(albumImage);
			}

			var sb = new System.Text.StringBuilder();
			sb.AppendLine(title);
			sb.AppendLine();
			sb.AppendLine($"by {artist}\n");

			if (albumName != null)
				sb.AppendLine($"from \"{albumName}\"\n");

			if (releaseDate != null)
				sb.AppendLine($"Released {releaseDate}\n");

			if (duration != null)
				sb.AppendLine($"Duration {duration}\n");

			if (isExplicit)
				sb.AppendLine("Explicit\n");

			var label = new Label
			{
				Text = sb.ToString(),
				FontSize = 35,
				TextColor = Colors.White,
				HorizontalTextAlignment = TextAlignment.Center,
				Margin = new Thickness(0, 15, 0, 0)
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
			Log.Error(TAG, $"[Info] FAILED: {ex}");
			SetNowPlaying($"info error: {ex.Message}");
		}
	}

	// ─── QUEUE ────────────────────────────────────────────────────
	async Task<List<(string song, string artist)>> GetUpcomingQueueAsync(int maxCount = 8)
	{
		var upcoming = new List<(string song, string artist)>();
		if (_accessToken == null) return upcoming;

		try
		{
			var response = await SendSpotifyRequestAsync(http =>
				http.GetAsync("https://api.spotify.com/v1/me/player/queue"));

			if (!response.IsSuccessStatusCode)
			{
				Log.Warn(TAG, $"[Queue] Non-success: {(int)response.StatusCode}");
				return upcoming;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var root = doc.RootElement;

			if (!root.TryGetProperty("queue", out var queueArray) ||
				queueArray.ValueKind != System.Text.Json.JsonValueKind.Array)
			{
				return upcoming;
			}

			foreach (var item in queueArray.EnumerateArray())
			{
				if (upcoming.Count >= maxCount) break;

				var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
				if (string.IsNullOrEmpty(name)) continue;

				var art = item.TryGetProperty("artists", out var artArr) && artArr.GetArrayLength() > 0
					? artArr[0].GetProperty("name").GetString() ?? ""
					: "";

				upcoming.Add((name, art));
			}
			return upcoming;
		}
		catch (Exception ex)
		{
			Log.Error(TAG, $"[Queue] FAILED: {ex}");
		}

		return upcoming;
	}

	async void ShowUpcomingQueue()
	{
		PlaylistStack.Children.Clear();
		List<(string, string)> queue = await GetUpcomingQueueAsync();
		if (queue.Count == 0)
		{
			NowPlayingLabel.Text = "No songs in queue";
			return;
		}
		int i = 0;
		foreach (var (song, art) in queue)
		{
			var color = i switch
			{
				0 => Colors.Green,
				1 => Colors.Orange,
				2 => Colors.Yellow,
				_ => Colors.White
			};
			var label = new Label
			{
				Text = $"{song} - {art}",
				FontSize = 32,
				TextColor = color,
				HorizontalTextAlignment = TextAlignment.Center
			};
			PlaylistStack.Children.Add(label);
			i++;
		}
		popupType = "showqueue";
		PlaylistPopup.IsVisible = true;
		await Task.Delay(10000);
		if (popupType == "showqueue")
		{
			PlaylistPopup.IsVisible = false;
		}
	}

	// ─── PLAY / QUEUE SONGS ──────────────────────────────────────
	[Obsolete]
	async void playSong(string song, string art)
	{
		if (_accessToken == null) return;
		try
		{
			string query = string.IsNullOrWhiteSpace(art)
				? Uri.EscapeDataString($"track:{song}")
				: Uri.EscapeDataString($"track:{song} artist:{art}");
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
			Log.Error(TAG, $"[play song] FAILED: {ex}");
			SetNowPlaying($"play song error: {ex.Message}");
		}
	}

	async void QueueRandomSongFromAllPlaylistsAsync()
	{
		if (_accessToken == null || _playlists.Count == 0)
		{
			NowPlayingLabel.Text = "No playlists loaded";
			return;
		}

		var allSongs = new List<(string song, string art)>();

		foreach (var (name, uri) in _playlists)
		{
			try
			{
				var playlistId = uri.Split(':').Last();
				var response = await SendSpotifyRequestAsync(http =>
					http.GetAsync($"https://api.spotify.com/v1/playlists/{playlistId}/items?limit=100"));

				if (!response.IsSuccessStatusCode)
				{
					Log.Warn(TAG, $"[all songs] skipped {name}: {(int)response.StatusCode}");
					continue;
				}

				var json = await response.Content.ReadAsStringAsync();
				using var doc = System.Text.Json.JsonDocument.Parse(json);
				var items = doc.RootElement.GetProperty("items");

				foreach (var item in items.EnumerateArray())
				{
					if (item.TryGetProperty("item", out var track) &&
						track.ValueKind == System.Text.Json.JsonValueKind.Object &&
						track.TryGetProperty("name", out var nameProp))
					{
						var songName = nameProp.GetString();
						if (string.IsNullOrEmpty(songName)) continue;

						var artistName = track.TryGetProperty("artists", out var artArr) && artArr.GetArrayLength() > 0
							? artArr[0].GetProperty("name").GetString() ?? ""
							: "";

						allSongs.Add((songName, artistName));
					}
				}
			}
			catch (Exception ex)
			{
				Log.Error(TAG, $"[all songs] FAILED on {name}: {ex}");
			}
		}

		if (allSongs.Count == 0)
		{
			NowPlayingLabel.Text = "Couldn't get any songs";
			return;
		}

		var (chosenSong, chosenArtist) = allSongs[_rand.Next(allSongs.Count)];
		NowPlayingLabel.Text = $"Queueing: {chosenSong} - {chosenArtist}";
		queueSong(chosenSong, chosenArtist, true);
	}

	async void queueSong(string song, string art, bool shouldSkip = false)
	{
		if (_accessToken == null) return;
		try
		{
			string query = string.IsNullOrWhiteSpace(art)
				? Uri.EscapeDataString($"track:{song}")
				: Uri.EscapeDataString($"track:{song} artist:{art}");
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
				if (shouldSkip)
				{
					await Task.Delay(300);
					OnSkipClicked(null, EventArgs.Empty);
				}
			}
			else
			{
				SetNowPlaying($"Queue error: {(int)queueResponse.StatusCode}");
			}
		}
		catch (Exception ex)
		{
			Log.Error(TAG, $"[queue song] FAILED: {ex}");
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
			"2" or "two" or "to" => 2,
			"3" or "three" => 3,
			"4" or "four" or "for" => 4,
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
			_ => 1
		};
	}

	// ─── SPOTIFY REQUEST WRAPPER ──────────────────────────────────
	async Task<HttpResponseMessage> SendSpotifyRequestAsync(Func<HttpClient, Task<HttpResponseMessage>> request)
	{
		_http.DefaultRequestHeaders.Authorization =
			new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

		var response = await request(_http);

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

	// ─── BUTTON HANDLERS ──────────────────────────────────────────
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
				Log.Warn(TAG, $"[PlayPause] error body: {errorBody}");
				NowPlayingLabel.Text = $"Spotify error: {(int)response.StatusCode}";
			}
		}
		catch (Exception ex)
		{
			Log.Error(TAG, $"[PlayPause] FAILED: {ex}");
			NowPlayingLabel.Text = $"Error: {ex.Message}";
		}
	}

	private async void OnVoiceClicked(object? sender, EventArgs e)
	{
		HideQuickOpenButton();
		HideScreenText();
		if (isListening)
		{
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

	// ─── VOICE COMMAND HANDLER ────────────────────────────────────
	async void OnSpeechCompleted(object? sender, SpeechToTextRecognitionResultCompletedEventArgs args)
	{
		HideQuickOpenButton();
		HideScreenText();
		var heard = args.RecognitionResult.Text?.ToLowerInvariant() ?? "";
		Log.Debug(TAG, $"[Heard] \"{heard}\"");
		await _speechToText.StopListenAsync(CancellationToken.None);
		await Task.Delay(600);

		string[] words = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		bool hasQueueWord = words.Contains("queue") || words.Contains("q") || words.Contains("cute") || words.Contains("cue");
		bool hasAddWord = words.Contains("and") || words.Contains("add");

		if (heard.Contains("open") && heard.Contains("spotify"))
		{
			OpenSpotifyApp();
		}
		else if (heard.Contains("info"))
		{
			showInfo();
		}
		else if (heard.Contains("random"))
		{
			QueueRandomSongFromAllPlaylistsAsync();
		}
		else if ((heard.Contains("show") && hasQueueWord) || heard.Equals("so cute") || (hasQueueWord && (heard.Length == 1 || heard.Length == 3 || heard.Length == 4 || heard.Length == 5)))
		{
			ShowUpcomingQueue();
		}
		else if (new[] { "queue", "q", "cute", "cue" }.Contains(words[0]))
		{
			int byIndex = Array.IndexOf(words, "by");

			string songName;
			string artistName = "";

			if (byIndex > 1 && byIndex < words.Length)
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

			queueSong(songName, artistName);
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

			queueSong(songName, artistName, true);
		}
		else if (hasAddWord && heard.Contains("to") && hasQueueWord)
		{
			if (words[0] != "add" && words[0] != "and") return;

			int toIndex = Array.LastIndexOf(words, "to");
			int byIndex = Array.IndexOf(words, "by");

			if (toIndex <= 1 || toIndex >= words.Length) return;

			string songName;
			string artistName = "";

			if (byIndex > 1 && byIndex < toIndex)
			{
				songName = string.Join(" ", words[1..byIndex]);
				artistName = string.Join(" ", words[(byIndex + 1)..toIndex]);
			}
			else
			{
				songName = string.Join(" ", words[1..toIndex]);
			}

			if (artistName == "geo") artistName = "gio.";
			if (artistName == "halsey") artistName = "hulvey";

			NowPlayingLabel.Text = artistName == ""
				? $"Searching {songName}"
				: $"Searching {songName} - {artistName}";

			queueSong(songName, artistName);
		}
		else if (heard.Contains("playlist") && (heard.Length == 8 || heard.Length == 9))
		{
			ShowPlaylistsPopup();
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
		else if (heard.Contains("open") && heard.Contains("maps"))
		{
			string after = heard.Substring(heard.IndexOf("maps") + "maps".Length).Trim();
			if (after.Length < 1)
			{
				OpenGoogleMapsNavigateTo("nolocation");
			}
			OpenGoogleMapsNavigateTo(after);
		}
		else if (heard.Contains("maps"))
		{
			SetCompactMode();
		}
		else if (heard.Contains("recommend"))
		{
			SongCountType[] songArray = GetSongs();
			HelperFunctions.Song[] songs = HelperFunctions.RecommendSongs(songArray);

			PlaylistStack.Children.Clear();
			if (songs.Length == 0)
			{
				var label = new Label
				{
					Text = $"No recent songs",
					FontSize = 50,
					TextColor = Colors.Red,
					HorizontalTextAlignment = TextAlignment.Center
				};
				PlaylistStack.Children.Add(label);
			}
			int i = 0;
			foreach (HelperFunctions.Song song in songs)
			{

				var label = new Label
				{
					Text = $"{song.Name} - {song.Artist}\n",
					FontSize = 50,
					TextColor = Colors.White,
					HorizontalTextAlignment = TextAlignment.Center
				};
				PlaylistStack.Children.Add(label);
				i++;
			}

			popupType = "recommendedSongs";
			PlaylistPopup.IsVisible = true;
			await Task.Delay(10000);
			if (popupType == "recommendedSongs")
			{
				PlaylistPopup.IsVisible = false;
			}


		}

		else if (heard.Contains("recent") && heard.Contains("clear"))
		{
			File.WriteAllText(path, new JObject { ["Songs"] = new JArray() }.ToString());
			SetNowPlaying($"Cleared all recent songs.");
		}
		else
		{
			NowPlayingLabel.Text = "Sorry, I didn't get that.";
		}

		ResetVoiceButton();
	}

	// ─── SPOTIFY APP ──────────────────────────────────────────────
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

	// ─── VOLUME ───────────────────────────────────────────────────
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

	// ─── MISC BUTTONS ─────────────────────────────────────────────
	private async void OnCommandsClicked(object? sender, EventArgs e)
	{
		ShowCommandsPopup();
		await RefreshPlaybackStateAsync();
	}

	private async void Reload(object? sender, EventArgs e)
	{
		HideQuickOpenButton();
		HideScreenText();
		await RefreshPlaybackStateAsync();
	}

	// ─── PLAY PLAYLIST ────────────────────────────────────────────
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

	// ─── SKIP ─────────────────────────────────────────────────────
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

	// ─── REFRESH PLAYBACK ─────────────────────────────────────────
	SemaphoreSlim _refreshLock = new(1, 1);
	async Task RefreshPlaybackStateAsync()
	{
		if (_accessToken == null) return;
		if (!await _refreshLock.WaitAsync(0)) return;

		try
		{
			var response = await SendSpotifyRequestAsync(http =>
				http.GetAsync("https://api.spotify.com/v1/me/player/currently-playing"));

			if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
			{
				SetNowPlaying("Nothing playing");
				isPlaying = false;
				UpdatePulsingDot();
				SetPlayPauseText("▶ PLAY");
				_currentDurationMs = 0;
				MainThread.BeginInvokeOnMainThread(() =>
				{
					SongProgressBar.Progress = 0;
					DurationLabel.Text = "0:00";
					ElapsedLabel.Text = "0:00";
				});
				return;
			}

			if (!response.IsSuccessStatusCode)
			{
				Log.Warn(TAG, $"[Refresh] Non-success: {(int)response.StatusCode}");
				return;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var root = doc.RootElement;

			isPlaying = root.TryGetProperty("is_playing", out var playingProp) && playingProp.GetBoolean();
			SetPlayPauseText(isPlaying ? "PAUSE" : "▶ PLAY");
			UpdatePulsingDot();

			if (!root.TryGetProperty("item", out var track) || track.ValueKind != System.Text.Json.JsonValueKind.Object)
			{
				SetNowPlaying(isPlaying ? "Playing..." : "Nothing playing");
				return;
			}

			var title = track.GetProperty("name").GetString();
			var artist = track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0
				? artists[0].GetProperty("name").GetString()
				: "Unknown artist";





			var progressMs = root.TryGetProperty("progress_ms", out var progressProp) ? progressProp.GetInt64() : 0;
			var durationMs = track.TryGetProperty("duration_ms", out var durationProp) ? durationProp.GetInt64() : 0;

			_currentProgressMs = progressMs;
			_currentDurationMs = durationMs;
			_lastProgressUpdate = DateTime.UtcNow;

			MainThread.BeginInvokeOnMainThread(() =>
			{
				DurationLabel.Text = FormatTime(durationMs);
				ElapsedLabel.Text = FormatTime(progressMs);
			});

			if (_prevSong.Name != title)
			{
				_prevSong.Name = title!;
				_prevSong.Artist = artist!;

				if (progressMs >= 30000)
				{
					AddSong(title!, artist!);
				}
			}

			SetNowPlaying($"{title} — {artist}");
		}
		catch (Exception ex)
		{
			Log.Error(TAG, $"[Refresh] FAILED: {ex}");
			SetNowPlaying($"Refresh error: {ex.Message}");
		}
		finally { _refreshLock.Release(); }
	}

	// ─── VISUALIZER / PROGRESS ────────────────────────────────────
	void InitVisualizer()
	{
		_visualizerTimer = Dispatcher.CreateTimer();
		_visualizerTimer.Interval = TimeSpan.FromMilliseconds(500);
		_visualizerTimer.Tick += (s, e) => UpdatePulsingDot();
		_visualizerTimer.Start();

		_progressTimer = Dispatcher.CreateTimer();
		_progressTimer.Interval = TimeSpan.FromMilliseconds(250);
		_progressTimer.Tick += (s, e) => TickProgressBar();
		_progressTimer.Start();
	}

	private bool _dotVisible = true;

	private void UpdatePulsingDot()
	{
		_dotVisible = !_dotVisible;
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (!isPlaying)
			{
				isPlayingText.Text = "--------------";
				PulsingDot.IsVisible = false;
			}
			else
			{
				isPlayingText.Text = "NOW PLAYING";
				PulsingDot.IsVisible = true;
			}
		});
	}

	async void TickProgressBar()
	{
		if (!isPlaying || _currentDurationMs <= 0 || isListening)
			return;

		var elapsed = (DateTime.UtcNow - _lastProgressUpdate).TotalMilliseconds;
		var estimatedProgress = _currentProgressMs + elapsed;
		double fraction = Math.Min(estimatedProgress / _currentDurationMs, 1.0);

		SongProgressBar.Progress = fraction;
		ElapsedLabel.Text = FormatTime(Math.Min(estimatedProgress, _currentDurationMs));
		if (DurationLabel.Text == ElapsedLabel.Text)
		{
			try
			{
				await Task.Delay(500);
				await RefreshPlaybackStateAsync();
			}
			catch (Exception)
			{
				return;
			}
		}
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