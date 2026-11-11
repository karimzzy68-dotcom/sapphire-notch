using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace SapphireNotch.Windows;

// Now playing: the track, its cover and a live equalizer taken from whatever Windows says is playing
// (Spotify, YouTube Music in a browser, ...), plus time-synced lyrics from LRCLIB (title + artist only are sent).
public partial class MainWindow
{
    private sealed record LyricLine(TimeSpan At, string Text);

    private static readonly HttpClient LyricsHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private GlobalSystemMediaTransportControlsSessionManager? mediaManager;
    private GlobalSystemMediaTransportControlsSession? mediaSession;
    private readonly DispatcherTimer lyricTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<ScaleTransform> tileBars = new();
    private readonly List<ScaleTransform> pillBars = new();
    private Panel? pillBarPanel;
    private List<LyricLine> lyrics = new();
    private string nowTitle = "", nowArtist = "", lyricShown = "";
    private bool nowPlaying, nowHasTrack;
    private int trackTicket;
    private ImageBrush? coverBrush;

    private async void InitNowPlaying()
    {
        BuildBars();
        lyricTimer.Tick += (_, _) => UpdateLyric();
        try
        {
            mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mediaManager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(HookSession);
            HookSession();
        }
        catch (Exception ex)
        {
            SpeechDictation.Log("now playing unavailable: " + ex.Message);
        }
    }

    private void HookSession()
    {
        if (mediaSession != null)
        {
            mediaSession.MediaPropertiesChanged -= OnMediaChanged;
            mediaSession.PlaybackInfoChanged -= OnMediaChanged;
        }
        mediaSession = mediaManager?.GetCurrentSession();
        if (mediaSession != null)
        {
            mediaSession.MediaPropertiesChanged += OnMediaChanged;
            mediaSession.PlaybackInfoChanged += OnMediaChanged;
        }
        _ = ReadSessionAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Dispatcher.BeginInvoke(() => _ = ReadSessionAsync());

    private async Task ReadSessionAsync()
    {
        var session = mediaSession;
        if (session == null)
        {
            ShowNothingPlaying();
            return;
        }
        try
        {
            bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var props = await session.TryGetMediaPropertiesAsync();
            string title = props.Title ?? "", artist = props.Artist ?? "";
            if (title.Length == 0)
            {
                ShowNothingPlaying();
                return;
            }
            SetPlaying(playing);
            if (title == nowTitle && artist == nowArtist && nowHasTrack)
            {
                return;
            }
            nowTitle = title;
            nowArtist = artist;
            nowHasTrack = true;
            int ticket = ++trackTicket;
            lyrics = new List<LyricLine>();
            ClearNowPlayingView();
            MusicTitle.Text = title;
            MusicTitle.ToolTip = title;
            MusicDesc.Text = artist;
            _ = LoadCoverAsync(props, ticket);
            _ = RetryCoverAsync(session, ticket);
            _ = LoadLyricsAsync(title, artist, session, ticket);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
        }
    }

    private void ShowNothingPlaying()
    {
        SetPlaying(false);
        if (!nowHasTrack)
        {
            return;
        }
        nowHasTrack = false;
        nowTitle = nowArtist = "";
        trackTicket++;
        lyrics = new List<LyricLine>();
        coverBrush = null;
        ClearNowPlayingView();
        SetMusicService(musicSpotify, remember: false);
        SetCoverVisible(false);
    }

    // ---- cover ----------------------------------------------------------------------------------

    private async Task LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props, int ticket)
    {
        try
        {
            if (props.Thumbnail == null)
            {
                SetCoverVisible(false);
                return;
            }
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(copy);
            copy.Position = 0;
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 600;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            SpeechDictation.Log($"cover {image.PixelWidth}x{image.PixelHeight} from {mediaSession?.SourceAppUserModelId}");
            if (ticket != trackTicket)
            {
                return;
            }
            coverBrush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            coverBrush.Freeze();
            MusicTile.Background = coverBrush;
            SetCoverVisible(true);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            SetCoverVisible(false);
        }
    }

