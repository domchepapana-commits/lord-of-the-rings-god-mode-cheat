using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WitchKingTrainer.Core;
using WinForms = System.Windows.Forms;

namespace WitchKingTrainer
{
    /// <summary>
    /// The trainer menu. One window, two ways of showing it:
    ///  * over the game: a topmost window that never takes keyboard focus, so the game keeps running in the foreground
    ///    (no Alt+Tab, no minimise, no resolution change) while the mouse works on the menu;
    ///  * on the desktop: an ordinary window (used when the game is not in front).
    /// The same layout is shown in two looks: dark and gold for The Rise of the Witch-king, blue and silver for BFME II.
    /// </summary>
    public sealed partial class MainWindow
    {
        #region Win32
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        #endregion

        readonly Application _app; readonly Window _w; readonly CheatEngine _engine = new CheatEngine(); readonly Settings _settings = Settings.Load();
        readonly InputHooks _hooks = new InputHooks(); readonly bool _startHidden;
        // index = (int)CheatId
        static readonly string[] Suffix = { "Money", "Army", "Build", "Upgrades", "Cp", "Freeze", "Power", "Cooldown", "Reveal", "Zoom", "Track", "GodU", "GodH", "GodB" };
        static readonly CheatId[] MasterSet = { CheatId.Money, CheatId.InstantArmy, CheatId.InstantBuild, CheatId.InstantUpgrades, CheatId.CommandPoints, CheatId.PowerPoints, CheatId.NoCooldowns, CheatId.RevealMap, CheatId.Zoom };
        static readonly CheatId[] GodSet = { CheatId.GodUnits, CheatId.GodHeroes, CheatId.GodBuildings };
        // index = (int)HotAction
        static readonly string[] ActionNames = { "Unlimited Money", "Instant Army", "Instant Construction + Upgrades", "Unlimited Command Points", "Reveal Entire Map", "Extended Zoom", "Gollum Tracking", "Unlimited Power Points + Unlock", "Instant Hero Level Up", "No Ability Cooldowns", "Freeze Enemy AI", "Show / Hide This Menu", "God Mode (units + heroes + buildings)", "Next Weather" };
        // key badge element -> hot action
        static readonly string[] BadgeNames = { "Money", "Army", "Build", "Upgrades", "Cp", "Reveal", "Zoom", "Track", "Power", "Hero", "Cooldown", "Freeze" };
        static readonly HotAction[] BadgeActions = { HotAction.Money, HotAction.Army, HotAction.Build, HotAction.Build, HotAction.CommandPoints, HotAction.Reveal, HotAction.Zoom, HotAction.Gollum, HotAction.Powers, HotAction.HeroLevel, HotAction.Cooldowns, HotAction.FreezeAI };
        static readonly string[] WeatherNames = { "Map default", "Clear", "Rain", "Snow", "Fog", "Storm" };

        // The two looks, taken from the games' own artwork. Each group of three: the colour written in MainWindow.xaml, then what it becomes
        //   for The Rise of the Witch-king - cold steel-blue panels, icy silver lettering and controls, gold headings (the box art),
        //   for The Battle for Middle-earth II - deep emerald panels with gold lettering and controls, jade lines (the logo).
        static readonly string[] Palette =
        {
            // accent: buttons, switches that are on, sliders
            "#F0D98A", "#E6F0F9", "#F3DE9B",   "#B8891C", "#7FA4C6", "#B5862A",   "#D4AF37", "#9CC0DF", "#D9B648",   "#F6E6A8", "#F4F9FD", "#F8EAB4",
            "#FFF8E1", "#FFFFFF", "#FFF8E1",   "#FFF3C4", "#EEF6FC", "#FFF3C4",   "#FFD54A", "#8FD0FF", "#FFD54A",   "#3A2A00", "#0A2540", "#3A2A00",
            "#2A1F08", "#0B1622", "#231A06",   "#1E1607", "#0B1622", "#1C1505",
            // lettering
            "#F0D98B", "#FFFFFF", "#FBEBB0",   "#E0C06B", "#D5E4F1", "#E3C566",   "#B8891D", "#8FAECB", "#A97E24",   "#D4AF38", "#D9B65C", "#DDBB55",   "#F0D98C", "#E8CE7E", "#F0DB93",
            "#EFE6CF", "#E6EDF4", "#EFEBDA",   "#A99C7C", "#93A5B8", "#9DB5A7",   "#8C8063", "#7C8EA1", "#7F988A",   "#7E7358", "#6C7E92", "#6F877A",   "#6E6249", "#5E6F83", "#5F776A",
            // frame and lines
            "#6B5525", "#6E8EAD", "#A58432",   "#5A4A22", "#2B3E53", "#245443",   "#D4AF39", "#6E8EAD", "#A58432",   "#8A6D1F", "#7FA3C4", "#B8963C",   "#7A6328", "#6F93B5", "#A8893A",
            "#6B5524", "#4A6480", "#3F7A62",   "#4A3F2A", "#33475C", "#2A5444",   "#3A3020", "#2A3B4F", "#1F4537",   "#9A8F76", "#8798AB", "#8CA598",
            // surfaces
            "#1D1812", "#1C2836", "#133328",   "#15110C", "#111923", "#0B2019",   "#110E0A", "#0A0F16", "#06120E",   "#211C15", "#131C27", "#0E221B",   "#272118", "#182330", "#122B21",
            "#17130E", "#0F1720", "#0A1A14",   "#2A2214", "#1A2838", "#17301F",   "#2A241B", "#1A2531", "#12281F",   "#1A1610", "#101822", "#0B1B15",   "#2A2317", "#1B2838", "#143024",
            "#3A301C", "#243549", "#1B3D2E",   "#33291A", "#203044", "#17362A",   "#16120C", "#0C131B", "#081510",   "#3A3122", "#25344A", "#1B3D2E"
        };

        readonly ToggleButton[] _sw = new ToggleButton[CheatEngine.CheatCount]; readonly TextBlock[] _st = new TextBlock[CheatEngine.CheatCount];
        ToggleButton _swMaster, _swHold; TextBlock _stMaster, _stHold, _statusText, _statusDetail, _statMoney, _statCp, _eventText, _stGollum, _stHero, _stUnlock, _stWeather, _zoomValue, _holdValue, _overlayValue, _mapHint, _hotkeyHint, _titleText, _subtitleText;
        System.Windows.Shapes.Ellipse _dot; Border _statusCard; Slider _zoom, _holdSlider, _overlaySlider; Canvas _map; FrameworkElement _mapDot, _mapRing, _root, _pageCheats, _pageSettings;
        ScaleTransform _scale; Button _btnSettings, _btnGame, _btnPatch; Popup _keyPopup, _gamePopup, _patchPopup; readonly Button[] _keyButtons = new Button[Settings.KeyCount]; int _popupAction = -1;
        RadioButton[] _cpRadios, _wxRadios; static readonly int[] CpValues = { 1000, 5000, 10000, 99999 };
        CheckBox _chkStartup, _chkHotkeys, _chkSounds, _chkTopmost;
        WinForms.NotifyIcon _tray; HwndSource _src; IntPtr _hwnd; uint _showMsg, _quitMsg; bool _updating, _exiting, _elevationTried, _overlayMode, _placing; int _renderQueued; int _lastEventSerial = -1;
        Connection _lastConn = (Connection)(-1); double _dpi0 = 1.0;
        GameId _theme; ImageSource _iconImage; string _lastEventLine = "Ready."; bool _lastEventError; string _shownPatchKey = "";

