# ScionCarAssistant

Voice-controlled Spotify remote for your car. Built with .NET MAUI for Android.

Basically I got tired of fiddling with my phone while driving so I made this. Tap the big green button, say what you want, and it does the thing.

![C#](https://img.shields.io/badge/C%23-239120?logo=c-sharp&logoColor=white)
![.NET MAUI](https://img.shields.io/badge/.NET%20MAUI-512BD4?logo=dotnet&logoColor=white)
![Android](https://img.shields.io/badge/Android-3DDC84?logo=android&logoColor=white)
![Spotify](https://img.shields.io/badge/Spotify-1DB954?logo=spotify&logoColor=white)

> **Heads up** — this is built around my exact setup and my Spotify account quirks. It's not really designed as a plug-and-play solution. If you want to use it, expect to poke around in the code and tweak things to fit your own car and preferences.

## What it does

- Play, pause, skip, and control volume with your voice
- Search and play any song ("play Bohemian Rhapsody by Queen")
- Queue songs ("add HUMBLE by Kendrick Lamar to the queue")
- Browse and play your playlists by number
- Shuffle a random song from all your playlists
- See what's up next in the queue
- On-screen progress bar and audio visualizer
- Opens the Spotify app directly if you need the full UI

## Voice commands

| Command | What it does |
|---|---|
| `Play [song] by [artist]` | Searches and plays the track |
| `Pause` / `Play` / `Stop` / `Start` | Toggle playback |
| `Skip` / `Next` | Skip to next track |
| `Add [song] by [artist] to the queue` | Adds to queue |
| `Volume up [number]` / `Volume down [number]` | Adjust volume (default 1 step) |
| `Random` | Queues a random song from your playlists |
| `Show queue` | Shows upcoming tracks |
| `Show playlists` | Lists your playlists with numbers |
| `Playlist [number]` | Plays that playlist |
| `Info` | Shows current track details |
| `Open Spotify` | Launches the Spotify app |
| `Commands` | Shows available commands |
| `Reload` | Refreshes playback state |

## Setup

### Prerequisites

- .NET 10 SDK
- Android device or emulator
- A Spotify Developer account

### Spotify API

1. Go to [developer.spotify.com](https://developer.spotify.com/dashboard) and create an app
2. Add a redirect URI in your app settings (match whatever's in `SpotifyAuthService`)
3. Note your **Client ID** and **Client Secret**

### Build & run

```bash
git clone https://github.com/yourusername/ScionCarAssistant.git
cd ScionCarAssistant
dotnet build -t:Run -f net10.0-android
```

First launch will open a Spotify login page. Authorize and you're good to go.

## Project structure

```
├── MainPage.xaml / MainPage.xaml.cs   Main UI and all the logic
├── SpotifyAuthService.cs              OAuth flow, token exchange, refresh
└── MauiProgram.cs                     App startup / DI
```

Yeah it's mostly in one file. It's a car remote, not an operating system. Might split it up later, might not.

## How it works

- **Speech to text** uses the `CommunityToolkit.Maui.Media` speech-to-text package
- **Spotify API** handles everything through their Web API with bearer token auth
- **Tokens** are stored in `SecureStorage` and auto-refresh when expired
- **Polling** checks playback state every 4 seconds so the UI stays current
- **Visualizer** bars animate randomly when something is playing, flat when paused

## Notes

- Built for Android only. iOS might work with MAUI in theory but I haven't touched it and probably won't.
- The voice recognition is decent but not perfect. Mishears like "geo" → "gio." and "halsey" → "hulvey" have manual corrections baked in because those are songs I play a lot. You'll probably want to add your own.
- Volume control uses Android's system audio, not Spotify's in-app volume.

## License

Do whatever you want with it.
