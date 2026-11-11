using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

public partial class MainWindow : Window
{
	private struct NativePoint
	{
		public int X;

		public int Y;
	}

	// Island geometry: same sizes as the macOS edition. The window is a fixed 570 x 228 canvas.
	private const double CompactWidth = 300.0;

	private const double CompactHeight = 12.0;

	private const double ExpandedWidth = 800.0;

	private const double ExpandedHeight = 252.0;

	private const double CompactRadius = 6.0;

	private const double ExpandedRadius = 26.0;

	private static readonly string[] TabOrder = { "Home", "Chat", "Music", "Dev", "Share", "Tools", "Prompter", "Tamer", "System" };

	private readonly DispatcherTimer expandTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(150.0)
	};

	private readonly DispatcherTimer collapseTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(5.0)
	};

	private readonly DispatcherTimer pointerTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(40.0)
	};

	private readonly DispatcherTimer countdownTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(1.0)
	};

	private readonly Spring shell = new Spring(0.0, 0.45, 0.78);

	// The Chat tab can grow taller (arrow button) to read long answers; its own spring, same feel.
	private readonly Spring tall = new Spring(0.0, 0.45, 0.85);

	private const double TallExtra = 270.0;

	private bool chatTall;

	private BackdropBlur? backdrop;

	private bool leftButtonDown;

	private HwndSource? source;

	private const int VoiceHotkey = 21326;

	private bool expanded;

	private bool renderingShell;

	private long lastShellFrame;

	private string currentTab = "Home";

	private int tabTransition;

	private int remainingSeconds;

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int key);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out NativePoint point);

	[DllImport("user32.dll")]
	private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

	[DllImport("user32.dll")]
	private static extern bool UnregisterHotKey(nint window, int id);

	[DllImport("user32.dll")]
	private static extern bool AddClipboardFormatListener(nint window);

	[DllImport("user32.dll")]
	private static extern bool RemoveClipboardFormatListener(nint window);

	public MainWindow()
	{
		InitializeComponent();
		ExpandedSurface.RenderTransformOrigin = new Point(0.5, 0.0);
		ExpandedSurface.RenderTransform = new ScaleTransform(1.0, 1.0);
		base.Loaded += delegate
		{
			PositionAtTop();
			SelectTab("Home", animate: false);
			SelectDevMode("Git");
			SelectTool("Timer");
			InitStats();
			InitSettings();
			InitPersonalAI();
			InitNowPlaying();
			InitStatus();
			InitTools();
			InitSwipe();
			ApplyShellFrame();
		};
		expandTimer.Tick += delegate
		{
			expandTimer.Stop();
			if (base.IsMouseOver && !expanded)
			{
				Expand();
			}
		};
		collapseTimer.Tick += delegate
		{
			collapseTimer.Stop();
			if (!ShouldHoldOpen)
			{
				Collapse();
			}
		};
		countdownTimer.Tick += delegate
		{
			TickCountdown();
		};
		base.ContentRendered += delegate
		{
			Dispatcher.BeginInvoke(new Action(delegate
			{
				shapeReady = true;
				UpdateHitRegion();
				// give the compositor a few frames to receive the first real paint, then reveal
				System.Windows.Threading.DispatcherTimer reveal = new() { Interval = TimeSpan.FromMilliseconds(250) };
				reveal.Tick += delegate
				{
					reveal.Stop();
					if (source != null)
					{
						DwmHelper.Cloak(source.Handle, false);
					}
					// the tour starts once the hello animation has faded (about 4 s), or at once when there is none
					StartTourIfFirstRun(StartupHello.Play() ? 4.6 : 0.8);
				};
				reveal.Start();
			}), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
		};
		base.SourceInitialized += delegate
		{
			source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
			source?.AddHook(WindowMessage);
			DwmHelper.Cloak(source!.Handle, true);
			DwmHelper.MakeTransparent(source!.Handle, source.CompositionTarget);
				UpdateHitRegion();
				backdrop = new BackdropBlur(this, BackdropImage);
				SetCanvasHeight(ExpandedHeight);
			AddClipboardFormatListener(source!.Handle);
			ApplyShellFrame();
			RegisterHotKey(source.Handle, LookHotkey, 16387u, 0x4Cu); // Ctrl + Alt + L
				bool voiceKeyOk = RegisterHotKey(source.Handle, 21326, 16387u, 32u);
				SpeechDictation.Log("hotkey Ctrl+Alt+Space registered: " + voiceKeyOk);
				if (!voiceKeyOk)
			{
				VoiceButton.ToolTip = "Dictate: click the mic, speak, click again";
			}
		};
		pointerTimer.Tick += delegate
		{
			TrackForeground(); CheckGameMode();
				bool flag = GetAsyncKeyState(1) < 0;
			if (flag && !leftButtonDown && expanded && !AnyDropDownOpen && GetCursorPos(out var point))
			{
				Point val = PointFromScreen(new Point(point.X, point.Y));
				double left = (base.ActualWidth - Island.ActualWidth) / 2.0;
				if (val.X < left || val.Y < 0.0 || val.X > left + Island.ActualWidth || val.Y > Island.ActualHeight)
				{
					Collapse();
				}
			}
			leftButtonDown = flag;
		};
		pointerTimer.Start();
		base.Closed += delegate
		{
			pointerTimer.Stop();
			expandTimer.Stop();
			collapseTimer.Stop();
			countdownTimer.Stop();
			backdrop?.Dispose();
			StopShell();
			if (source != null)
			{
				RemoveClipboardFormatListener(source.Handle);
				UnregisterHotKey(source.Handle, 21326);
					UnregisterHotKey(source.Handle, LookHotkey);
				source.RemoveHook(WindowMessage);
			}
		};
	}

	// Keep the island open while a menu is dropped down or you are typing in it.
	private bool ShouldHoldOpen => base.IsMouseOver || (openMenu?.IsOpen ?? false) || (Island.ContextMenu?.IsOpen ?? false) || (base.IsActive && (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is PasswordBox)) || chatBusy || pickingImage || dictating || AnyDropDownOpen || tourActive || pinnedOpen || CheckInPanel.Visibility == Visibility.Visible;

	private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
	{
		if (message == 786 && ((IntPtr)wParam).ToInt32() == LookHotkey)
		{
			handled = true;
			LookNow(fullScreen: false, fromHotkey: true);
		}
		if (message == 786 && ((IntPtr)wParam).ToInt32() == 21326)
		{
			handled = true;
			DispatcherTimer release = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(40.0)
			};
			release.Tick += delegate
			{
				if (GetAsyncKeyState(17) >= 0 && GetAsyncKeyState(18) >= 0 && GetAsyncKeyState(32) >= 0)
				{
					release.Stop();
					ToggleDictation();
				}
			};
			release.Start();
		}
		else if (message == 0x031D)
		{
			ClipboardChanged();
		}
		else if (message == 0x020E && expanded)
		{
			// two-finger horizontal swipe on a precision touchpad (or a tilt wheel)
			int delta = (short)(((long)wParam >> 16) & 0xFFFF);
			handled = TouchpadSwipe(delta);
		}
		return IntPtr.Zero;
	}

	private void PositionAtTop()
	{
		base.Left = (SystemParameters.PrimaryScreenWidth - ExpandedWidth) / 2.0;
		base.Top = 0.0;
	}

	private void Window_MouseEnter(object sender, MouseEventArgs e)
	{
		collapseTimer.Stop();
		if (!expanded && SettingsStore.Current.OpenOnHover)
		{
			expandTimer.Start();
		}
	}

	private void Window_MouseLeave(object sender, MouseEventArgs e)
	{
		expandTimer.Stop();
		if (expanded)
		{
			collapseTimer.Start();
		}
	}

	private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!expanded)
		{
			Expand();
		}
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (!expanded)
		{
			return;
		}
		if (e.Key == Key.Escape)
		{
			Collapse();
			e.Handled = true;
		}
		else if ((e.Key == Key.Left || e.Key == Key.Right) && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) && !(Keyboard.FocusedElement is ComboBox) && !(Keyboard.FocusedElement is PasswordBox))
		{
			NavigatePage(e.Key == Key.Right ? 1 : -1);
			e.Handled = true;
		}
	}

	private void CollapseButton_Click(object sender, RoutedEventArgs e)
	{
		Collapse();
		e.Handled = true;
	}

	private void Quit_Click(object sender, RoutedEventArgs e)
	{
		Application.Current.Shutdown();
	}

	private void Voice_Click(object sender, RoutedEventArgs e)
	{
		ToggleDictation();
		e.Handled = true;
	}

	private void Expand()
	{
		if (expanded)
		{
			return;
		}
		expandTimer.Stop();
		collapseTimer.Stop();
		expanded = true;
		backdrop?.Refresh();
		UpdateHitRegion();
		ExpandedSurface.Visibility = Visibility.Visible;
		StartStats();
		RevealContent(opening: true);
		SetShellTarget(1.0);
	}

	private void Collapse()
	{
		if (!expanded)
		{
			return;
		}
		expandTimer.Stop();
		collapseTimer.Stop();
		expanded = false;
		SetTall(false);
		StopStats();
		Keyboard.ClearFocus();
		RevealContent(opening: false);
		SetShellTarget(0.0);
	}

	// Content blooms in as the island springs open: scale up, blur away, fade in (reverse on close).
	private void RevealContent(bool opening)
	{
		ScaleTransform scale = (ScaleTransform)ExpandedSurface.RenderTransform;
		ExpandedSurface.Effect = null; // a blur effect on a layered window is drawn in software and made opening choppy
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, Animate(opening ? 0.8 : scale.ScaleX, opening ? 1.0 : 0.9, 350));
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, Animate(opening ? 0.8 : scale.ScaleY, opening ? 1.0 : 0.9, 350));
		DoubleAnimation fade = Animate(opening ? 0.0 : ExpandedSurface.Opacity, opening ? 1.0 : 0.0, 350);
		fade.Completed += delegate
		{
			if (!expanded)
			{
				ExpandedSurface.Visibility = Visibility.Collapsed;
			}
		};
		ExpandedSurface.BeginAnimation(UIElement.OpacityProperty, fade);
	}

	private static DoubleAnimation Animate(double from, double to, int milliseconds)
	{
		return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds((!SystemParameters.ClientAreaAnimation) ? 1 : milliseconds))
		{
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}
		};
	}

	// ---- Island motion: one spring drives width, height and corner radius together ----------

	private void SetShellTarget(double target)
	{
		shell.Target = target;
		StartShellLoop();
	}

	private void StartShellLoop()
	{
		if (backdrop != null)
		{
			backdrop.Active = true;
		}
		if (!SystemParameters.ClientAreaAnimation)
		{
			shell.Snap(shell.Target);
			tall.Snap(tall.Target);
			StopShell();
			ApplyShellFrame();
			return;
		}
		if (!renderingShell)
		{
			renderingShell = true;
			lastShellFrame = Stopwatch.GetTimestamp();
			CompositionTarget.Rendering += RenderShell;
		}
	}

	private void RenderShell(object? sender, EventArgs e)
	{
		long now = Stopwatch.GetTimestamp();
		double seconds = Math.Min(0.05, Stopwatch.GetElapsedTime(lastShellFrame, now).TotalSeconds);
		lastShellFrame = now;
		shell.Step(seconds);
		tall.Step(seconds);
		ApplyShellFrame();
		if (shell.IsSettled && tall.IsSettled)
		{
			StopShell();
		}
	}

	private void StopShell()
	{
		CompositionTarget.Rendering -= RenderShell;
		renderingShell = false;
		if (backdrop != null)
		{
			backdrop.Active = expanded; // grab only while open
			if (!expanded)
			{
				backdrop.Refresh(); // one last picture so the closed pill matches what is behind it
			}
		}
		if (!chatTall && tall.Value == 0.0)
		{
			SetCanvasHeight(ExpandedHeight); // back to the small canvas once the tall view has shrunk
		}
		UpdateHitRegion();
	}

	private void SetCanvasHeight(double height)
	{
		// the window stays full size (resizing it stalls the screen); only its visible shape and content change
		if (backdrop != null)
		{
			backdrop.HeightPixels = (int)Math.Round(height * VisualTreeHelper.GetDpi(this).DpiScaleY);
		}
		ExpandedSurface.Height = height;
		BackdropImage.Height = height;
		UpdateHitRegion();
	}

	private void SetTall(bool on)
	{
		if (chatTall == on)
		{
			return;
		}
		chatTall = on;
		if (on)
		{
			SetCanvasHeight(ExpandedHeight + TallExtra);
		}
		else
		{
			// the new page lays out at its own short size straight away; only the glass behind it shrinks gradually
			ExpandedSurface.Height = ExpandedHeight;
		}
		tall.Target = on ? 1.0 : 0.0;
		ChatExpandIcon.Text = on ? "\uE13C" : "\uE136"; // chevron up / down
		StartShellLoop();
	}

	private void ChatExpand_Click(object sender, RoutedEventArgs e)
	{
		SetTall(!chatTall);
		e.Handled = true;
	}

	private void ApplyShellFrame()
	{
		if (Island == null)
		{
			return;
		}
		double t = shell.Value;
		double width = Math.Max(CompactWidth * 0.9, CompactWidth + (ExpandedWidth - CompactWidth) * t);
		double fullHeight = ExpandedHeight + TallExtra * tall.Value;
		double height = Math.Max(CompactHeight * 0.9, CompactHeight + (fullHeight - CompactHeight) * t);
		double radius = Math.Min(Math.Max(2.0, CompactRadius + (ExpandedRadius - CompactRadius) * t), height / 2.0);
		Island.Width = width;
		Island.Height = height;
		Island.CornerRadius = new CornerRadius(0.0, 0.0, radius, radius);

		StreamGeometry clip = new StreamGeometry();
		using (StreamGeometryContext ctx = clip.Open())
		{
			ctx.BeginFigure(new Point(0.0, 0.0), isFilled: true, isClosed: true);
			ctx.LineTo(new Point(width, 0.0), isStroked: true, isSmoothJoin: false);
			ctx.LineTo(new Point(width, height - radius), isStroked: true, isSmoothJoin: false);
			ctx.ArcTo(new Point(width - radius, height), new Size(radius, radius), 0.0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
			ctx.LineTo(new Point(radius, height), isStroked: true, isSmoothJoin: false);
			ctx.ArcTo(new Point(0.0, height - radius), new Size(radius, radius), 0.0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
		}
		clip.Freeze();
		Island.Clip = clip;
	}

	// ---- Tabs ----------------------------------------------------------------------------

	private void Tab_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string tag })
		{
			SelectTab(tag);
		}
	}

	private void HomeCard_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string tag })
		{
			SelectTab(tag);
		}
	}

	private Dictionary<string, Button> TabButtons => new Dictionary<string, Button>
	{
		["Home"] = HomeTab,
		["Chat"] = ChatTab,
		["Music"] = MusicTab,
		["Dev"] = DevTab,
		["Share"] = ShareTab,
		["Tools"] = ToolsTab,
		["Prompter"] = PrompterTab,
		["Tamer"] = TamerTab,
		["System"] = SystemTab
	};

	private List<string> VisibleTabs() => TabOrder.Where((string name) => TabButtons[name].Visibility == Visibility.Visible).ToList();

	private void NavigatePage(int direction)
	{
		List<string> tabs = VisibleTabs();
		int target = tabs.IndexOf(currentTab) + direction;
		if (target >= 0 && target < tabs.Count)
		{
			SelectTab(tabs[target]);
		}
		else
		{
			BounceStage(direction);
		}
	}

	private void SelectTab(string tab, bool animate = true)
	{
		string next = tab;
		if (next == currentTab && animate)
		{
			return;
		}
		int direction = ((Array.IndexOf(TabOrder, next) >= Array.IndexOf(TabOrder, currentTab)) ? 1 : (-1));
		currentTab = next;
		UpdateSelection(TabButtons, next);
		EnsureTabVisible(next);
		int ticket = ++tabTransition;
		if (!animate || !SystemParameters.ClientAreaAnimation || ExpandedSurface.Visibility != Visibility.Visible)
		{
			ApplyTab(next);
			return;
		}
		DoubleAnimation fadeOut = Animate(ContentStage.Opacity, 0.0, 70);
		fadeOut.Completed += delegate
		{
			if (ticket != tabTransition)
			{
				return;
			}
			ApplyTab(next);
			TranslateTransform shift = (TranslateTransform)ContentStage.RenderTransform;
			shift.X = 18 * direction;
			ContentStage.BeginAnimation(UIElement.OpacityProperty, Animate(0.0, 1.0, 170));
			shift.BeginAnimation(TranslateTransform.XProperty, Animate(shift.X, 0.0, 190));
		};
		ContentStage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
	}

	private void ApplyTab(string tab)
	{
		SyncTabLabels(tab);
		HomePanel.Visibility = ((!(tab == "Home")) ? Visibility.Collapsed : Visibility.Visible);
		ChatPanel.Visibility = ((!(tab == "Chat")) ? Visibility.Collapsed : Visibility.Visible);
		PrompterPanel.Visibility = ((!(tab == "Prompter" && !copilotMode)) ? Visibility.Collapsed : Visibility.Visible);
		CopilotPanel.Visibility = ((!(tab == "Prompter" && copilotMode)) ? Visibility.Collapsed : Visibility.Visible);
		TamerPanel.Visibility = ((!(tab == "Tamer")) ? Visibility.Collapsed : Visibility.Visible);
		if (tab == "Tamer")
		{
			Dispatcher.BeginInvoke(new Action(RefreshTamerList), System.Windows.Threading.DispatcherPriority.ContextIdle); // after the tab has started to appear
		}
		MusicPanel.Visibility = ((!(tab == "Music")) ? Visibility.Collapsed : Visibility.Visible);
		DevPanel.Visibility = ((!(tab == "Dev")) ? Visibility.Collapsed : Visibility.Visible);
		SharePanel.Visibility = ((!(tab == "Share")) ? Visibility.Collapsed : Visibility.Visible);
		ToolsPanel.Visibility = ((!(tab == "Tools")) ? Visibility.Collapsed : Visibility.Visible);
		SystemPanel.Visibility = ((!(tab == "System")) ? Visibility.Collapsed : Visibility.Visible);
		ContentStage.BeginAnimation(UIElement.OpacityProperty, null);
		ContentStage.Opacity = 1.0;
		if (tab == "Tamer")
		{
			SetTall(true);
		}
		else if (tab == "Dev")
		{
			SetTall(PersonalAIPanel.Visibility == Visibility.Visible || AgentsPanel.Visibility == Visibility.Visible); // room for two rows of account cards
		}
		else if (tab != "Chat")
		{
			SetTall(false);
		}
		UpdateStatsView();
	}

	// The selected button becomes a lit glass pill (bright rim on opposite corners).
	private static void UpdateSelection(Dictionary<string, Button> buttons, string selected)
	{
		foreach (KeyValuePair<string, Button> button2 in buttons)
		{
			button2.Deconstruct(out var key, out var value);
			string text = key;
			Button button = value;
			bool on = text == selected;
			button.Background = on ? ((Brush)Application.Current.Resources["GlassSelected"]) : Brushes.Transparent;
			button.BorderBrush = on ? ((Brush)Application.Current.Resources["GlassRim"]) : Brushes.Transparent;
			button.BorderThickness = new Thickness(on ? 1.5 : 0.0);
			button.Foreground = on ? ((Brush)Application.Current.Resources["Ink"]) : Round.GetRestBrush(button);
		}
	}

	private void DevMode_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string tag })
		{
			SelectDevMode(tag);
		}
	}

	private void SelectDevMode(string mode)
	{
		GitPanel.Visibility = ((!(mode == "Git")) ? Visibility.Collapsed : Visibility.Visible);
		PersonalAIPanel.Visibility = ((!(mode == "AI")) ? Visibility.Collapsed : Visibility.Visible);
		AgentsPanel.Visibility = ((!(mode == "Agents")) ? Visibility.Collapsed : Visibility.Visible);
		UpdateSelection(new Dictionary<string, Button>
		{
			["Git"] = GitModeButton,
			["AI"] = AIModeButton,
				["Agents"] = AgentsModeButton
			}, mode);
		if (DevPanel.Visibility == Visibility.Visible)
		{
			SetTall(mode == "AI" || mode == "Agents");
		}
	}

	private void Tool_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string tag })
		{
			SelectTool(tag);
		}
	}

	private void SelectTool(string tool)
	{
		TimerContent.Visibility = ((!(tool == "Timer")) ? Visibility.Collapsed : Visibility.Visible);
		CalculatorContent.Visibility = ((!(tool == "Calculator")) ? Visibility.Collapsed : Visibility.Visible);
		ClipboardContent.Visibility = ((!(tool == "Clipboard")) ? Visibility.Collapsed : Visibility.Visible);
		NotesContent.Visibility = ((!(tool == "Notes")) ? Visibility.Collapsed : Visibility.Visible);
		CalendarContent.Visibility = ((!(tool == "Calendar")) ? Visibility.Collapsed : Visibility.Visible);
		MirrorContent.Visibility = ((!(tool == "Mirror")) ? Visibility.Collapsed : Visibility.Visible);
		UpdateSelection(new Dictionary<string, Button>
		{
			["Timer"] = TimerToolButton,
			["Calculator"] = CalculatorToolButton,
			["Clipboard"] = ClipboardToolButton,
			["Notes"] = NotesToolButton,
			["Calendar"] = CalendarToolButton,
			["Mirror"] = MirrorToolButton
		}, tool);
	}

	private static void OpenUrl(string url)
	{
		Process.Start(new ProcessStartInfo(url)
		{
			UseShellExecute = true
		});
	}

	private void OpenYouTubeMusic_Click(object sender, RoutedEventArgs e)
	{
		OpenMusicWindow(SpotifySelected ? "https://open.spotify.com/" : "https://music.youtube.com/");
	}

	private void MusicSearch_Click(object sender, RoutedEventArgs e)
	{
		string text = MusicSearchInput.Text.Trim();
		if (text.Length != 0)
		{
			string text2 = Uri.EscapeDataString(text);
			OpenMusicWindow(SpotifySelected ? ("https://open.spotify.com/search/" + text2) : ("https://music.youtube.com/search?q=" + text2));
		}
	}

	private void MusicSearchInput_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (MusicSearchPlaceholder != null)
		{
			MusicSearchPlaceholder.Visibility = ((!string.IsNullOrEmpty(MusicSearchInput.Text)) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private static void OpenMusicWindow(string url)
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		string text = new string[3]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
			Path.Combine(folderPath, "Google", "Chrome", "Application", "chrome.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
		}.FirstOrDefault(File.Exists);
		if (text == null)
		{
			OpenUrl(url);
			return;
		}
		Process.Start(new ProcessStartInfo(text)
		{
			UseShellExecute = false,
			ArgumentList = { "--app=" + url }
		});
	}

	private static void SendMediaKey(byte key)
	{
		keybd_event(key, 0, 0u, UIntPtr.Zero);
		keybd_event(key, 0, 2u, UIntPtr.Zero);
	}

	private void PreviousTrack_Click(object sender, RoutedEventArgs e)
	{
		SendMediaKey(177);
	}

	private void TogglePlayback_Click(object sender, RoutedEventArgs e)
	{
		SendMediaKey(179);
	}

	private void NextTrack_Click(object sender, RoutedEventArgs e)
	{
		SendMediaKey(176);
	}

	private void OpenNearbyShare_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("ms-settings:crossdevice");
	}

	private void OpenLocalSend_Click(object sender, RoutedEventArgs e)
	{
		ShareStatus.Text = (LaunchLocalSend(Array.Empty<string>()) ? "LocalSend opened." : "LocalSend is not installed. Download it from localsend.org.");
	}

	private void Window_DragOver(object sender, DragEventArgs e)
	{
		bool dataPresent = e.Data.GetDataPresent(DataFormats.FileDrop);
		e.Effects = (dataPresent ? DragDropEffects.Copy : DragDropEffects.None);
		if (dataPresent)
		{
			if (!expanded)
			{
				Expand();
			}
			SelectTab("Share", animate: false);
			ShareStatus.Text = "Release to open these files in LocalSend.";
		}
		e.Handled = true;
	}

	private void Window_DragLeave(object sender, DragEventArgs e)
	{
		if (SharePanel.Visibility == Visibility.Visible)
		{
			ShareStatus.Text = "Drop files onto Nearby Sharing or LocalSend";
		}
	}

	private void Window_Drop(object sender, DragEventArgs e)
	{
		if (!(e.Data.GetData(DataFormats.FileDrop) is string[] array) || array.Length == 0)
		{
			ShareStatus.Text = "That item is not a file or folder.";
			return;
		}
		SelectTab("Share", animate: false);
		ShareStatus.Text = (LaunchLocalSend(array) ? $"Opened {array.Length} item{((array.Length == 1) ? "" : "s")} in LocalSend." : "LocalSend is not installed. Install it, then drop the files again.");
		e.Handled = true;
	}

	private static bool LaunchLocalSend(IEnumerable<string> paths)
	{
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
		string text = new string[3]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LocalSend", "localsend_app.exe"),
			Path.Combine(path, "LocalSend", "localsend_app.exe"),
			Path.Combine(path, "localsend", "localsend_app.exe")
		}.FirstOrDefault(File.Exists);
		if (text == null)
		{
			return false;
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo(text)
		{
			UseShellExecute = true
		};
		foreach (string path2 in paths)
		{
			processStartInfo.ArgumentList.Add(path2);
		}
		Process.Start(processStartInfo);
		return true;
	}

	private async void ConnectGitHub_Click(object sender, RoutedEventArgs e)
	{
		GitHubStatus.Text = "One-time code copied—paste it in GitHub";
		OpenUrl("https://github.com/login/device");
		TextBlock gitHubStatus = GitHubStatus;
		gitHubStatus.Text = await GitHubService.SignInAsync();
		bool connected = gitHubStatus.Text.Contains("connected", StringComparison.OrdinalIgnoreCase) && !gitHubStatus.Text.Contains("not", StringComparison.OrdinalIgnoreCase);
		GitHubDot.Fill = (Brush)FindResource(connected ? "Green" : "Red");
	}

	private void TimerStart_Click(object sender, RoutedEventArgs e)
	{
		if (remainingSeconds <= 0)
		{
			remainingSeconds = 300;
		}
		countdownTimer.Start();
	}

	private void TimerCancel_Click(object sender, RoutedEventArgs e)
	{
		countdownTimer.Stop();
		remainingSeconds = 0;
		UpdateTimerDisplay();
	}

	private void TickCountdown()
	{
		remainingSeconds = Math.Max(0, remainingSeconds - 1);
		UpdateTimerDisplay();
		if (remainingSeconds == 0)
		{
			countdownTimer.Stop();
		}
	}

	private void UpdateTimerDisplay()
	{
		TimerDisplay.Text = $"{remainingSeconds / 60:00}:{remainingSeconds % 60:00}";
	}

	private void OpenClipboard_Click(object sender, RoutedEventArgs e)
	{
		keybd_event(91, 0, 0u, UIntPtr.Zero);
		keybd_event(86, 0, 0u, UIntPtr.Zero);
		keybd_event(86, 0, 2u, UIntPtr.Zero);
		keybd_event(91, 0, 2u, UIntPtr.Zero);
	}

	private void OpenCalendar_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("outlookcal:");
	}

	private void OpenCamera_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("microsoft.windows.camera:");
	}
}