        static readonly Brush Green = Frozen("#7FD69A"), Amber = Frozen("#E8B95A"), Red = Frozen("#E57368");
        Brush Dim = Frozen("#93A5B8");
        static Brush Frozen(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

        public MainWindow(Application app, bool startHidden)
        {
            _app = app; _startHidden = startHidden;
#if !PREVIEW
            try { Directory.CreateDirectory(Settings.Folder); CheatEngine.LogPath = Path.Combine(Settings.Folder, "session.log"); CheatEngine.Log("---- trainer " + InstallInfo.Version + " started ----"); } catch { }
#endif
            _theme = _settings.Game == "bfme2" ? GameId.Bfme2 : _settings.Game == "rotwk" ? GameId.Rotwk : (_settings.LastGame == "bfme2" ? GameId.Bfme2 : GameId.Rotwk);
            _w = (Window)XamlReader.Parse(ThemedXaml(_theme));
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WitchKingTrainer.app.png"))
                {
                    var img = new BitmapImage(); img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = s; img.EndInit(); img.Freeze();
                    _iconImage = img; _w.Icon = img;
                }
            }
            catch { }
            _w.WindowStartupLocation = WindowStartupLocation.Manual;

            // ---- engine options that do not depend on the layout ----
            _engine.MoneyAmount = _settings.MoneyAmount;
            if (Array.IndexOf(CpValues, _settings.CommandPointLimit) < 0) _settings.CommandPointLimit = CpValues[2];
            _engine.CommandPointLimit = _settings.CommandPointLimit; _engine.ZoomFactor = (float)_settings.ZoomFactor;
            _engine.OnlyGame = _settings.Game == "rotwk" ? (int)GameId.Rotwk : _settings.Game == "bfme2" ? (int)GameId.Bfme2 : -1;
            _engine.PatchGate = PatchGate;

            BindUi();

            _w.Closing += (s, e) => { if (!_exiting) { e.Cancel = true; Exit(); } };
            _w.SourceInitialized += OnSourceInitialized;
            _w.SizeChanged += (s, e) => { if (_w.IsVisible && !_placing) Place(); };

#if !PREVIEW
            CreateTray();
            _hooks.Fired += a => _w.Dispatcher.BeginInvoke(new Action(() => OnHotAction(a)));
            _hooks.Typed += vk => _w.Dispatcher.BeginInvoke(new Action(() => OnSearchKey(vk)));
            _hooks.SpendCounter = () => _engine.SpendEvents; _hooks.InMatch = () => _engine.Current.Connection == Connection.InMatch;
            _hooks.HoldClicksPerSecond = _settings.HoldSpeed; _hooks.HoldEnabled = _settings.HoldToRecruit;
            _engine.Updated += snap => { _hooks.GamePid = OverlayPid; if (Interlocked.Exchange(ref _renderQueued, 1) == 0) _w.Dispatcher.BeginInvoke(new Action(RenderLatest)); };
            _engine.Start();
#endif
        }

#if PREVIEW
        /// <summary>
        /// Development aid (dev\build-preview.ps1) - NOT compiled into the released program. Draws one page of the menu into a
        /// PNG file without showing a window: no game is looked for, no hook is installed, nothing is clicked.
        /// </summary>
        public void RenderPreview(GameId game, int page, string faction, string category, string search, EngineSnapshot snap, string path)
        {
            ApplyTheme(game);
            if (faction != null) { _facKey = faction; _facBuiltFor = ""; }
            if (category != null) _facCat = category;
            _search = search ?? "";
            ShowPage(page);
            if (page == 2) { _search = search ?? ""; ShowSearch(); BuildCategories(); BuildTiles(); }
            if (snap != null) Render(snap);
            var root = (FrameworkElement)_w.Content; Size sz = new Size(0, 0);
            for (int pass = 0; pass < 3; pass++)
            {
                root.InvalidateMeasure(); root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); sz = root.DesiredSize;
                root.Arrange(new Rect(0, 0, sz.Width, sz.Height)); root.UpdateLayout();
                _w.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(sz.Width), (int)Math.Ceiling(sz.Height), 96, 96, PixelFormats.Pbgra32); bmp.Render(root);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }
#endif

