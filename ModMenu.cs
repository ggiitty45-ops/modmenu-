using System;
using System.Collections.Generic;
using Engine;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game
{
    // ------------------------------------------------------------------
    // Loader: only writes a line to the game log so you can confirm that
    // the mod was loaded (Settings > Compatibility > View Game Log).
    // ------------------------------------------------------------------
    public class ModMenuLoader : ModLoader
    {
        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("OnLoadingFinished", this, 1);
        }

        public override void OnLoadingFinished(List<Action> actions)
        {
            Log.Information("[ModMenu] loaded");
        }
    }

    // ------------------------------------------------------------------
    // Component attached to the Player entity (see Assets/ModMenu.xdb).
    // Adds a small "MOD" button to the player's HUD that opens the menu.
    // ------------------------------------------------------------------
    public class ComponentModMenu : Component, IUpdateable
    {
        ComponentPlayer m_player;
        BevelledButtonWidget m_launcher;

        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_player = Entity.FindComponent<ComponentPlayer>(true);
        }

        public void Update(float dt)
        {
            if (m_player == null) return;

            // The HUD is built by other components, so attach on the first
            // frame where it exists instead of inside Load().
            if (m_launcher == null)
            {
                ContainerWidget gui = m_player.GuiWidget;
                if (gui == null) return;

                m_launcher = new BevelledButtonWidget
                {
                    Text = "MOD",
                    Size = new Vector2(76f, 46f),
                    HorizontalAlignment = WidgetAlignment.Near,
                    VerticalAlignment = WidgetAlignment.Center,
                    Margin = new Vector2(6f, 0f)
                };
                gui.AddChildren(m_launcher);
            }

            if (m_launcher.IsClicked)
            {
                DialogsManager.ShowDialog(m_player.GuiWidget, new ModMenuDialog(m_player));
            }
        }
    }

    // ------------------------------------------------------------------
    // The menu window. Page 1 (for now): spawn items.
    // Uses only basic widgets (Canvas, StackPanel, Label, Button, BlockIcon).
    // ------------------------------------------------------------------
    public class ModMenuDialog : Dialog
    {
        class Entry
        {
            public int Value;
            public string Name;
            public string Lower;
        }

        const int PageSize = 5;
        static List<Entry> s_entries;

        readonly ComponentPlayer m_player;
        readonly List<Entry> m_filtered = new List<Entry>();
        readonly StackPanelWidget[] m_rows = new StackPanelWidget[PageSize];
        readonly BlockIconWidget[] m_icons = new BlockIconWidget[PageSize];
        readonly BevelledButtonWidget[] m_rowButtons = new BevelledButtonWidget[PageSize];
        readonly BevelledButtonWidget[] m_amountButtons;
        readonly int[] m_amounts = { 1, 10, 40 };
        readonly LabelWidget m_selectedLabel;
        readonly LabelWidget m_statusLabel;
        readonly LabelWidget m_pageLabel;
        readonly BevelledButtonWidget m_searchButton;
        readonly BevelledButtonWidget m_closeButton;
        readonly BevelledButtonWidget m_giveButton;
        readonly BevelledButtonWidget m_prevButton;
        readonly BevelledButtonWidget m_nextButton;

        int m_amountIndex = 1;
        int m_page;
        string m_filter = "";
        Entry m_selected;

        static Widget Spacer()
        {
            return new CanvasWidget { Size = new Vector2(float.PositiveInfinity, 0f) };
        }

        public ModMenuDialog(ComponentPlayer player)
        {
            m_player = player;
            EnsureEntries();

            Color accent = new Color(86, 156, 255);
            Color panelFill = new Color(14, 18, 30, 242);
            Color dim = new Color(160, 170, 190);

            CanvasWidget panel = new CanvasWidget
            {
                Size = new Vector2(640f, 400f),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Center
            };
            panel.AddChildren(new RectangleWidget { FillColor = panelFill, OutlineColor = accent });

            StackPanelWidget content = new StackPanelWidget
            {
                Direction = LayoutDirection.Vertical,
                Margin = new Vector2(14f, 10f)
            };

            // ---- header ----
            StackPanelWidget header = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            header.AddChildren(new LabelWidget
            {
                Text = "MOD MENU - Spawn Items",
                Color = accent,
                VerticalAlignment = WidgetAlignment.Center
            });
            header.AddChildren(Spacer());
            m_searchButton = new BevelledButtonWidget { Text = "Search", Size = new Vector2(110f, 40f), Margin = new Vector2(3f, 0f) };
            m_closeButton = new BevelledButtonWidget { Text = "Close", Size = new Vector2(100f, 40f), Margin = new Vector2(3f, 0f) };
            header.AddChildren(m_searchButton);
            header.AddChildren(m_closeButton);
            content.AddChildren(header);

            // ---- item rows (paged) ----
            for (int i = 0; i < PageSize; i++)
            {
                m_icons[i] = new BlockIconWidget
                {
                    Size = new Vector2(36f, 36f),
                    VerticalAlignment = WidgetAlignment.Center,
                    Margin = new Vector2(4f, 0f)
                };
                m_rowButtons[i] = new BevelledButtonWidget { Size = new Vector2(540f, 40f), Margin = new Vector2(2f, 2f) };
                m_rows[i] = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
                m_rows[i].AddChildren(m_icons[i]);
                m_rows[i].AddChildren(m_rowButtons[i]);
                content.AddChildren(m_rows[i]);
            }

            // ---- pager ----
            StackPanelWidget pager = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            m_prevButton = new BevelledButtonWidget { Text = "<", Size = new Vector2(70f, 38f), Margin = new Vector2(3f, 0f) };
            m_nextButton = new BevelledButtonWidget { Text = ">", Size = new Vector2(70f, 38f), Margin = new Vector2(3f, 0f) };
            m_pageLabel = new LabelWidget { Color = dim, VerticalAlignment = WidgetAlignment.Center, Margin = new Vector2(10f, 0f) };
            pager.AddChildren(m_prevButton);
            pager.AddChildren(m_nextButton);
            pager.AddChildren(m_pageLabel);
            content.AddChildren(pager);

            // ---- footer ----
            StackPanelWidget footer = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            m_selectedLabel = new LabelWidget
            {
                Text = "Tap an item",
                Color = dim,
                VerticalAlignment = WidgetAlignment.Center
            };
            footer.AddChildren(m_selectedLabel);
            footer.AddChildren(Spacer());
            m_amountButtons = new BevelledButtonWidget[m_amounts.Length];
            for (int i = 0; i < m_amounts.Length; i++)
            {
                m_amountButtons[i] = new BevelledButtonWidget { Size = new Vector2(70f, 40f), Margin = new Vector2(3f, 0f) };
                footer.AddChildren(m_amountButtons[i]);
            }
            m_giveButton = new BevelledButtonWidget { Text = "GIVE", Size = new Vector2(96f, 40f), Margin = new Vector2(6f, 0f) };
            footer.AddChildren(m_giveButton);
            content.AddChildren(footer);

            m_statusLabel = new LabelWidget { Color = dim, FontScale = 0.7f };
            content.AddChildren(m_statusLabel);

            panel.AddChildren(content);
            AddChildren(panel);

            RefreshAmountButtons();
            ApplyFilter();
        }

        void EnsureEntries()
        {
            if (s_entries != null) return;

            SubsystemTerrain terrain = m_player.Project.FindSubsystem<SubsystemTerrain>(true);
            HashSet<int> seen = new HashSet<int>();
            List<Entry> list = new List<Entry>();

            foreach (Block block in BlocksManager.Blocks)
            {
                if (block == null) continue;
                IEnumerable<int> values = block.GetCreativeValues();
                if (values == null) continue;

                foreach (int value in values)
                {
                    if (!seen.Add(value)) continue;
                    string name = block.GetDisplayName(terrain, value);
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add(new Entry { Value = value, Name = name, Lower = name.ToLowerInvariant() });
                }
            }

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            s_entries = list;
        }

        int PageCount()
        {
            return Math.Max(1, (m_filtered.Count + PageSize - 1) / PageSize);
        }

        void ApplyFilter()
        {
            m_filtered.Clear();
            foreach (Entry entry in s_entries)
            {
                if (m_filter.Length == 0 || entry.Lower.Contains(m_filter))
                    m_filtered.Add(entry);
            }
            m_page = 0;
            RefreshPage();
        }

        void RefreshPage()
        {
            for (int i = 0; i < PageSize; i++)
            {
                int index = m_page * PageSize + i;
                if (index < m_filtered.Count)
                {
                    Entry entry = m_filtered[index];
                    m_rows[i].IsVisible = true;
                    m_icons[i].Value = entry.Value;
                    m_rowButtons[i].Text = (entry == m_selected ? "> " : "") + entry.Name;
                }
                else
                {
                    m_rows[i].IsVisible = false;
                }
            }
            m_pageLabel.Text = "Page " + (m_page + 1) + " / " + PageCount();
            string filterText = m_filter.Length == 0 ? "" : "  |  filter: " + m_filter;
            m_statusLabel.Text = m_filtered.Count + " items" + filterText;
        }

        void RefreshAmountButtons()
        {
            for (int i = 0; i < m_amountButtons.Length; i++)
            {
                string text = "x" + m_amounts[i];
                m_amountButtons[i].Text = i == m_amountIndex ? "[" + text + "]" : text;
            }
        }

        void Give()
        {
            if (m_selected == null)
            {
                m_player.ComponentGui.DisplaySmallMessage("Pick an item first", Color.White, false, false);
                return;
            }

            int amount = m_amounts[m_amountIndex];
            IInventory inventory = m_player.ComponentMiner.Inventory;
            ComponentInventoryBase.AcquireItems(inventory, m_selected.Value, amount);
            m_player.ComponentGui.DisplaySmallMessage("+" + amount + "  " + m_selected.Name, Color.White, false, false);
        }

        public override void Update()
        {
            if (m_closeButton.IsClicked || Input.Cancel)
            {
                DialogsManager.HideDialog(this);
                return;
            }

            if (m_searchButton.IsClicked)
            {
                DialogsManager.ShowDialog(m_player.GuiWidget, new TextBoxDialog("Search item", m_filter, 30, text =>
                {
                    m_filter = (text ?? "").Trim().ToLowerInvariant();
                    ApplyFilter();
                }));
            }

            if (m_prevButton.IsClicked && m_page > 0)
            {
                m_page--;
                RefreshPage();
            }
            if (m_nextButton.IsClicked && m_page < PageCount() - 1)
            {
                m_page++;
                RefreshPage();
            }

            for (int i = 0; i < PageSize; i++)
            {
                if (m_rowButtons[i].IsClicked)
                {
                    int index = m_page * PageSize + i;
                    if (index < m_filtered.Count)
                    {
                        m_selected = m_filtered[index];
                        m_selectedLabel.Text = m_selected.Name;
                        m_selectedLabel.Color = Color.White;
                        RefreshPage();
                    }
                }
            }

            for (int i = 0; i < m_amountButtons.Length; i++)
            {
                if (m_amountButtons[i].IsClicked)
                {
                    m_amountIndex = i;
                    RefreshAmountButtons();
                }
            }

            if (m_giveButton.IsClicked)
                Give();
        }
    }
}
