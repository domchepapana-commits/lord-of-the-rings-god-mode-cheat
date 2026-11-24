using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WitchKingTrainer.Core;

namespace WitchKingTrainer
{
    // The "All Factions" page: heroes, units and buildings of every faction, change faction, every faction's powers.
    // NOT YET GAMEPLAY TESTED (version 4.0.0): the page itself only sends requests to the engine (CheatEngine.Factions.cs).
    public sealed partial class MainWindow
    {
        WrapPanel _facList, _facCats, _facItems; TextBlock _facNow, _stFaction, _stSpawn, _facSearchText; Border _facSearchBox;
        Button _btnFacPowers, _btnFacChange, _btnFacRestore, _btnHeroesFaction, _btnHeroesAll; CheckBox _chkFacBuilders, _chkFacFortress;
        string _facKey = "", _facCat = "Hero", _search = ""; bool _searching; string _facBuiltFor = "", _facAutoPicked = "";
        const int MaxTiles = 160;

        FactionCatalog Cat { get { return FactionCatalog.For(_theme); } }
        CatalogFaction Fac { get { FactionCatalog c = Cat; return c.Find(_facKey) ?? (c.Factions.Count > 0 ? c.Factions[0] : null); } }

        /// <summary>Wires the page (runs again after every theme change, like BindUi).</summary>
        void BindFactions()
        {
            _facList = F<WrapPanel>("FacList"); _facCats = F<WrapPanel>("FacCats"); _facItems = F<WrapPanel>("FacItems");
            _facNow = F<TextBlock>("FacNow"); _stFaction = F<TextBlock>("StFaction"); _stSpawn = F<TextBlock>("StSpawn");
            _facSearchText = F<TextBlock>("FacSearchText"); _facSearchBox = F<Border>("FacSearchBox");
            _btnFacPowers = F<Button>("BtnFacPowers"); _btnFacChange = F<Button>("BtnFacChange"); _btnFacRestore = F<Button>("BtnFacRestore");
            _btnHeroesFaction = F<Button>("BtnHeroesFaction"); _btnHeroesAll = F<Button>("BtnHeroesAll");
            _chkFacBuilders = F<CheckBox>("ChkFacBuilders"); _chkFacFortress = F<CheckBox>("ChkFacFortress");
            _facBuiltFor = ""; _searching = false; _hooks.TypingActive = false;

            _facSearchBox.MouseLeftButtonDown += (s, e) => { if (_searching) EndSearch(); else BeginSearch(); e.Handled = true; };
            _btnFacPowers.Click += (s, e) => { CatalogFaction f = Fac; if (f == null || !f.HasPowers) return; _engine.RequestFaction(f.Side, f.Name, f.SpellBook, false, null); Cue(true); };
            _btnFacChange.Click += (s, e) =>
            {
                CatalogFaction f = Fac; if (f == null || !f.HasPowers) return; var extras = new List<SpawnOrder>();
                if (_chkFacFortress.IsChecked == true && f.Fortress.Length > 0) extras.Add(new SpawnOrder { Object = f.Fortress, Name = f.Name + " fortress", Structure = true });
                if (_chkFacBuilders.IsChecked == true && f.Builder.Length > 0) for (int i = 0; i < 2; i++) extras.Add(new SpawnOrder { Object = f.Builder, Name = f.Name + " builder" });
                _engine.RequestFaction(f.Side, f.Name, f.SpellBook, true, extras); Cue(true);
            };
            _btnFacRestore.Click += (s, e) => { _engine.RequestRestoreFaction(); Cue(false); };
            _btnHeroesFaction.Click += (s, e) => { CatalogFaction f = Fac; if (f == null) return; RecruitHeroes(new[] { f }, f.Name + " heroes"); };
            _btnHeroesAll.Click += (s, e) => RecruitHeroes(Cat.Factions, "heroes of every faction");
            if (_page == 2) RefreshFactionPage();
        }

        void RecruitHeroes(IEnumerable<CatalogFaction> factions, string title)
        {
            var orders = new List<SpawnOrder>();
            foreach (CatalogFaction f in factions) foreach (CatalogItem i in f.Items) if (i.Category == "Hero") orders.Add(Order(i));
            if (orders.Count == 0) return;
            _engine.RequestSpawn(orders, title); Cue(true);
        }

        static SpawnOrder Order(CatalogItem i) { return new SpawnOrder { Object = i.Object, Name = i.Name, Hero = i.IsHero, Structure = i.IsStructure }; }