        // ------------------------------------------------------------------ themes
        static string _xamlText;
        static string ThemedXaml(GameId game)
        {
            if (_xamlText == null)
                using (var sr = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("WitchKingTrainer.MainWindow.xaml"))) _xamlText = sr.ReadToEnd();
            var sb = new StringBuilder(_xamlText); int col = game == GameId.Bfme2 ? 2 : 1;
            // two passes, so that a new colour which happens to equal another colour of the layout is not replaced a second time
            for (int i = 0; i < Palette.Length; i += 3)
            {
                sb.Replace("#00" + Palette[i].Substring(1), "\u0001T" + (i / 3) + "\u0002");      // the fully transparent form (ends of the ornamental rule)
                sb.Replace(Palette[i], "\u0001" + (i / 3) + "\u0002");
            }
            for (int i = 0; i < Palette.Length; i += 3)
            {
                sb.Replace("\u0001T" + (i / 3) + "\u0002", "#00" + Palette[i + col].Substring(1));
                sb.Replace("\u0001" + (i / 3) + "\u0002", Palette[i + col]);
            }
            return sb.ToString();
        }

        /// <summary>Switches the whole menu to the look of the given game (the window itself stays where it is).</summary>
        void ApplyTheme(GameId game)
        {
            if (game == _theme) { UpdateGameUi(); return; }
            ClosePopups();
            int page = _page;
            Window fresh;
            try { fresh = (Window)XamlReader.Parse(ThemedXaml(game)); } catch (Exception ex) { ReportCrash(ex); return; }
            object content = fresh.Content; fresh.Content = null;
            _w.Content = null;
            _w.Resources = fresh.Resources; NameScope.SetNameScope(_w, NameScope.GetNameScope(fresh));
            _w.Content = content;
            try { fresh.Close(); } catch { }
            _theme = game;
            BindUi();
            ShowPage(page);
            if (_w.IsVisible) { _w.UpdateLayout(); Place(); }
            Render(_engine.Current);
        }

        // ------------------------------------------------------------------ wiring the layout (runs again after every theme change)
        void BindUi()
        {
            _updating = true;
            try
            {
                Dim = Frozen(_theme == GameId.Bfme2 ? "#9DB5A7" : "#93A5B8");
                for (int i = 0; i < Suffix.Length; i++) { _sw[i] = F<ToggleButton>("Sw" + Suffix[i]); _st[i] = F<TextBlock>("St" + Suffix[i]); _sw[i].IsChecked = _engine.GetSwitch((CheatId)i); }
                _swMaster = F<ToggleButton>("SwMaster"); _stMaster = F<TextBlock>("StMaster"); _swHold = F<ToggleButton>("SwHold"); _stHold = F<TextBlock>("StHold");
                _statusText = F<TextBlock>("StatusText"); _statusDetail = F<TextBlock>("StatusDetail"); _statMoney = F<TextBlock>("StatMoney"); _statCp = F<TextBlock>("StatCp");
                _eventText = F<TextBlock>("EventText"); _stGollum = F<TextBlock>("StGollum"); _stHero = F<TextBlock>("StHero"); _stUnlock = F<TextBlock>("StUnlock"); _stWeather = F<TextBlock>("StWeather");
                _zoomValue = F<TextBlock>("ZoomValue"); _holdValue = F<TextBlock>("HoldValue"); _overlayValue = F<TextBlock>("OverlayValue"); _mapHint = F<TextBlock>("MapHint"); _hotkeyHint = F<TextBlock>("HotkeyHint");
                _titleText = F<TextBlock>("TitleText"); _subtitleText = F<TextBlock>("SubtitleText");
                _dot = F<System.Windows.Shapes.Ellipse>("StatusDot"); _statusCard = F<Border>("StatusCard");
                _zoom = F<Slider>("ZoomSlider"); _holdSlider = F<Slider>("HoldSlider"); _overlaySlider = F<Slider>("OverlaySlider");
                _map = F<Canvas>("MapCanvas"); _mapDot = F<FrameworkElement>("MapDot"); _mapRing = F<FrameworkElement>("MapRing");
                _root = F<FrameworkElement>("Root"); _scale = F<ScaleTransform>("RootScale"); _pageCheats = F<FrameworkElement>("PageCheats"); _pageSettings = F<FrameworkElement>("PageSettings"); _pageFactions = F<FrameworkElement>("PageFactions"); _btnFactions = F<Button>("BtnFactions");
                _btnSettings = F<Button>("BtnSettings"); _btnGame = F<Button>("BtnGame"); _btnPatch = F<Button>("BtnPatch");
                _keyPopup = F<Popup>("KeyPopup"); _gamePopup = F<Popup>("GamePopup"); _patchPopup = F<Popup>("PatchPopup"); _popupAction = -1;
                _cpRadios = new[] { F<RadioButton>("Cp1000"), F<RadioButton>("Cp5000"), F<RadioButton>("Cp10000"), F<RadioButton>("Cp99999") };
                _wxRadios = new[] { F<RadioButton>("Wx1"), F<RadioButton>("Wx2"), F<RadioButton>("Wx3"), F<RadioButton>("Wx4"), F<RadioButton>("Wx5") };
                _chkStartup = F<CheckBox>("ChkStartup"); _chkHotkeys = F<CheckBox>("ChkHotkeys"); _chkSounds = F<CheckBox>("ChkSounds"); _chkTopmost = F<CheckBox>("ChkTopmost");
                if (_iconImage != null) F<Image>("AppIcon").Source = _iconImage;

                // ---- current values ----
                _cpRadios[Math.Max(0, Array.IndexOf(CpValues, _settings.CommandPointLimit))].IsChecked = true;
                _zoom.Value = _settings.ZoomFactor; _zoomValue.Text = _settings.ZoomFactor.ToString("0.0", CultureInfo.InvariantCulture) + "×";
                _holdSlider.Value = _settings.HoldSpeed; _holdValue.Text = _settings.HoldSpeed + " / sec"; _swHold.IsChecked = _settings.HoldToRecruit;
                _overlaySlider.Value = _settings.OverlayPercent; _overlayValue.Text = _settings.OverlayPercent + "%";
                _chkStartup.IsChecked = Settings.StartWithWindows; _chkHotkeys.IsChecked = _settings.Hotkeys; _chkSounds.IsChecked = _settings.Sounds; _chkTopmost.IsChecked = _settings.Topmost;
                int wx = (int)_engine.Weather; for (int i = 0; i < _wxRadios.Length; i++) _wxRadios[i].IsChecked = wx == i + 1;
                bool allOn = true; foreach (CheatId id in MasterSet) if (!_engine.GetSwitch(id)) allOn = false; _swMaster.IsChecked = allOn;
                _eventText.Text = _lastEventLine; _eventText.Foreground = _lastEventError ? Red : Dim;
            }
            finally { _updating = false; }

            // ---- window chrome ----
            F<Grid>("Header").MouseLeftButtonDown += (s, e) => { if (!_overlayMode && e.ButtonState == MouseButtonState.Pressed && !IsInsideControl(e.OriginalSource as DependencyObject, (DependencyObject)s)) { try { _w.DragMove(); } catch { } } };
            F<Button>("BtnHide").Click += (s, e) => HideMenu();
            F<Button>("BtnClose").Click += (s, e) => Exit();
            _btnSettings.Click += (s, e) => ShowPage(_page == 1 ? 0 : 1);
            _btnFactions.Click += (s, e) => ShowPage(_page == 2 ? 0 : 2);
            BindFactions(); BindHeroes();
            _btnGame.Click += (s, e) => { if (_gamePopup.IsOpen) _gamePopup.IsOpen = false; else OpenGamePopup(); };
            _btnPatch.Click += (s, e) => { if (_patchPopup.IsOpen) _patchPopup.IsOpen = false; else OpenPatchPopup(); };

            // ---- switches ----
            for (int i = 0; i < Suffix.Length; i++)
            {
                CheatId id = (CheatId)i; ToggleButton t = _sw[i];
                t.Checked += (s, e) => OnSwitch(id, true); t.Unchecked += (s, e) => OnSwitch(id, false);
            }
            _swMaster.Checked += (s, e) => OnMaster(true); _swMaster.Unchecked += (s, e) => OnMaster(false);
            _swHold.Checked += (s, e) => OnHold(true); _swHold.Unchecked += (s, e) => OnHold(false);
            // cards that hold extra controls (sliders, pills, buttons) only toggle from their title row
            string[] clickable = { "CardMaster", "CardMoney", "CardArmy", "CardBuild", "CardUpgrades", "HeadHold", "HeadCp", "CardFreeze", "HeadPower", "CardCooldown", "CardReveal", "HeadZoom" };
            ToggleButton[] target = { _swMaster, _sw[0], _sw[1], _sw[2], _sw[3], _swHold, _sw[(int)CheatId.CommandPoints], _sw[(int)CheatId.FreezeAI], _sw[(int)CheatId.PowerPoints], _sw[(int)CheatId.NoCooldowns], _sw[(int)CheatId.RevealMap], _sw[(int)CheatId.Zoom] };
            for (int i = 0; i < clickable.Length; i++) MakeCardClickable(clickable[i], target[i]);

            for (int i = 0; i < _cpRadios.Length; i++)
            {
                int value = CpValues[i];
                _cpRadios[i].Checked += (s, e) => { if (_updating) return; _settings.CommandPointLimit = value; _engine.CommandPointLimit = value; _settings.Save(); };
            }
            for (int i = 0; i < _wxRadios.Length; i++)
            {
                WeatherMode mode = (WeatherMode)(i + 1);
                _wxRadios[i].Checked += (s, e) => { if (_updating) return; _engine.Weather = mode; Cue(true); };
            }
            F<Button>("BtnWeatherDefault").Click += (s, e) => { SetWeather(WeatherMode.Default); Cue(false); };
            _zoom.ValueChanged += (s, e) => { double v = Math.Round(_zoom.Value * 10) / 10.0; _zoomValue.Text = v.ToString("0.0", CultureInfo.InvariantCulture) + "×"; if (_updating) return; _engine.ZoomFactor = (float)v; _settings.ZoomFactor = v; };
            _zoom.PreviewMouseLeftButtonUp += (s, e) => _settings.Save();
            _holdSlider.ValueChanged += (s, e) => { int v = (int)Math.Round(_holdSlider.Value); _holdValue.Text = v + " / sec"; if (_updating) return; _settings.HoldSpeed = v; _hooks.HoldClicksPerSecond = v; };
            _holdSlider.PreviewMouseLeftButtonUp += (s, e) => _settings.Save();
            _overlaySlider.ValueChanged += (s, e) => { int v = (int)(Math.Round(_overlaySlider.Value / 5) * 5); _overlayValue.Text = v + "%"; if (_updating) return; _settings.OverlayPercent = v; };
            _overlaySlider.PreviewMouseLeftButtonUp += (s, e) => { _settings.Save(); if (_overlayMode) Place(); };
            F<Button>("BtnZoomDefault").Click += (s, e) => { _sw[(int)CheatId.Zoom].IsChecked = false; };
            F<Button>("BtnFind").Click += (s, e) => { _sw[(int)CheatId.TrackGollum].IsChecked = true; _engine.RequestFindGollum(false); };
            F<Button>("BtnGoto").Click += (s, e) => _engine.RequestFindGollum(true);
            F<Button>("BtnUnlock").Click += (s, e) => { _engine.RequestUnlockPowers(); Cue(true); };
            F<Button>("BtnLevelAll").Click += (s, e) => { _engine.RequestLevelHeroes(false); Cue(true); };

            // ---- settings page ----
            _chkStartup.Click += (s, e) => { Settings.StartWithWindows = _chkStartup.IsChecked == true; _chkStartup.IsChecked = Settings.StartWithWindows; };
            _chkHotkeys.Click += (s, e) => { _settings.Hotkeys = _chkHotkeys.IsChecked == true; _settings.Save(); ApplyKeys(); };
            _chkSounds.Click += (s, e) => { _settings.Sounds = _chkSounds.IsChecked == true; _settings.Save(); };
            _chkTopmost.Click += (s, e) => { _settings.Topmost = _chkTopmost.IsChecked == true; if (!_overlayMode) _w.Topmost = _settings.Topmost; _settings.Save(); };
            F<Button>("BtnResetKeys").Click += (s, e) => { _settings.Keys = Settings.DefaultKeys(); _settings.Hotkeys = true; _chkHotkeys.IsChecked = true; _settings.Save(); ApplyKeys(); };
            BuildHotkeyPage();

            _statusCard.MouseLeftButtonUp += (s, e) => { if (_engine.Current.Connection == Connection.NeedsElevation) Elevate(false); };
            ApplyKeys();
            UpdateGameUi();
        }

        T F<T>(string name) where T : class { object o = _w.FindName(name); if (o == null) throw new InvalidOperationException("Missing UI element " + name); return (T)o; }

        public void Show()
        {
            new WindowInteropHelper(_w).EnsureHandle();   // creates the window handle (and starts the input hooks) without showing anything
            if (!_startHidden) ShowMenu();
        }

        // ------------------------------------------------------------------ game and patch selection
        static string Key(GameId g) { return g == GameId.Bfme2 ? "bfme2" : "rotwk"; }
        string PatchSetting(GameId g) { return g == GameId.Bfme2 ? _settings.PatchBfme2 : _settings.PatchRotwk; }

        readonly object _catalogLock = new object(); readonly InstalledGame[] _catalog = new InstalledGame[2]; readonly DateTime[] _catalogAt = new DateTime[2];
        /// <summary>Installed-game information, re-read at most every few seconds (it is asked for from two threads).</summary>
        InstalledGame Catalog(GameId g, bool fresh)
        {
            lock (_catalogLock)
            {
                int i = (int)g;
                if (fresh || _catalog[i] == null || (DateTime.UtcNow - _catalogAt[i]).TotalSeconds > 5) { _catalog[i] = GameCatalog.Detect(g); _catalogAt[i] = DateTime.UtcNow; }
                return _catalog[i];
            }
        }

        /// <summary>The data patch the trainer assumes for a game: the user's choice, otherwise what was detected.</summary>
        PatchEntry EffectivePatch(GameId g, string exePath)
        {
            string sel = PatchSetting(g);
            if (sel != "auto") { PatchEntry chosen = GameCatalog.Find(g, sel); if (chosen != null) return chosen; }
            InstalledGame ig = Catalog(g, false);
            if (!string.IsNullOrEmpty(exePath))
            {
                // a game started from some other folder (a mod with its own copy of the game, for example) is not the installation that was checked
                string dir = ""; try { dir = Path.GetDirectoryName(exePath) ?? ""; } catch { }
                if (dir.Length > 0 && (!ig.Installed || !string.Equals(dir.TrimEnd('\\'), ig.InstallPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    return new PatchEntry { Key = "other-install", Name = "Another installation (" + dir + ")", Status = PatchStatus.Untested, Note = "Not the registered game folder; mods and copies were never play-tested." };
            }
            return ig.Patch;
        }

        /// <summary>Called by the engine before it connects: null = go ahead, otherwise why cheats stay off.</summary>
        string PatchGate(GameProfile prof, string exePath)
        {
            PatchEntry e = EffectivePatch(prof.Game, exePath);
            if (e.Status == PatchStatus.Verified) return null;
            if (e.Status == PatchStatus.Untested && _settings.AllowUnverified) return null;
            return e.Name + " is " + (e.Status == PatchStatus.Untested ? "not verified" : "not supported") + ": " + e.Note + " Nothing was changed in the game. See the patch list at the top of the trainer.";
        }

        GameId DisplayGame()
        {
            if (_settings.Game == "rotwk") return GameId.Rotwk;
            if (_settings.Game == "bfme2") return GameId.Bfme2;
            EngineSnapshot s = _engine.Current; GameProfile p = s.Profile ?? s.SeenProfile;
            return p != null ? p.Game : (_settings.LastGame == "bfme2" ? GameId.Bfme2 : GameId.Rotwk);
        }

        /// <summary>Header texts and the two selector buttons for the game that is shown.</summary>
        void UpdateGameUi()
        {
            PatchEntry e = EffectivePatch(_theme, null); bool auto = _settings.Game == "auto";
            _titleText.Text = "LORD OF THE RINGS\nGOD MODE CHEAT";
            _subtitleText.Text = GameCatalog.GameName(_theme) + "  ·  " + e.Name + "  ·  offline skirmish only";
            _btnGame.Content = (auto ? "Auto:  " : "") + GameCatalog.ShortName(_theme) + "   ▾";
            bool ok = e.Status == PatchStatus.Verified, allowed = e.Status == PatchStatus.Untested && _settings.AllowUnverified;
            _btnPatch.Content = (ok ? "✔  " : "⚠  ") + (e.Name.Length > 26 ? e.Name.Substring(0, 25) + "…" : e.Name) + "   ▾";
            _btnPatch.Foreground = ok ? Green : allowed ? Amber : Red;
            _btnPatch.ToolTip = e.Name + " - " + e.StatusText + (allowed ? " (allowed by you)" : "") + ". " + e.Note;
            _shownPatchKey = e.Key;
            if (_tray != null) { try { _tray.Text = InstallInfo.AppName; } catch { } }
        }

        void ChooseGame(string key)
        {
            _gamePopup.IsOpen = false;
            _settings.Game = key; _settings.Save();
            _engine.OnlyGame = key == "rotwk" ? (int)GameId.Rotwk : key == "bfme2" ? (int)GameId.Bfme2 : -1;
            ApplyTheme(DisplayGame()); Cue(true);
        }

        void ChoosePatch(GameId g, string key)
        {
            _patchPopup.IsOpen = false;
            if (g == GameId.Bfme2) _settings.PatchBfme2 = key; else _settings.PatchRotwk = key;
            _settings.Save(); _engine.RequestReattach(); UpdateGameUi(); Cue(true);
        }

        /// <summary>One row of a drop-down list: a mark, a title and a small explanation.</summary>
        Border MenuRow(string title, string sub, bool selected, bool enabled, Brush subBrush, Action click)
        {
            Brush main = (Brush)_w.FindResource("TextMain"), accent = (Brush)_w.FindResource("GoldLight"), hover = (Brush)_w.FindResource("CardLine");
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = selected ? "●" : "○", Foreground = selected ? accent : Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, 0, 0) });
            var sp = new StackPanel(); Grid.SetColumn(sp, 1);
            sp.Children.Add(new TextBlock { Text = title, Foreground = selected ? accent : main, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(sub)) sp.Children.Add(new TextBlock { Text = sub, Foreground = subBrush ?? Dim, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
            grid.Children.Add(sp);
            var row = new Border { Child = grid, Padding = new Thickness(6, 5, 8, 5), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent, Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.55 };
            if (enabled)
            {
                row.MouseEnter += (s, e) => row.Background = hover; row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            }
            return row;
        }

        void OpenGamePopup()
        {
            ClosePopups();
            StackPanel p = F<StackPanel>("GamePopupPanel"); p.Children.Clear();
            EngineSnapshot s = _engine.Current; GameProfile live = s.Profile ?? s.SeenProfile;
            p.Children.Add(MenuRow("Auto-detect", "Follows whichever game is running" + (live != null ? "  ·  now: " + GameCatalog.ShortName(live.Game) : ""), _settings.Game == "auto", true, null, () => ChooseGame("auto")));
            foreach (GameId g in new[] { GameId.Rotwk, GameId.Bfme2 })
            {
                InstalledGame ig = Catalog(g, true); string key = Key(g); bool running = live != null && live.Game == g;
                string sub = ig.Installed ? ig.InstallPath + "  ·  " + ig.Patch.Name + (running ? "  ·  running" : "") : "Not found on this PC";
                p.Children.Add(MenuRow(ig.Name, sub, _settings.Game == key, true, running ? Green : null, () => ChooseGame(key)));
            }
            _gamePopup.PlacementTarget = _btnGame; _gamePopup.IsOpen = true;
        }

        void OpenPatchPopup()
        {
            ClosePopups();
            GameId g = _theme; InstalledGame ig = Catalog(g, true); string sel = PatchSetting(g);
            StackPanel p = F<StackPanel>("PatchPopupPanel"); p.Children.Clear();
            Brush main = (Brush)_w.FindResource("TextMain");
            p.Children.Add(new TextBlock { Text = GameCatalog.GameName(g).ToUpperInvariant() + "  -  PATCH COMPATIBILITY", Style = (Style)_w.FindResource("Section"), Margin = new Thickness(6, 3, 6, 4) });
            string found = ig.Installed ? "Found: " + ig.Patch.Name + " (from the " + ig.PatchSource + "), game engine " + ig.EngineLabel + (ig.EngineKnown ? "" : " - unknown build") : "The game was not found on this PC.";
            p.Children.Add(new TextBlock { Text = found, Foreground = main, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 5) });
            p.Children.Add(MenuRow("Auto-detect", "Use the patch that was found: " + ig.Patch.Name + "  -  " + ig.Patch.StatusText, sel == "auto", true, StatusBrush(ig.Patch.Status), () => ChoosePatch(g, "auto")));
            foreach (PatchEntry e in GameCatalog.Known(g))
            {
                string key = e.Key;
                p.Children.Add(MenuRow(e.Name, e.StatusText + "  -  " + e.Note, sel == key, e.Status != PatchStatus.Unsupported, StatusBrush(e.Status), () => ChoosePatch(g, key)));
            }
            var allow = new CheckBox { Style = (Style)_w.FindResource("Check"), IsChecked = _settings.AllowUnverified, Margin = new Thickness(6, 7, 6, 2), FontSize = 11.5 };
            allow.Content = new TextBlock { Text = "Allow cheats on NOT VERIFIED patches (same game engine only, at your own risk)", TextWrapping = TextWrapping.Wrap, Width = 410 };
            allow.Click += (s, e) => { _settings.AllowUnverified = allow.IsChecked == true; _settings.Save(); _engine.RequestReattach(); UpdateGameUi(); };
            p.Children.Add(allow);
            p.Children.Add(new TextBlock { Text = "The trainer always checks the running game's program code first. A game engine it does not know is never touched, whatever is chosen here.", Foreground = Dim, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 3) });
            _patchPopup.PlacementTarget = _btnPatch; _patchPopup.IsOpen = true;
        }

        Brush StatusBrush(PatchStatus s) { return s == PatchStatus.Verified ? Green : s == PatchStatus.Untested ? Amber : Red; }

        void ClosePopups() { try { _keyPopup.IsOpen = false; _gamePopup.IsOpen = false; _patchPopup.IsOpen = false; } catch { } }

        /// <summary>The game whose window the in-game menu and the hotkeys belong to: the connected one, otherwise a supported game that is merely running.</summary>
        int OverlayPid { get { int p = _engine.GamePid; return p != 0 ? p : _engine.SeenPid; } }

        // ------------------------------------------------------------------ showing the menu
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

        IntPtr GameWindow()
        {
            int pid = OverlayPid; if (pid == 0) return IntPtr.Zero; IntPtr best = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                int p; GetWindowThreadProcessId(h, out p);
                if (p == pid && IsWindowVisible(h)) { var cn = new StringBuilder(64); GetClassName(h, cn, 64); if (cn.ToString().StartsWith("E99E8455")) { best = h; return false; } }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        /// <summary>True when the game's window lies above the menu in the window order, i.e. the menu is open but hidden behind the game.</summary>
        bool CoveredByGame()
        {
            IntPtr game = GameWindow(); if (game == IntPtr.Zero || _hwnd == IntPtr.Zero) return false;
            IntPtr h = _hwnd; for (int i = 0; i < 400 && h != IntPtr.Zero; i++) { h = GetWindow(h, 3); if (h == game) return true; }   // 3 = the window above
            return false;
        }

        bool GameWindowRect(out RECT area)
        {
            area = new RECT(); IntPtr best = GameWindow();
            if (best == IntPtr.Zero || IsIconic(best)) return false;
            RECT c; POINT o = new POINT(); if (!GetClientRect(best, out c) || !ClientToScreen(best, ref o) || c.R < 320 || c.B < 240) return false;
            area.L = o.X; area.T = o.Y; area.R = o.X + c.R; area.B = o.Y + c.B; return true;
        }

        static double PrimaryMonitorScale()
        {
            try { uint x, y; if (GetDpiForMonitor(MonitorFromPoint(new POINT(), 1), 0, out x, out y) == 0 && x >= 96) return x / 96.0; } catch { }
            return 1.0;
        }

        /// <summary>Sizes and positions the window for the current mode. All arithmetic is in real pixels.</summary>
        void Place()
        {
            if (_placing) return; _placing = true;
            try
            {
                double designW = _root.Width + 24, designH = (_root.ActualHeight > 100 ? _root.ActualHeight : 640) + 24, dpi = _dpi0, s; RECT a;
                if (_overlayMode && GameWindowRect(out a))
                {
                    double gw = a.R - a.L, gh = a.B - a.T;
                    s = Math.Min(gw * _settings.OverlayPercent / 100.0 / (designW * dpi), gh * 0.97 / (designH * dpi));
                }
                else
                {
                    a = new RECT { L = 0, T = 0, R = GetSystemMetrics(0), B = GetSystemMetrics(1) };
                    s = Math.Min(PrimaryMonitorScale() / dpi, Math.Min((a.R - a.L) * 0.97 / (designW * dpi), (a.B - a.T) * 0.90 / (designH * dpi)));
                }
                s = Math.Max(0.2, Math.Min(4.0, s));
                if (Math.Abs(_scale.ScaleX - s) > 0.002) { _scale.ScaleX = _scale.ScaleY = s; _w.UpdateLayout(); designH = _root.ActualHeight + 24; }
                double wpx = designW * dpi * s, hpx = designH * dpi * s;
                _w.Left = (a.L + ((a.R - a.L) - wpx) / 2) / dpi; _w.Top = Math.Max(a.T, a.T + ((a.B - a.T) - hpx) / 2) / dpi;
            }
            finally { _placing = false; }
        }

        void SetNoActivate(bool on)
        {
            if (_hwnd == IntPtr.Zero) return; int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, on ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE);
        }

        /// <summary>Shows the menu the right way for the situation: over the game if the game is in front, otherwise as a desktop window.</summary>
        void ShowMenu()
        {
            if (_exiting) return;
            RECT a; bool overGame = _hooks.GameIsForeground() && GameWindowRect(out a);
            _overlayMode = overGame;
            SetNoActivate(overGame); _w.ShowActivated = !overGame; _w.Topmost = overGame || _settings.Topmost;
            Place();
            if (!_w.IsVisible) _w.Show();
            Place();
            if (overGame) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);   // keep above the game, never activate
            else { if (_w.WindowState == WindowState.Minimized) _w.WindowState = WindowState.Normal; _w.Activate(); Win32.SetForegroundWindow(_hwnd); }
            // A full-screen game that has only just come back to the front (from the task bar) is still taking over the screen:
            // a menu opened in that moment is in place but not drawn. It is opened once more when the game has settled.
            _reshowAt = overGame && (DateTime.UtcNow - _lastNotFront).TotalSeconds < 3.0 ? _lastNotFront.AddSeconds(3.5) : DateTime.MaxValue;
        }
        DateTime _lastNotFront = DateTime.UtcNow, _reshowAt = DateTime.MaxValue;

        void HideMenu()
        {
            ClosePopups(); EndSearch(); _engine.WantHeroList = false; bool wasDesktop = !_overlayMode && _w.IsVisible; _reshowAt = DateTime.MaxValue;
            _w.Hide();
            if (wasDesktop && !_settings.TrayTipShown)
            {
                _settings.TrayTipShown = true; _settings.Save();
                try { _tray.ShowBalloonTip(4000, "The trainer is still running", "Cheats stay active. In the game press F12 to open the menu; on the desktop click the trainer icon in the tray.", WinForms.ToolTipIcon.Info); } catch { }
            }
        }

        int _menuRetry, _coveredTicks;
        void ToggleMenu()
        {
            bool gameFront = _hooks.GameIsForeground(); RECT area;
            if (gameFront && !(_w.IsVisible && _overlayMode) && !GameWindowRect(out area))
            {
                // The game is in front but its window is not there yet (it is still coming back from the task bar). Opening the
                // desktop window now would take the screen away from the game again, so wait for the game and open over it.
                if (_menuRetry++ < 16)
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    t.Tick += (s, e) => { t.Stop(); if (_exiting) return; if (_hooks.GameIsForeground()) ToggleMenu(); else _menuRetry = 0; };
                    t.Start();
                }
                else _menuRetry = 0;
                return;
            }
            _menuRetry = 0;
            if (!_w.IsVisible) ShowMenu();
            else if (_overlayMode) HideMenu();
            else if (gameFront) { ClosePopups(); _w.Hide(); ShowMenu(); }   // it was open on the desktop behind the game: open it afresh over the game (only raising it does not get it above a full-screen game that was started first)
            else HideMenu();
        }

        FrameworkElement _pageFactions; Button _btnFactions; int _page;   // 0 = cheats, 1 = settings, 2 = all factions, 3 = my heroes
        void ShowPage(int page)
        {
            _page = page; ClosePopups();
            _pageCheats.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed; _pageSettings.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
            _pageFactions.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed; if (_pageHeroes != null) _pageHeroes.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
            _btnSettings.Content = page == 1 ? "◀  CHEATS" : "⚙  SETTINGS"; _btnFactions.Content = page == 2 ? "◀  CHEATS" : "⚔  ALL FACTIONS";
            if (page == 2) RefreshFactionPage(); else EndSearch();
            if (page == 3) RenderHeroes(_engine.Current); else _engine.WantHeroList = false;
            if (_w.IsVisible) { _w.UpdateLayout(); Place(); }
        }
        void ShowSettingsPage(bool settings) { ShowPage(settings ? 1 : 0); }

        // ------------------------------------------------------------------ switches
        void MakeCardClickable(string cardName, ToggleButton toggle)
        {
            // Clicking the empty part of a card flips its switch (a large, forgiving target); clicks on controls inside it are left alone.
            FrameworkElement card = F<FrameworkElement>(cardName); bool armed = false;
            card.PreviewMouseLeftButtonDown += (s, e) => { armed = !IsInsideControl(e.OriginalSource as DependencyObject, card); };
            card.MouseLeave += (s, e) => { armed = false; };
            card.PreviewMouseLeftButtonUp += (s, e) =>
            {
                bool go = armed && !IsInsideControl(e.OriginalSource as DependencyObject, card); armed = false;
                if (go && toggle.IsEnabled) toggle.IsChecked = toggle.IsChecked != true;
            };
        }

        static bool IsInsideControl(DependencyObject d, DependencyObject stopAt)
        {
            for (; d != null && d != stopAt; d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ButtonBase || d is RangeBase || d is Thumb || d is Track || d is Canvas) return true;
            return false;
        }

        void OnSwitch(CheatId id, bool on)
        {
            _engine.SetSwitch(id, on);
            if (_updating) return;
            Cue(on); SyncMaster();
        }

        void OnHold(bool on) { if (_updating) return; _settings.HoldToRecruit = on; _hooks.HoldEnabled = on; _settings.Save(); Cue(on); }


        void OnMaster(bool on)
        {
            if (_updating) return;
            _updating = true;
            try
            {
                foreach (CheatId id in MasterSet) _sw[(int)id].IsChecked = on;
                if (!on) for (int i = 0; i < _sw.Length; i++) _sw[i].IsChecked = false;   // OFF really means everything off
            }
            finally { _updating = false; }
            Cue(on);
        }

        void SyncMaster()
        {
            bool all = true; foreach (CheatId id in MasterSet) if (_sw[(int)id].IsChecked != true) all = false;
            _updating = true; try { _swMaster.IsChecked = all; } finally { _updating = false; }
        }

        void Toggle(CheatId id) { ToggleButton t = _sw[(int)id]; t.IsChecked = t.IsChecked != true; }

        void SetWeather(WeatherMode mode)
        {
            _engine.Weather = mode;
            _updating = true; try { for (int i = 0; i < _wxRadios.Length; i++) _wxRadios[i].IsChecked = (int)mode == i + 1; } finally { _updating = false; }
        }

        void OnHotAction(HotAction a)
        {
            if (_exiting) return;
            switch (a)
            {
                case HotAction.Menu: ToggleMenu(); break;
                case HotAction.Money: Toggle(CheatId.Money); break;
                case HotAction.Army: Toggle(CheatId.InstantArmy); break;
                case HotAction.Build: { bool on = _sw[(int)CheatId.InstantBuild].IsChecked != true; _sw[(int)CheatId.InstantBuild].IsChecked = on; _sw[(int)CheatId.InstantUpgrades].IsChecked = on; break; }
                case HotAction.CommandPoints: Toggle(CheatId.CommandPoints); break;
                case HotAction.Reveal: Toggle(CheatId.RevealMap); break;
                case HotAction.Zoom: Toggle(CheatId.Zoom); break;
                case HotAction.Gollum: Toggle(CheatId.TrackGollum); if (_sw[(int)CheatId.TrackGollum].IsChecked == true) _engine.RequestFindGollum(false); break;
                case HotAction.Powers: Toggle(CheatId.PowerPoints); if (_sw[(int)CheatId.PowerPoints].IsChecked == true) _engine.RequestUnlockPowers(); break;
                case HotAction.HeroLevel: _engine.RequestLevelHeroes(false); Cue(true); break;   // all heroes together (there is no "selected hero only" variant any more)
                case HotAction.Cooldowns: Toggle(CheatId.NoCooldowns); break;
                case HotAction.FreezeAI: Toggle(CheatId.FreezeAI); break;
                case HotAction.GodMode: { bool all = true; foreach (CheatId id in GodSet) if (_sw[(int)id].IsChecked != true) all = false; foreach (CheatId id in GodSet) _sw[(int)id].IsChecked = !all; break; }
                case HotAction.Weather: { int next = ((int)_engine.Weather + 1) % WeatherNames.Length; SetWeather((WeatherMode)next); Cue(next != 0); break; }
            }
        }

        void Cue(bool on)
        {
            if (!_settings.Sounds) return;
            ThreadPool.QueueUserWorkItem(_ => { try { if (on) { Win32.Beep(988, 70); Win32.Beep(1319, 110); } else { Win32.Beep(784, 70); Win32.Beep(523, 120); } } catch { } });
        }

        // ------------------------------------------------------------------ hotkey settings
        void BuildHotkeyPage()
        {
            UniformGrid grid = F<UniformGrid>("HotkeyGrid"); Style keyStyle = (Style)_w.FindResource("KeyButton"); Brush text = (Brush)_w.FindResource("TextMain");
            grid.Children.Clear();
            for (int i = 0; i < Settings.KeyCount; i++)
            {
                int action = i; var row = new Grid { Margin = new Thickness(0, 2, 14, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = ActionNames[i], Foreground = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var b = new Button { Style = keyStyle }; Grid.SetColumn(b, 1); row.Children.Add(b); _keyButtons[i] = b;
                b.Click += (s, e) => { bool reopen = !(_keyPopup.IsOpen && _popupAction == action); ClosePopups(); if (reopen) { _popupAction = action; _keyPopup.PlacementTarget = b; _keyPopup.IsOpen = true; } };
                grid.Children.Add(row);
            }
            WrapPanel panel = F<WrapPanel>("KeyPopupPanel"); panel.Children.Clear();
            var keys = new List<int>(); for (int k = 0; k < 12; k++) keys.Add(0x70 + k); for (int k = 0; k < 10; k++) keys.Add(0x60 + k); keys.Add(0);
            foreach (int key in keys)
            {
                int vk = key;
                var b = new Button { Style = keyStyle, Content = Settings.KeyName(vk), Margin = new Thickness(3), MinWidth = 62 };
                b.Click += (s, e) => { _keyPopup.IsOpen = false; AssignKey(_popupAction, vk); };
                panel.Children.Add(b);
            }
        }

        void AssignKey(int action, int vk)
        {
            if (action < 0) return;
            int old = _settings.Keys[action];
            if (vk != 0) for (int i = 0; i < Settings.KeyCount; i++) if (i != action && _settings.Keys[i] == vk) _settings.Keys[i] = old;   // swap, so every key has one job
            _settings.Keys[action] = vk; _settings.Save(); ApplyKeys();
        }

        void ApplyKeys()
        {
            _hooks.SetKeys(_settings.Keys); _hooks.HotkeysEnabled = _settings.Hotkeys;
            for (int i = 0; i < Settings.KeyCount; i++) if (_keyButtons[i] != null) _keyButtons[i].Content = Settings.KeyName(_settings.Keys[i]);
            for (int i = 0; i < BadgeNames.Length; i++)
            {
                int vk = _settings.Keys[(int)BadgeActions[i]]; bool show = _settings.Hotkeys && vk != 0;
                F<TextBlock>("Key" + BadgeNames[i]).Text = Settings.KeyName(vk); F<Border>("Key" + BadgeNames[i] + "Box").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            int menu = _settings.Keys[(int)HotAction.Menu];
            _hotkeyHint.Text = _settings.Hotkeys && menu != 0 ? Settings.KeyName(menu) + " shows / hides this menu in the game" : "Hotkeys are off - use the tray icon to open this menu";
            F<Button>("BtnHide").Content = _settings.Hotkeys && menu != 0 ? "HIDE  (" + Settings.KeyName(menu) + ")" : "HIDE";
        }

        // ------------------------------------------------------------------ rendering
        void RenderLatest() { Interlocked.Exchange(ref _renderQueued, 0); if (!_exiting) Render(_engine.Current); }

        void Render(EngineSnapshot s)
        {
            // the look follows the game: the one chosen by hand, otherwise the one that is running
            GameProfile seen = s.Profile ?? s.SeenProfile;
            if (seen != null && _settings.LastGame != Key(seen.Game) && _settings.Game == "auto") { _settings.LastGame = Key(seen.Game); _settings.Save(); }
            GameId want = DisplayGame();
            if (want != _theme) { ApplyTheme(want); return; }
            if (EffectivePatch(_theme, null).Key != _shownPatchKey) UpdateGameUi();

            Brush dot = s.Connection == Connection.InMatch ? Green : (s.Connection == Connection.InMenus || s.Connection == Connection.Starting) ? Amber : Red;
            _dot.Fill = dot; _statusText.Text = s.ConnectionText;
            _statusDetail.Text = s.Connection == Connection.NotRunning && s.SeenProfile == null && s.ConnectionText == "Game not running" ? "Start " + GameCatalog.GameName(_theme) + " and begin a skirmish." : s.Detail;
            _statusDetail.ToolTip = _statusDetail.Text;
            _statusCard.Cursor = s.Connection == Connection.NeedsElevation ? Cursors.Hand : Cursors.Arrow;
            bool inMatch = s.Connection == Connection.InMatch;
            RenderFactions(s); RenderHeroes(s);
            _statMoney.Text = inMatch ? "Resources  " + s.Game.Money.ToString("N0") : "";
            _statCp.Text = inMatch ? "Command points  " + s.Game.CpUsed.ToString("N0") + " / " + s.Game.CpLimit.ToString("N0") + (s.PowerPointsText.Length > 0 ? "   ·   " + s.PowerPointsText : "") : "";

            int on = 0; foreach (CheatId id in MasterSet) if (_sw[(int)id].IsChecked == true) on++;
            for (int i = 0; i < _st.Length; i++)
            {
                CheatStatus c = s.Cheats[i]; TextBlock t = _st[i]; bool god = i >= (int)CheatId.GodUnits;
                switch (c.State)
                {
                    case CheatState.Off: t.Text = _sw[i].IsChecked == true && god ? "Waiting" : "Off"; t.Foreground = _sw[i].IsChecked == true && god ? Amber : Dim; break;
                    case CheatState.Armed: t.Text = god ? "Waiting" : c.Text; t.Foreground = Amber; break;
                    case CheatState.Active: t.Text = god ? "Protected" : "Active  ·  " + c.Text; t.Foreground = Green; break;
                    case CheatState.Pending: t.Text = god ? "Starting" : c.Text; t.Foreground = Amber; break;
                    default: t.Text = god ? "Error" : c.Text; t.Foreground = Red; break;
                }
                if (god) t.ToolTip = c.Text.Length > 0 ? c.Text : null;
            }
            _stMaster.Text = on == 0 ? "All cheats on or off together" : on + " of " + MasterSet.Length + " cheats switched on";

            string hold = _hooks.HoldStatus;
            _stHold.Text = _swHold.IsChecked != true ? "Off" : hold.Length > 0 ? hold : "On  ·  hold the left mouse button on a unit icon";
            _stHold.Foreground = _swHold.IsChecked == true ? Green : Dim;
            _stHero.Text = inMatch ? (s.HeroText.Length > 0 ? s.HeroText : "All your heroes go to their highest level at once.") : "Start a skirmish first.";
            _stUnlock.Text = inMatch ? s.UnlockText : "";

            int wx = (int)_engine.Weather;
            _stWeather.Text = !inMatch ? (wx == 0 ? "Start a skirmish first" : WeatherNames[wx] + " - applies when a match is running") : s.WeatherText.Length > 0 ? s.WeatherText : "Map default";
            _stWeather.Foreground = inMatch && wx != 0 ? Green : Dim;

            GollumInfo g = s.Gollum;
            _stGollum.Text = inMatch ? g.Message : "Start a skirmish, then switch tracking on.";
            _stGollum.Foreground = g.State == GollumState.OnMap ? Green : g.State == GollumState.RingDropped ? Amber : Dim;
            bool have = inMatch && (g.State == GollumState.OnMap || g.State == GollumState.RingDropped) && g.MapWidth > 1 && g.MapHeight > 1;
            _mapDot.Visibility = _mapRing.Visibility = have ? Visibility.Visible : Visibility.Collapsed; _mapHint.Visibility = have ? Visibility.Collapsed : Visibility.Visible;
            if (have)
            {
                // fit the map rectangle into the canvas keeping its aspect ratio; world +Y is north (up)
                double cw = _map.Width, ch = _map.Height, scale = Math.Min(cw / g.MapWidth, ch / g.MapHeight), ox = (cw - g.MapWidth * scale) / 2, oy = (ch - g.MapHeight * scale) / 2;
                double x = ox + Math.Max(0, Math.Min(g.MapWidth, g.X)) * scale, y = oy + (g.MapHeight - Math.Max(0, Math.Min(g.MapHeight, g.Y))) * scale;
                Canvas.SetLeft(_mapDot, x - 5); Canvas.SetTop(_mapDot, y - 5); Canvas.SetLeft(_mapRing, x - 13); Canvas.SetTop(_mapRing, y - 13);
            }

            if (s.Events.Length > 0)
            {
                EngineEvent ev = s.Events[s.Events.Length - 1];
                if (ev.Serial != _lastEventSerial) { _lastEventSerial = ev.Serial; _lastEventLine = ev.When.ToString("HH:mm:ss") + "   " + ev.Text; _lastEventError = ev.IsError; _eventText.Text = _lastEventLine; _eventText.Foreground = ev.IsError ? Red : Dim; }
            }

            // the in-game menu belongs to the game: if the game is no longer in front, put the menu away
            bool gameFrontNow = _hooks.GameIsForeground(); if (!gameFrontNow) _lastNotFront = DateTime.UtcNow;
            if (_reshowAt != DateTime.MaxValue && DateTime.UtcNow >= _reshowAt)
            {
                _reshowAt = DateTime.MaxValue;
                if (_overlayMode && _w.IsVisible && gameFrontNow) { ClosePopups(); _w.Hide(); ShowMenu(); }
            }
            if (_overlayMode && _w.IsVisible && !_hooks.GameIsForeground() && GetForegroundWindow() != _hwnd) HideMenu();
            // ... and it has to be on top of the game: a full-screen game that takes the screen back just after the menu opened
            // (it was still returning from the task bar) ends up above it, so the menu is opened afresh
            if (_overlayMode && _w.IsVisible && _hooks.GameIsForeground() && CoveredByGame())
            {
                if (++_coveredTicks >= 2) { _coveredTicks = 0; ClosePopups(); _w.Hide(); ShowMenu(); }
            }
            else _coveredTicks = 0;

            if (s.Connection != _lastConn)
            {
                _lastConn = s.Connection;
                if (_tray != null) { string tip = "LotR God Mode Cheat - " + s.ConnectionText; _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; }
                // started with Windows without rights: elevate automatically the first time the game needs it
                if (s.Connection == Connection.NeedsElevation && _startHidden && !_elevationTried) Elevate(true);
            }
        }

        void Elevate(bool keepInTray)
        {
            if (Program.IsElevated) return;
            _elevationTried = true;
            if (Program.RelaunchElevated(keepInTray ? "--tray --relaunched" : "--relaunched")) Exit();
            else { _eventText.Text = "Administrator rights were not granted - the trainer cannot connect to the game without them."; _eventText.Foreground = Red; }
        }

        // ------------------------------------------------------------------ tray, window messages
        void CreateTray()
        {
            System.Drawing.Icon icon = null;
            try { using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WitchKingTrainer.app.ico")) icon = new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize); } catch { }
            _tray = new WinForms.NotifyIcon { Icon = icon ?? System.Drawing.SystemIcons.Application, Text = InstallInfo.AppName, Visible = true };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show trainer", null, (s, e) => ShowMenu());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("All cheats ON", null, (s, e) => _swMaster.IsChecked = true);
            menu.Items.Add("All cheats OFF", null, (s, e) => { if (_swMaster.IsChecked == true) _swMaster.IsChecked = false; else OnMaster(false); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Close trainer", null, (s, e) => Exit());
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) { if (_w.IsVisible && !_overlayMode && _w.IsActive) HideMenu(); else ShowMenu(); } };
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(_w).Handle; _src = HwndSource.FromHwnd(_hwnd);
            _src.AddHook(WndProc);
            try { _dpi0 = _src.CompositionTarget.TransformToDevice.M11; } catch { _dpi0 = 1.0; }
            _showMsg = Win32.RegisterWindowMessage(Program.ShowMessageName); _quitMsg = Win32.RegisterWindowMessage(InstallInfo.QuitMessageName);
            Win32.ChangeWindowMessageFilterEx(_hwnd, _showMsg, 1, IntPtr.Zero);   // let an unelevated second launch reach us
            Win32.ChangeWindowMessageFilterEx(_hwnd, _quitMsg, 1, IntPtr.Zero);   // ...and the installer / uninstaller
            _hooks.TrainerWindow = _hwnd; _hooks.Start();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE && _overlayMode) { handled = true; return (IntPtr)MA_NOACTIVATE; }   // clicking the in-game menu must never take focus from the game
            if (msg == 0x007E && _overlayMode && _w.IsVisible) _reshowAt = DateTime.UtcNow.AddMilliseconds(800);   // the screen mode changed under the open menu (the game went full screen): draw it afresh
            if (_showMsg != 0 && (uint)msg == _showMsg) { ShowMenu(); handled = true; }
            else if (_quitMsg != 0 && (uint)msg == _quitMsg) { handled = true; _w.Dispatcher.BeginInvoke(new Action(Exit)); }
            return IntPtr.Zero;
        }

        // ------------------------------------------------------------------ exit
        public void Exit()
        {
            if (_exiting) return; _exiting = true;
            try { _settings.Save(); } catch { }
            try { ClosePopups(); _w.Hide(); } catch { }
            try { _hooks.Dispose(); } catch { }
            try { _engine.Stop(); _engine.Dispose(); } catch { }     // switches everything off and restores the game's values
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            CheatEngine.Log("---- trainer closed ----");
            _app.Shutdown();
        }

        public void ReportCrash(Exception ex)
        {
            try { Directory.CreateDirectory(Settings.Folder); File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), DateTime.Now + "  " + ex + Environment.NewLine); } catch { }
            try { _eventText.Text = "Unexpected error: " + ex.Message; _eventText.Foreground = Red; } catch { }
        }
    }
}