    // browsers publish the new cover a moment after the title, so look again once
    private async Task RetryCoverAsync(GlobalSystemMediaTransportControlsSession session, int ticket)
    {
        await Task.Delay(1800);
        if (ticket != trackTicket)
        {
            return;
        }
        try
        {
            await LoadCoverAsync(await session.TryGetMediaPropertiesAsync(), ticket);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private void SetCoverVisible(bool on)
    {
        if (MusicTile.Child is Grid grid && grid.Children.Count > 0)
        {
            grid.Children[0].Visibility = on ? Visibility.Collapsed : Visibility.Visible; // play glyph hides behind a cover
        }
    }

    // ---- equalizer bars -------------------------------------------------------------------------

    private static Panel MakeBars(List<ScaleTransform> sink, double barWidth, double maxHeight, Brush brush)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        for (int i = 0; i < 4; i++)
        {
            ScaleTransform scale = new(1, 0.3);
            panel.Children.Add(new Border
            {
                Width = barWidth, Height = maxHeight, CornerRadius = new CornerRadius(barWidth / 2), Background = brush,
                Margin = new Thickness(i == 0 ? 0 : barWidth * 0.6, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = scale
            });
            sink.Add(scale);
        }
        return panel;
    }

    private void BuildBars()
    {
        // wrap the tile's glyph so bars can sit over the cover
        TextBlock glyph = (TextBlock)MusicTile.Child;
        MusicTile.Child = null;
        Grid host = new();
        host.Children.Add(glyph);
        Panel tile = MakeBars(tileBars, 3, 14, Brushes.White);
        tile.HorizontalAlignment = HorizontalAlignment.Right;
        tile.VerticalAlignment = VerticalAlignment.Bottom;
        tile.Margin = new Thickness(0, 0, 6, 6);
        tile.Visibility = Visibility.Collapsed;
        tile.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.8 };
        host.Children.Add(tile);
        MusicTile.Child = host;

        pillBarPanel = MakeBars(pillBars, 2, 7, new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));
        pillBarPanel.HorizontalAlignment = HorizontalAlignment.Right;
        pillBarPanel.VerticalAlignment = VerticalAlignment.Center;
        pillBarPanel.Margin = new Thickness(0, 0, 14, 2);
        pillBarPanel.Visibility = Visibility.Collapsed;
        CompactSurface.Children.Add(pillBarPanel);
    }

    private void SetPlaying(bool playing)
    {
        if (playing == nowPlaying)
        {
            return;
        }
        nowPlaying = playing;
        MusicPlayGlyph.Text = playing ? "\uE39E" : "\uE3D0";
        Panel tile = (Panel)((Grid)MusicTile.Child).Children[1];
        tile.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        if (pillBarPanel != null)
        {
            pillBarPanel.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        }
        Animate(tileBars, playing);
        Animate(pillBars, playing);
        if (playing)
        {
            lyricTimer.Start();
        }
        else
        {
            lyricTimer.Stop();
        }
    }

    private static readonly double[] BarSeconds = { 0.42, 0.61, 0.5, 0.74 };