        /// <summary>Builds the faction buttons for the game that is shown (once per game / theme) and fills the list.</summary>
        void RefreshFactionPage()
        {
            if (_facList == null) return;
            FactionCatalog c = Cat; string builtFor = _theme + "|" + c.Factions.Count;
            if (_facBuiltFor != builtFor)
            {
                _facBuiltFor = builtFor; _facList.Children.Clear();
                if (c.Find(_facKey) == null) _facKey = c.Factions.Count > 0 ? c.Factions[0].Key : "";
                Style pill = (Style)_w.FindResource("Pill");
                foreach (CatalogFaction f in c.Factions)
                {
                    string key = f.Key;
                    var rb = new RadioButton { Style = pill, GroupName = "Fac", Content = f.Name, MinWidth = 92, Height = 36, FontSize = 13.5, Margin = new Thickness(0, 0, 6, 6), IsChecked = key == _facKey, ToolTip = f.Items.Count + " heroes, units and buildings" };
                    rb.Checked += (s, e) => { if (_facKey == key && _search.Length == 0) return; _facKey = key; _search = ""; ShowSearch(); BuildCategories(); BuildTiles(); };
                    _facList.Children.Add(rb);
                }
                if (c.Factions.Count == 0) _facList.Children.Add(new TextBlock { Text = "No list for this game is included in this build.", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap });
                BuildCategories();
            }
            BuildTiles();
        }

        void SelectFaction(string key)
        {
            if (_facList == null || Cat.Find(key) == null) return; _facKey = key;
            foreach (object o in _facList.Children) { var rb = o as RadioButton; if (rb != null) rb.IsChecked = string.Equals((string)rb.Content, Cat.Find(key).Name); }
            BuildCategories(); BuildTiles();
        }

        void BuildCategories()
        {
            _facCats.Children.Clear(); CatalogFaction f = Fac; if (f == null) return;
            var present = new List<string>();
            foreach (string cat in FactionCatalog.CategoryOrder) if (f.Items.Exists(i => i.Category == cat)) present.Add(cat);
            present.Add("All");
            if (!present.Contains(_facCat)) _facCat = present[0];
            Style pill = (Style)_w.FindResource("Pill");
            foreach (string cat in present)
            {
                string c = cat; int n = cat == "All" ? f.Items.Count : f.Items.FindAll(i => i.Category == c).Count;
                var rb = new RadioButton { Style = pill, GroupName = "FacCat", Content = (cat == "All" ? "All" : FactionCatalog.CategoryTitle(cat)) + "  " + n, IsChecked = cat == _facCat, Margin = new Thickness(0, 0, 5, 4), FontSize = 12 };
                rb.Checked += (s, e) => { _facCat = c; if (_search.Length > 0) { _search = ""; ShowSearch(); } BuildTiles(); };
                _facCats.Children.Add(rb);
            }
        }

        void BuildTiles()
        {
            _facItems.Children.Clear(); FactionCatalog c = Cat; CatalogFaction f = Fac; if (f == null) return;
            var items = new List<CatalogItem>(); bool searching = _search.Length > 0;
            if (searching)
            {
                foreach (CatalogFaction ff in c.Factions) foreach (CatalogItem i in ff.Items)
                    if (i.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 || i.Object.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0) items.Add(i);
            }
            else items = _facCat == "All" ? new List<CatalogItem>(f.Items) : f.Items.FindAll(i => i.Category == _facCat);

            Style tile = (Style)_w.FindResource("Tile"); Brush main = (Brush)_w.FindResource("TextMain"); int shown = 0;
            foreach (CatalogItem item in items)
            {
                if (shown++ >= MaxTiles) break;
                CatalogItem it = item; CatalogFaction owner = c.Find(it.Faction);
                string sub = (searching || _facCat == "All" ? (searching && owner != null ? owner.Name + "  ·  " : "") + FactionCatalog.CategoryTitle(it.Category) : it.IsStructure ? "Building" : it.IsHero ? "Hero" : "Unit")
                             + (it.CommandPoints > 0 ? "  ·  " + it.CommandPoints + " CP" : "");
                var sp = new StackPanel { Width = 164 };
                sp.Children.Add(new TextBlock { Text = it.Name, FontWeight = FontWeights.SemiBold, Foreground = main, TextTrimming = TextTrimming.CharacterEllipsis });
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                var b = new Button { Style = tile, Content = sp, ToolTip = (it.IsStructure ? "Build " : "Recruit ") + it.Name + " where the camera is looking   (" + it.Object + ")" };
                b.Click += (s, e) => { _engine.RequestSpawn(new[] { Order(it) }, it.Name); Cue(true); };
                _facItems.Children.Add(b);
            }
            if (items.Count == 0) _facItems.Children.Add(new TextBlock { Text = searching ? "Nothing matches \"" + _search + "\"." : "Nothing in this category.", Foreground = Dim, FontSize = 12.5, Margin = new Thickness(2, 8, 0, 0) });
            else if (items.Count > MaxTiles) _facItems.Children.Add(new TextBlock { Text = "… " + (items.Count - MaxTiles) + " more - type more letters to narrow the search.", Foreground = Dim, FontSize = 12, Margin = new Thickness(2, 8, 0, 8), Width = 700 });
            _btnHeroesFaction.Content = "RECRUIT ALL " + f.Name.ToUpperInvariant() + " HEROES";
        }

