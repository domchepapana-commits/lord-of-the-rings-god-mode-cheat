using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WitchKingTrainer.Core;

namespace WitchKingTrainer
{
    // The "My Heroes" page (24 Nov 2026): every hero the player has on the map, also those the game's own hero bar has no
    // room for (it shows 16). A click asks the engine to select that hero in the game and move the camera to him.
    public sealed partial class MainWindow
    {
        FrameworkElement _pageHeroes; WrapPanel _heroItems; TextBlock _heroListInfo, _stHeroList; string _heroTilesFor = "";
        readonly Dictionary<uint, TextBlock> _heroSub = new Dictionary<uint, TextBlock>();

        /// <summary>Wires the page (runs again after every theme change, like BindUi).</summary>
        void BindHeroes()
        {
            _pageHeroes = F<FrameworkElement>("PageHeroes"); _heroItems = F<WrapPanel>("HeroItems"); _heroListInfo = F<TextBlock>("HeroListInfo"); _stHeroList = F<TextBlock>("StHeroList");
            _heroTilesFor = ""; _heroSub.Clear();
            F<Button>("BtnMyHeroes").Click += (s, e) => ShowPage(3);
            F<Button>("BtnHeroesBack").Click += (s, e) => ShowPage(0);
            F<Button>("BtnHeroesLevel").Click += (s, e) => { _engine.RequestLevelHeroes(false); Cue(true); };
        }

        sealed class HeroRow { public HeroInfo Hero; public string Name, Faction; public int Order; }

        /// <summary>Catalog name and faction of a hero (the lists know "Aragorn"; the game's own name would be "GondorAragornMP").</summary>
        List<HeroRow> HeroRows(HeroInfo[] heroes)
        {
            FactionCatalog c = Cat; var rows = new List<HeroRow>();
            foreach (HeroInfo h in heroes)
            {
                var r = new HeroRow { Hero = h, Name = h.Name, Faction = "", Order = 999 };
                for (int f = 0; f < c.Factions.Count && r.Order == 999; f++)
                    foreach (CatalogItem i in c.Factions[f].Items)
                        if (i.IsHero && string.Equals(i.Object, h.Template, StringComparison.OrdinalIgnoreCase)) { r.Name = i.Name; r.Faction = c.Factions[f].Name; r.Order = f; break; }
                rows.Add(r);
            }
            rows.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Name != b.Name ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) : a.Hero.Id.CompareTo(b.Hero.Id));
            return rows;
        }

        static string HeroSubText(HeroRow r)
        {
            HeroInfo h = r.Hero; string hp = h.MaxHealth > 0 ? "  ·  " + (int)Math.Round(100f * h.Health / h.MaxHealth) + "% health" : "";
            if (h.Selected) return "▶ selected  ·  level " + h.Level;                         // short enough for the tile
            return (r.Faction.Length > 0 ? r.Faction + "  ·  " : "") + "level " + h.Level + hp;
        }

        void RenderHeroes(EngineSnapshot s)
        {
            if (_heroItems == null) return;
            // the engine reads the hero list only while this page is really on screen
            _engine.WantHeroList = _page == 3 && _w.IsVisible && !_exiting;
            if (_page != 3) return;
            bool inMatch = s.Connection == Connection.InMatch; HeroInfo[] heroes = inMatch ? (s.Heroes ?? new HeroInfo[0]) : new HeroInfo[0];
            _heroListInfo.Text = !inMatch ? "Start a skirmish first - then your heroes are listed here."
                               : heroes.Length == 0 ? "You have no heroes on the map right now."
                               : heroes.Length + (heroes.Length == 1 ? " hero" : " heroes") + " on the map." + (heroes.Length > 16 ? "  The game's own hero bar has room for 16 - here are all of them." : "");
            _stHeroList.Text = inMatch ? s.HeroListText : "";

            List<HeroRow> rows = HeroRows(heroes);
            var key = new System.Text.StringBuilder(_theme.ToString()); foreach (HeroRow r in rows) key.Append('|').Append(r.Hero.Id);
            if (key.ToString() != _heroTilesFor)
            {
                // the set of heroes changed: build the tiles again (never while it is unchanged, so a click is not lost)
                _heroTilesFor = key.ToString(); _heroItems.Children.Clear(); _heroSub.Clear();
                Style tile = (Style)_w.FindResource("Tile"); Brush main = (Brush)_w.FindResource("TextMain");
                foreach (HeroRow r in rows)
                {
                    uint id = r.Hero.Id; string name = r.Name;
                    var sp = new StackPanel { Width = 158 };
                    sp.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold, Foreground = main, TextTrimming = TextTrimming.CharacterEllipsis });
                    var sub = new TextBlock { Text = HeroSubText(r), FontSize = 10.5, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
                    sp.Children.Add(sub); _heroSub[id] = sub;
                    var b = new Button { Style = tile, Width = 176, Content = sp, ToolTip = "Select " + r.Name + " in the game and move the camera there" };   // six in a row: 36 heroes fit without scrolling
                    b.Click += (o, e) => { _engine.RequestHeroFocus(id, name); Cue(true); if (_overlayMode) HideMenu(); };   // over the game the menu gets out of the way; on the desktop it stays
                    _heroItems.Children.Add(b);
                }
            }
            else foreach (HeroRow r in rows) { TextBlock sub; if (_heroSub.TryGetValue(r.Hero.Id, out sub)) { string t = HeroSubText(r); if (sub.Text != t) sub.Text = t; } }
        }
    }
}
