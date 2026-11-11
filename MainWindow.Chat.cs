using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Chat tab: any OpenRouter model, the agency roles, ADHD-style answers and the 15-minute check-in.
public partial class MainWindow
{
    private const int HistoryLimit = 8;

    private readonly List<ChatTurn> chatHistory = new();
    private readonly DispatcherTimer checkInTimer = new();
    private bool chatBusy;

    // The role and model lists drop down into their own window, outside the island: do not treat a click there as "outside".
    private bool AnyDropDownOpen => PersonaBox.IsDropDownOpen || ModelBox.IsDropDownOpen || TamerDropDownOpen;
    private bool chatReady;
    private bool awaitingCheckIn;
    private string sessionGoal = "";
    private string checkInLog = "";

    private void InitChat()
    {
        PersonaBox.ItemsSource = AiService.Personas;
        PersonaBox.SelectedItem = AiService.Personas.Contains(SettingsStore.Current.AiPersona) ? SettingsStore.Current.AiPersona : "Auto";
        // OpenRouter only for now (the OmniRoute switch is hidden; its code stays for later)
        SettingsStore.Current.AiProvider = "OpenRouter";
        // a model saved by an older build that is not in the curated list falls back to the free router
        if (AiService.BestModels.All(m => m.Id != SettingsStore.Current.AiModel))
        {
            SettingsStore.Current.AiModel = AiService.AutoFreeModel;
        }
        RefreshModelList();
        UpdateProviderUi();
        checkInTimer.Tick += (_, _) => CheckInDue();

        AddBubble("Team", AiService.HasKey || AiService.UsingOmniRoute
            ? "Ask anything. Pick a role, or leave it on Auto and I answer as whoever is needed."
            : "Tap Key and paste your OpenRouter key (free at openrouter.ai/keys), or switch to OmniRoute there.", false);
    }

    // OpenRouter shows the hand-picked list; OmniRoute shows whatever models its dashboard has connected.
    private void RefreshModelList(bool announce = false)
    {
        chatReady = false;
        if (!AiService.UsingOmniRoute)
        {
            ModelBox.ItemsSource = AiService.BestModels.Select(m => m.Label).ToList();
            ModelBox.SelectedItem = AiService.LabelFor(SettingsStore.Current.AiModel);
            chatReady = true;
            return;
        }

        string current = SettingsStore.Current.AiOmniModel;
        ModelBox.ItemsSource = current.Length > 0 ? new[] { current } : new[] { "Loading models…" };
        ModelBox.SelectedIndex = 0;
        chatReady = true;
        _ = LoadOmniModelsAsync(announce);
    }