        // ---- search field (fed by the keyboard hook, because the in-game menu never takes the keyboard from the game) ----
        void BeginSearch() { _searching = true; _hooks.TypingActive = true; ShowSearch(); }
        void EndSearch() { if (!_searching && !_hooks.TypingActive) return; _searching = false; _hooks.TypingActive = false; ShowSearch(); }
        void ShowSearch()
        {
            if (_facSearchText == null) return;
            _facSearchText.Text = _searching ? "🔍  " + _search + "▏" : _search.Length > 0 ? "🔍  " + _search + "    (Esc clears)" : "🔍  Search all factions";
            _facSearchText.Foreground = _searching || _search.Length > 0 ? (Brush)_w.FindResource("TextMain") : Dim;
            _facSearchBox.BorderBrush = (Brush)_w.FindResource(_searching ? "Gold" : "CardLine"); _facSearchBox.BorderThickness = new Thickness(_searching ? 2 : 1);
        }
        void OnSearchKey(int vk)
        {
            if (_exiting || !_searching || _page != 2 || !_w.IsVisible) { _hooks.TypingActive = false; return; }
            if (vk == 0x1B) { _search = ""; EndSearch(); BuildTiles(); return; }                    // Esc
            if (vk == 0x0D) { EndSearch(); return; }                                                // Enter: keep the filter, give the keys back to the game
            if (vk == 0x08) { if (_search.Length > 0) _search = _search.Substring(0, _search.Length - 1); }
            else if (_search.Length < 24) _search += vk == 0x20 ? " " : vk == 0xBD ? "-" : ((char)vk).ToString().ToLowerInvariant();
            _search = _search.TrimStart(); ShowSearch(); BuildTiles();
        }

        void RenderFactions(EngineSnapshot s)
        {
            if (_facNow == null) return; bool inMatch = s.Connection == Connection.InMatch; FactionCatalog c = Cat;
            CatalogFaction now = c.BySide(s.PlayerTemplateName), own = c.BySide(s.OwnTemplateName), sel = Fac;
            // the first time a match is seen, the list opens on the faction that is being played
            if (inMatch && own != null && _facAutoPicked != s.OwnTemplateName + "|" + _theme) { _facAutoPicked = s.OwnTemplateName + "|" + _theme; if (_page == 2 && _search.Length == 0) SelectFaction(own.Key); else _facKey = own.Key; }
            if (!inMatch) { _facAutoPicked = ""; _facNow.Text = "Start a skirmish first - then everything here can be used."; }
            else if (s.OwnTemplateName.Length == 0) _facNow.Text = "In a match.";
            else _facNow.Text = "Your faction:  " + (own != null ? own.Name : s.OwnTemplateName) + (s.FactionSwitched ? "\nPowers in use:  " + (now != null ? now.Name : s.PlayerTemplateName) : "");
            _btnFacPowers.IsEnabled = _btnFacChange.IsEnabled = inMatch && sel != null && sel.HasPowers;
            _btnHeroesFaction.IsEnabled = inMatch && sel != null && sel.Heroes.Count > 0; _btnHeroesAll.IsEnabled = inMatch && c.Factions.Count > 0;
            _btnFacRestore.IsEnabled = inMatch && s.FactionSwitched; _facItems.IsEnabled = inMatch;
            _stFaction.Text = s.FactionText; _stSpawn.Text = s.SpawnText;
        }
    }
}
