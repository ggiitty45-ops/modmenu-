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
    // The menu window. Tab 1 (for now): spawn items.
    // To add more features later, add buttons in the header/footer and
    // handle them in Update().
    // ------------------------------------------------------------------
    public class ModMenuDialog : Dialog
    {
        class Entry
        {
            public int Value;
            public string Name;
            public string Lower;
        }

        static List<Entry> s_entries;

        readonly ComponentPlayer m_player;
        readonly ListPanelWidget m_list;
        readonly LabelWidget m_selectedLabel;
        readonly LabelWidget m_countLabel;
        readonly BevelledButtonWidget m_searchButton;
        readonly BevelledButtonWidget m_closeButton;
        readonly BevelledButtonWidget m_giveButton;
        readonly BevelledButtonWidget[] m_amountButtons;
        readonly int[] m_amounts = { 1, 10, 40 };

        int m_amountIndex = 1;
        string m_filter = "";
        Entry m_selected;

        public ModMenuDialog(ComponentPlayer player)
        {
            m_player = player;
            EnsureEntries();

            // ---- palette (dark, flat, blue accent) ----
            Color accent = new Color(86, 156, 255);
            Color panelFill = new Color(14, 18, 30, 242);
            Color dim = new Color(160, 170, 190);

            // ---- panel ----
            CanvasWidget panel = new CanvasWidget
            {
                Size = new Vector2(660f, 420f),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Center
            };
            panel.AddChildren(new RectangleWidget { FillColor = panelFill, OutlineColor = accent });

            StackPanelWidget content = new StackPanelWidget
            {
                Direction = LayoutDirection.Vertical,
                Size = new Vector2(float.PositiveInfinity, float.PositiveInfinity),
                Margin = new Vector2(16f, 12f)
            };

            // ---- header: title + search + close ----
            StackPanelWidget header = new StackPanelWidget
            {
                Direction = LayoutDirection.Horizontal,
                Size = new Vector2(float.PositiveInfinity, 50f)
            };
            header.AddChildren(new LabelWidget
            {
                Text = "MOD MENU  -  Spawn Items",
                Color = accent,
                VerticalAlignment = WidgetAlignment.Center
            });
            header.AddChildren(new UniformSpacerWidget());
            m_searchButton = new BevelledButtonWidget { Text = "Search", Size = new Vector2(110f, 44f), Margin = new Vector2(4f, 0f) };
            m_closeButton = new BevelledButtonWidget { Text = "Close", Size = new Vector2(100f, 44f), Margin = new Vector2(4f, 0f) };
            header.AddChildren(m_searchButton);
            header.AddChildren(m_closeButton);
            content.AddChildren(header);

            // ---- item list ----
            m_list = new ListPanelWidget
            {
                Direction = LayoutDirection.Vertical,
                ItemSize = 52f,
                Size = new Vector2(float.PositiveInfinity, 250f)
            };
            m_list.ItemWidgetFactory = BuildRow;
            m_list.ItemClicked += item => Select(item as Entry);
            content.AddChildren(m_list);

            // ---- footer: selected item, amount, give ----
            StackPanelWidget footer = new StackPanelWidget
            {
                Direction = LayoutDirection.Horizontal,
                Size = new Vector2(float.PositiveInfinity, 54f)
            };
            m_selectedLabel = new LabelWidget
            {
                Text = "Tap an item in the list",
                Color = dim,
                VerticalAlignment = WidgetAlignment.Center
            };
            footer.AddChildren(m_selectedLabel);
            footer.AddChildren(new UniformSpacerWidget());

            m_amountButtons = new BevelledButtonWidget[m_amounts.Length];
            for (int i = 0; i < m_amounts.Length; i++)
            {
                m_amountButtons[i] = new BevelledButtonWidget { Size = new Vector2(72f, 44f), Margin = new Vector2(3f, 0f) };
                footer.AddChildren(m_amountButtons[i]);
            }
            m_giveButton = new BevelledButtonWidget { Text = "GIVE", Size = new Vector2(100f, 44f), Margin = new Vector2(8f, 0f) };
            footer.AddChildren(m_giveButton);
            content.AddChildren(footer);

            // ---- status line ----
            m_countLabel = new LabelWidget
            {
                Color = dim,
                FontScale = 0.7f,
                HorizontalAlignment = WidgetAlignment.Near
            };
            content.AddChildren(m_countLabel);

            panel.AddChildren(content);
            AddChildren(panel);

            RefreshAmountButtons();
            Refill();
        }

        // Build the list of spawnable items once per game session.
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

        Widget BuildRow(object item)
        {
            Entry entry = (Entry)item;
            StackPanelWidget row = new StackPanelWidget
            {
                Direction = LayoutDirection.Horizontal,
                Size = new Vector2(float.PositiveInfinity, 52f)
            };
            row.AddChildren(new BlockIconWidget
            {
                Value = entry.Value,
                Size = new Vector2(40f, 40f),
                VerticalAlignment = WidgetAlignment.Center,
                Margin = new Vector2(8f, 0f)
            });
            row.AddChildren(new LabelWidget
            {
                Text = entry.Name,
                VerticalAlignment = WidgetAlignment.Center,
                Margin = new Vector2(10f, 0f)
            });
            return row;
        }

        void Refill()
        {
            m_list.ClearItems();
            int shown = 0;
            foreach (Entry entry in s_entries)
            {
                if (m_filter.Length == 0 || entry.Lower.Contains(m_filter))
                {
                    m_list.AddItem(entry);
                    shown++;
                }
            }
            string filterText = m_filter.Length == 0 ? "" : "  |  filter: " + m_filter;
            m_countLabel.Text = shown + " items" + filterText;
        }

        void Select(Entry entry)
        {
            if (entry == null) return;
            m_selected = entry;
            m_selectedLabel.Text = entry.Name;
            m_selectedLabel.Color = Color.White;
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
                    Refill();
                }));
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
            {
                Give();
            }
        }
    }
}