    private async Task LoadOmniModelsAsync(bool announce)
    {
        try
        {
            List<string> models = await AiService.ListModelsAsync();
            if (!AiService.UsingOmniRoute)
            {
                return;
            }
            chatReady = false;
            if (announce)
            {
                AddBubble("Team", models.Count == 0
                    ? "Connected to OmniRoute, but it has no models yet. Open its dashboard (http://localhost:20128) and connect a provider."
                    : $"Connected to OmniRoute: {models.Count} models available.", false);
            }
            if (models.Count == 0)
            {
                ModelBox.ItemsSource = new[] { "No models yet: connect a provider in the OmniRoute dashboard" };
                ModelBox.SelectedIndex = 0;
            }
            else
            {
                if (!models.Contains(SettingsStore.Current.AiOmniModel))
                {
                    SettingsStore.Current.AiOmniModel = models[0];
                }
                ModelBox.ItemsSource = models;
                ModelBox.SelectedItem = SettingsStore.Current.AiOmniModel;
            }
            chatReady = true;
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            chatReady = false;
            ModelBox.ItemsSource = new[] { "OmniRoute needs its key: tap Key" };
            ModelBox.SelectedIndex = 0;
            chatReady = true;
            if (announce)
            {
                AddBubble("Team", "OmniRoute rejected that key. Copy the key again from its dashboard (Endpoints page), paste it in the key field and press Save.", false);
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            chatReady = false;
            ModelBox.ItemsSource = new[] { "OmniRoute not reachable: run `omniroute`" };
            ModelBox.SelectedIndex = 0;
            chatReady = true;
            if (announce)
            {
                AddBubble("Team", "I could not reach OmniRoute at " + SettingsStore.Current.AiBaseUrl + ". Start it by running `omniroute` in a terminal.", false);
            }
        }
    }

    private string ActiveModel => AiService.UsingOmniRoute ? SettingsStore.Current.AiOmniModel : SettingsStore.Current.AiModel;

    private void UpdateProviderUi()
    {
        bool omni = AiService.UsingOmniRoute;
        UpdateSelection(new Dictionary<string, Button>
        {
            ["OpenRouter"] = ProviderOpenRouter,
            ["OmniRoute"] = ProviderOmniRoute
        }, AiService.ProviderName);
        BaseUrlRow.Visibility = omni ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlBox.Text = SettingsStore.Current.AiBaseUrl;
        KeyHint.Text = omni ? "Paste OmniRoute key (blank if none)" : "Paste OpenRouter key";
        KeyHint.Visibility = KeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KeyBox_Changed(object sender, RoutedEventArgs e)
    {
        KeyHint.Visibility = KeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string provider } && provider != SettingsStore.Current.AiProvider)
        {
            SettingsStore.Current.AiProvider = provider;
            SettingsStore.Save();
            UpdateProviderUi();
            RefreshModelList();
        }
        e.Handled = true;
    }