    private static void Animate(List<ScaleTransform> bars, bool on)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            if (on)
            {
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(BarSeconds[i % 4]))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase()
                });
            }
            else
            {
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                bars[i].ScaleY = 0.3;
            }
        }
    }

    // ---- lyrics ---------------------------------------------------------------------------------

    private async Task LoadLyricsAsync(string title, string artist, GlobalSystemMediaTransportControlsSession session, int ticket)
    {
        try
        {
            double seconds = 0;
            try
            {
                seconds = (session.GetTimelineProperties().EndTime - session.GetTimelineProperties().StartTime).TotalSeconds;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
            string cleanTitle = Regex.Replace(title, @"\s*[\(\[].*?(feat|remaster|live|version).*?[\)\]]", "", RegexOptions.IgnoreCase).Trim();
            string q = "track_name=" + Uri.EscapeDataString(cleanTitle) + "&artist_name=" + Uri.EscapeDataString(artist);
            string? syncedText = null;
            // exact match with length, then without length, then the search list (YouTube lengths often differ from the studio cut)
            foreach (string url in new[] { "get?" + q + (seconds > 10 ? "&duration=" + (int)Math.Round(seconds) : ""), "get?" + q, "search?" + q })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/" + url);
                request.Headers.UserAgent.ParseAdd("SapphireNotch/1.0");
                using var response = await LyricsHttp.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
                foreach (JsonElement item in items)
                {
                    if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    {
                        syncedText = s.GetString();
                        break;
                    }
                }
                if (syncedText != null)
                {
                    break;
                }
            }
            SpeechDictation.Log($"lyrics for '{cleanTitle}' / '{artist}' ({seconds:0}s): " + (syncedText == null ? "none" : "found"));
            if (syncedText == null)
            {
                return;
            }            List<LyricLine> parsed = new();
            foreach (Match m in Regex.Matches(syncedText, @"^\[(\d+):(\d+(?:\.\d+)?)\][ \t]*(.*)$", RegexOptions.Multiline))
            {
                parsed.Add(new LyricLine(TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)), m.Groups[3].Value.Trim()));
            }
            if (ticket == trackTicket && parsed.Count > 0)
            {
                lyrics = parsed;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
        }
    }

    private TimeSpan CurrentPosition(GlobalSystemMediaTransportControlsSession session, out TimeSpan length)
    {
        var timeline = session.GetTimelineProperties();
        length = timeline.EndTime - timeline.StartTime;
        TimeSpan position = timeline.Position;
        if (nowPlaying)
        {
            position += DateTimeOffset.Now - timeline.LastUpdatedTime;
        }
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (length > TimeSpan.Zero && position > length) position = length;
        return position;
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void MusicTrack_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var session = mediaSession;
        if (session == null || !nowHasTrack)
        {
            return;
        }
        try
        {
            CurrentPosition(session, out TimeSpan length);
            if (length > TimeSpan.Zero)
            {
                double fraction = Math.Clamp(e.GetPosition(MusicTrack).X / MusicTrack.ActualWidth, 0, 1);
                _ = session.TryChangePlaybackPositionAsync((long)(length.Ticks * fraction));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private void ClearNowPlayingView()
    {
        MusicFill.Width = 0;
        MusicElapsed.Text = "0:00";
        MusicRemaining.Text = "";
        LyricA.Text = LyricB.Text = LyricC.Text = LyricD.Text = "";
        lyricShown = "";
    }

    private void UpdateLyric()
    {
        var session = mediaSession;
        if (session == null || !nowHasTrack || MusicPanel.Visibility != Visibility.Visible)
        {
            return;
        }
        try
        {
            TimeSpan position = CurrentPosition(session, out TimeSpan length);
            MusicElapsed.Text = Clock(position);
            if (length > TimeSpan.Zero)
            {
                MusicRemaining.Text = "-" + Clock(length - position);
                MusicFill.Width = Math.Max(0, (MusicTrack.ActualWidth - 8) * position.TotalSeconds / length.TotalSeconds);
            }
            if (lyrics.Count == 0)
            {
                return;
            }
            int index = -1;
            for (int i = 0; i < lyrics.Count && lyrics[i].At <= position; i++)
            {
                index = i;
            }
            string key = index + ":" + nowTitle;
            if (key == lyricShown)
            {
                return;
            }
            lyricShown = key;
            string At(int i) => i >= 0 && i < lyrics.Count ? lyrics[i].Text : "";
            LyricA.Text = At(index - 1);
            LyricB.Text = index >= 0 ? At(index) : "";
            LyricC.Text = At(index + 1);
            LyricD.Text = At(index + 2);
            if (index < 0)
            {
                LyricB.Text = "♪";
            }
            LyricBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(280)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }
}