    private void Persona_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (chatReady && PersonaBox.SelectedItem is string persona)
        {
            SettingsStore.Current.AiPersona = persona;
            SettingsStore.Save();
        }
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (chatReady && ModelBox.SelectedItem is string label)
        {
            if (AiService.UsingOmniRoute)
            {
                if (label.Contains(' '))
                {
                    return; // a status line, not a model
                }
                SettingsStore.Current.AiOmniModel = label;
            }
            else
            {
                SettingsStore.Current.AiModel = AiService.IdFor(label);
            }
            SettingsStore.Save();
        }
    }

    // ---- API key -------------------------------------------------------------------------

    private void Key_Click(object sender, RoutedEventArgs e)
    {
        KeyRow.Visibility = KeyRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (KeyRow.Visibility == Visibility.Visible)
        {
            UpdateProviderUi();
            SetTall(true); // room for the key fields
            KeyBox.Focus();
        }
    }

    private void KeySave_Click(object sender, RoutedEventArgs e) => SaveKeyFromBox();

    private void KeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveKeyFromBox();
            e.Handled = true;
        }
    }

    private void SaveKeyFromBox()
    {
        string key = KeyBox.Password.Trim();
        if (AiService.UsingOmniRoute)
        {
            string url = BaseUrlBox.Text.Trim();
            if (url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                SettingsStore.Current.AiBaseUrl = url;
                SettingsStore.Save();
            }
            else
            {
                AddBubble("Team", "That OmniRoute address is not valid. Example: http://localhost:20128/v1", false);
                return;
            }
        }
        if (key.Length > 0)
        {
            AiService.SaveKey(key);
            AddBubble("Team", $"{AiService.ProviderName} key saved on this PC (encrypted).", false);
        }
        else
        {
            AddBubble("Team", AiService.HasKey
                ? $"No new key entered, so I kept your saved {AiService.ProviderName} key."
                : $"No {AiService.ProviderName} key was entered, so nothing was saved. Paste the key into the key field first.", false);
        }
        KeyBox.Clear();
        KeyRow.Visibility = Visibility.Collapsed;
        if (AiService.UsingOmniRoute)
        {
            RefreshModelList(announce: true); // checks the key against OmniRoute and reports the result here
        }
    }

    // ---- Conversation --------------------------------------------------------------------

    private void ChatInput_Changed(object sender, TextChangedEventArgs e)
    {
        ChatHint.Visibility = ChatInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Images: attach a file or paste one; a model that can see it describes it --------

    private byte[]? pendingImage;
    private bool pickingImage;

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff" };
        pickingImage = true;
        bool? picked = dialog.ShowDialog(this);
        pickingImage = false;
        if (picked == true)
        {
            try
            {
                BitmapImage image = new();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(dialog.FileName);
                image.EndInit();
                SetPendingImage(image);
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or InvalidOperationException or UriFormatException)
            {
                AddBubble("Team", "I could not open that image.", false);
            }
        }
    }

    private void ImageRemove_Click(object sender, RoutedEventArgs e)
    {
        pendingImage = null;
        pendingLook = false;
        ImageChip.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    // Shrunk to 1280 px and saved as JPEG so it is quick to send and cheap in tokens.
    private void SetPendingImage(BitmapSource source, int maxSide = 1280)
    {
        pendingLook = false;
        ChipText.Text = "Image ready: the model will look at it";
        double scale = Math.Min(1.0, maxSide / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource sized = scale < 1.0 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        JpegBitmapEncoder encoder = new() { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(sized));
        using System.IO.MemoryStream stream = new();
        encoder.Save(stream);
        pendingImage = stream.ToArray();
        ChipThumb.Source = sized;
        ImageChip.Visibility = Visibility.Visible;
    }

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource pasted)
                {
                    SetPendingImage(pasted);
                    e.Handled = true;
                    return;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            e.Handled = true;
            _ = SendChatAsync();
        }
    }

    private async Task SendChatAsync()
    {
        try
        {
            await SendChatCoreAsync();
        }
        catch (Exception ex)
        {
            chatBusy = false;
            AddBubble("Team", "Chat failed: " + ex.Message, false);
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(DataFolder, "errors.log"), $"[{DateTime.Now:s}] chat: {ex}\n");
            }
            catch (System.IO.IOException)
            {
            }
        }
    }

    private async Task SendChatCoreAsync()
    {
        string text = ChatInput.Text.Trim();
        byte[]? image = pendingImage;
        if ((text.Length == 0 && image == null) || chatBusy)
        {
            return;
        }
        bool look = pendingLook;
        pendingLook = false;
        if (text.Length == 0)
        {
            text = look ? "Look at my screen. What am I doing here, and what should I do next?" : "Look at this image. Tell me what it is and what matters in it.";
        }
        ChatInput.Clear();
        pendingImage = null;
        ImageChip.Visibility = Visibility.Collapsed;
        AddBubble("You", text, true, image);

        string model = ActiveModel;
        string wait = "Thinking…";
        if (image != null && model == AiService.AutoFreeModel)
        {
            model = AiService.VisionFreeModel;
            wait = "Looking (using Gemma 4, which can see images)…";
        }

        string persona = SettingsStore.Current.AiPersona;
        string system;
        if (awaitingCheckIn)
        {
            awaitingCheckIn = false;
            if (sessionGoal.Length == 0)
            {
                sessionGoal = text;
            }
            checkInLog += $"- {DateTime.Now:t}: {text}\n";
            system = AiService.SystemPrompt("Coach") + $" Session goal: {sessionGoal}. Check-ins so far:\n{checkInLog}";
        }
        else
        {
            system = AiService.SystemPrompt(persona);
        }
        if (look)
        {
            system += AiService.LookRules;
        }

        chatHistory.Add(new ChatTurn("user", text, image == null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(image)));
        while (chatHistory.Count > HistoryLimit)
        {
            chatHistory.RemoveAt(0);
        }

        chatBusy = true;
        TextBox thinking = AddBubble("Team", wait, false);
        string reply = await AiService.ChatAsync(model, system, chatHistory);
        chatBusy = false;

        thinking.Text = reply;
        chatHistory.Add(new ChatTurn("assistant", reply));

        // show the start of the answer, not the end: the arrow button makes the view tall enough to read it all
        ChatLog.UpdateLayout();
        if (thinking.Parent is FrameworkElement { Parent: FrameworkElement bubble })
        {
            double top = bubble.TranslatePoint(new Point(0, 0), ChatLog).Y;
            ChatScroll.ScrollToVerticalOffset(Math.Max(0, top - 4));
        }
    }

    private void CopyToClipboard(string text)
    {
        ignoreNextClipboard = true; // do not echo our own copy into the clipboard history
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ignoreNextClipboard = false;
        }
    }

    private ContextMenu CopyMenu(TextBox box)
    {
        ContextMenu menu = new();
        MenuItem copySelection = new() { Header = "Copy" };
        copySelection.Click += (_, _) => CopyToClipboard(box.SelectionLength > 0 ? box.SelectedText : box.Text);
        MenuItem copyAll = new() { Header = "Copy whole message" };
        copyAll.Click += (_, _) => CopyToClipboard(box.Text);
        menu.Items.Add(copySelection);
        menu.Items.Add(copyAll);
        menu.Opened += (_, _) => openMenu = menu;
        menu.Closed += (_, _) => openMenu = null;
        return menu;
    }

    private TextBox AddBubble(string who, string text, bool user, byte[]? image = null)
    {
        // a read-only TextBox so the text can be selected and copied (Ctrl + C, right click, or the Copy link)
        TextBox body = new()
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("Ink"),
            Cursor = Cursors.IBeam,
            Margin = new Thickness(0)
        };
        body.PreviewMouseWheel += (_, e) =>
        {
            ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset - e.Delta);
            e.Handled = true;
        };
        body.ContextMenu = CopyMenu(body);
        StackPanel content = new();
        if (image != null)
        {
            BitmapImage thumb = new();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.StreamSource = new System.IO.MemoryStream(image);
            thumb.DecodePixelHeight = 120;
            thumb.EndInit();
            content.Children.Add(new Image { Source = thumb, MaxHeight = 60, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4) });
        }
        content.Children.Add(body);
        if (!user)
        {
            Button copy = new()
            {
                Content = "Copy",
                Style = (Style)FindResource("LinkButton"),
                Foreground = (Brush)FindResource("Dim"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0)
            };
            copy.Click += (_, _) =>
            {
                CopyToClipboard(body.Text);
                copy.Content = "Copied";
            };
            content.Children.Add(copy);
        }
        Border bubble = new()
        {
            Child = content,
            Background = (Brush)FindResource(user ? "Fill09" : "Fill07"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(user ? 40 : 0, 0, user ? 0 : 40, 5),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            ToolTip = who
        };
        ChatLog.Children.Add(bubble);
        ChatScroll.ScrollToBottom();
        return body;
    }

    // ---- 15-minute check-in --------------------------------------------------------------
    // Borrowed from how Tesla runs: every 15 minutes, say what you did and what is next.

    private void ApplyCheckIn()
    {
        AppSettings s = SettingsStore.Current;
        bool on = s.CheckInEnabled;
        TimeSpan every = TimeSpan.FromMinutes(Math.Max(1, s.CheckInMinutes));
        if (!on)
        {
            checkInTimer.Stop();
        }
        else if (!checkInTimer.IsEnabled || checkInTimer.Interval != every)
        {
            checkInTimer.Interval = every;
            checkInTimer.Stop();
            checkInTimer.Start();
        }
        if (CheckInButton != null)
        {
            CheckInButton.Content = on ? $"{s.CheckInMinutes}m check-in: on" : $"{s.CheckInMinutes}m check-in: off";
        }
    }

    private void CheckIn_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.CheckInEnabled = !SettingsStore.Current.CheckInEnabled;
        if (SettingsStore.Current.CheckInEnabled)
        {
            sessionGoal = "";
            checkInLog = "";
            AddBubble("Coach", "Check-ins on. What is your goal for this session?", false);
            awaitingCheckIn = true;
        }
        SettingsStore.Save();
    }

    private void CheckInDue()
    {
        ShowCheckIn();
    }
}
