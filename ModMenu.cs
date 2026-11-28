using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Engine;
using GameEntitySystem;
using HarmonyLib;

namespace Game
{
    // ------------------------------------------------------------------
    // Loader: registers the Harmony patch below and writes a line to the
    // game log so you can confirm the mod was loaded.
    // ------------------------------------------------------------------
    public class ModMenuLoader : ModLoader
    {
        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("OnLoadingFinished", this, 1);
            new Harmony("com.user.modmenu").PatchAll(typeof(ModMenuLoader).Assembly);
        }

        public override void OnLoadingFinished(List<Action> actions)
        {
            Log.Information("[ModMenu] loaded");
        }
    }

    // ------------------------------------------------------------------
    // After every ComponentPlayer.Update, make sure that player has the
    // "MOD" launcher button on its HUD (added once per player).
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(ComponentPlayer), nameof(ComponentPlayer.Update))]
    public static class ModMenuPlayerPatch
    {
        static readonly ConditionalWeakTable<ComponentPlayer, ModMenuLauncher> s_launchers =
            new ConditionalWeakTable<ComponentPlayer, ModMenuLauncher>();

        [HarmonyPostfix]
        public static void Postfix(ComponentPlayer __instance)
        {
            if (__instance == null) return;
            if (s_launchers.TryGetValue(__instance, out ModMenuLauncher _)) return;

            ContainerWidget gui = __instance.GuiWidget;
            if (gui == null) return;

            ModMenuLauncher launcher = new ModMenuLauncher(__instance);
            gui.AddChildren(launcher);
            s_launchers.Add(__instance, launcher);
        }
    }

    // ------------------------------------------------------------------
    // Reflection helpers. Used for game values whose exact member names
    // are not guaranteed, so a wrong name never breaks compilation.
    // ------------------------------------------------------------------
    public static class Rf
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly HashSet<string> s_warned = new HashSet<string>();

        public static Component FindComponent(ComponentPlayer player, string typeName)
        {
            foreach (Component component in player.Entity.Components)
            {
                if (component.GetType().Name == typeName) return component;
            }
            return null;
        }

        public static bool HasMember(object target, string name)
        {
            if (target == null) return false;
            try
            {
                Type type = target.GetType();
                return type.GetProperty(name, Flags) != null || type.GetField(name, Flags) != null;
            }
            catch (Exception) { return false; }
        }

        public static object Get(object target, string name)
        {
            if (target == null) return null;
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, Flags);
                if (property != null && property.CanRead) return property.GetValue(target);
                FieldInfo field = type.GetField(name, Flags);
                if (field != null) return field.GetValue(target);
            }
            catch (Exception) { }
            return null;
        }

        public static bool Set(object target, string name, object value)
        {
            if (target == null) return false;
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, Flags);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(target, Convert(value, property.PropertyType));
                    return true;
                }
                FieldInfo field = type.GetField(name, Flags);
                if (field != null)
                {
                    field.SetValue(target, Convert(value, field.FieldType));
                    return true;
                }
            }
            catch (Exception e)
            {
                if (s_warned.Add(name))
                    Log.Warning("[ModMenu] could not set '" + name + "': " + e.Message);
            }
            return false;
        }

        static object Convert(object value, Type type)
        {
            Type real = Nullable.GetUnderlyingType(type) ?? type;
            return System.Convert.ChangeType(value, real);
        }

        // Lists members whose name contains the text (for the diagnostics line).
        public static string ListMembers(object target, string contains)
        {
            if (target == null) return "-";
            List<string> names = new List<string>();
            try
            {
                Type type = target.GetType();
                foreach (PropertyInfo p in type.GetProperties(Flags))
                    if (p.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) names.Add(p.Name);
                foreach (FieldInfo f in type.GetFields(Flags))
                    if (f.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) names.Add(f.Name);
            }
            catch (Exception) { }
            return names.Count == 0 ? "-" : string.Join(", ", names);
        }
    }

    // ------------------------------------------------------------------
    // The "MOD" button next to the crouch button (right side of screen).
    // It also applies the cheat toggles every frame.
    // ------------------------------------------------------------------
    public class ModMenuLauncher : CanvasWidget
    {
        // If the button overlaps another button, change these two numbers.
        const float TopOffset = 190f;
        const float SideOffset = 0f;

        public bool LockHealth;
        public bool LockFood;
        public bool Fly;
        public double? LevelLock;

        public readonly ComponentPlayer Player;
        readonly BevelledButtonWidget m_button;

        bool m_cached;
        Component m_health;
        Component m_vitals;
        Component m_locomotion;
        bool m_flyWasOn;

        object m_levelTarget;
        string m_levelMember;
        bool m_levelSearched;

        public ModMenuLauncher(ComponentPlayer player)
        {
            Player = player;
            Size = new Vector2(82f, 46f);
            HorizontalAlignment = WidgetAlignment.Far;
            VerticalAlignment = WidgetAlignment.Near;
            Margin = new Vector2(SideOffset, TopOffset);

            m_button = new BevelledButtonWidget { Text = "MOD", Size = new Vector2(82f, 46f) };
            AddChildren(m_button);
        }

        // Finds where the game keeps the player's level (searched once).
        public bool TryGetLevelMember(out object target, out string member)
        {
            if (!m_levelSearched)
            {
                m_levelSearched = true;
                object[] targets =
                {
                    Rf.FindComponent(Player, "ComponentLevel"),
                    Rf.Get(Player, "PlayerStats")
                };
                string[] names = { "Level", "LevelOverride", "ExperienceLevel", "m_level" };
                foreach (object candidate in targets)
                {
                    foreach (string name in names)
                    {
                        if (Rf.HasMember(candidate, name))
                        {
                            m_levelTarget = candidate;
                            m_levelMember = name;
                            break;
                        }
                    }
                    if (m_levelTarget != null) break;
                }
                if (m_levelTarget == null)
                {
                    Log.Information("[ModMenu] level members: ComponentLevel -> " + Rf.ListMembers(targets[0], "level")
                        + " | PlayerStats -> " + Rf.ListMembers(targets[1], "level"));
                }
            }
            target = m_levelTarget;
            member = m_levelMember;
            return m_levelTarget != null;
        }

        public string LevelDiagnostics()
        {
            object a = Rf.FindComponent(Player, "ComponentLevel");
            object b = Rf.Get(Player, "PlayerStats");
            return "ComponentLevel: " + Rf.ListMembers(a, "level") + "  |  PlayerStats: " + Rf.ListMembers(b, "level");
        }

        void Cache()
        {
            if (m_cached) return;
            m_cached = true;
            m_health = Rf.FindComponent(Player, "ComponentHealth");
            m_vitals = Rf.FindComponent(Player, "ComponentVitalStats");
            m_locomotion = Rf.FindComponent(Player, "ComponentLocomotion");
        }

        void ApplyCheats()
        {
            Cache();

            if (LockHealth)
                Rf.Set(m_health, "Health", 1f);

            if (LockFood)
            {
                Rf.Set(m_vitals, "Food", 1f);
                Rf.Set(m_vitals, "Stamina", 1f);
            }

            if (Fly || m_flyWasOn)
            {
                Rf.Set(m_locomotion, "IsCreativeFlyEnabled", Fly);
                m_flyWasOn = Fly;
            }

            if (LevelLock.HasValue && TryGetLevelMember(out object target, out string member))
                Rf.Set(target, member, LevelLock.Value);
        }

        public override void Update()
        {
            if (m_button.IsClicked)
                DialogsManager.ShowDialog(Player.GuiWidget, new ModMenuDialog(this));

            ApplyCheats();
        }
    }

    // ------------------------------------------------------------------
    // The menu window. Four separate tabs: Items, Level, Immortal, Fly.
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
        const int TabCount = 4;
        static readonly string[] TabNames = { "Items", "Level", "Immortal", "Fly" };

        static Dictionary<string, List<Entry>> s_byCategory;
        static List<string> s_categories;

        readonly ModMenuLauncher m_launcher;
        readonly ComponentPlayer m_player;

        // common
        readonly BevelledButtonWidget m_closeButton;
        readonly BevelledButtonWidget[] m_tabButtons = new BevelledButtonWidget[TabCount];
        readonly StackPanelWidget[] m_panels = new StackPanelWidget[TabCount];
        readonly LabelWidget m_statusLabel;
        int m_tab;

        // items tab
        readonly List<Entry> m_filtered = new List<Entry>();
        readonly StackPanelWidget[] m_rows = new StackPanelWidget[PageSize];
        readonly BlockIconWidget[] m_icons = new BlockIconWidget[PageSize];
        readonly BevelledButtonWidget[] m_rowButtons = new BevelledButtonWidget[PageSize];
        readonly BevelledButtonWidget[] m_amountButtons;
        readonly int[] m_amounts = { 1, 10, 40 };
        readonly LabelWidget m_categoryLabel;
        readonly LabelWidget m_pageLabel;
        readonly BevelledButtonWidget m_prevCategory;
        readonly BevelledButtonWidget m_nextCategory;
        readonly BevelledButtonWidget m_searchButton;
        readonly BevelledButtonWidget m_prevPage;
        readonly BevelledButtonWidget m_nextPage;
        readonly BevelledButtonWidget m_giveButton;
        int m_amountIndex = 1;
        int m_categoryIndex;
        int m_page;
        string m_filter = "";
        Entry m_selected;

        // level tab
        readonly LabelWidget m_levelInfo;
        readonly LabelWidget m_levelLock;
        readonly LabelWidget m_levelDiag;
        readonly BevelledButtonWidget[] m_levelButtons = new BevelledButtonWidget[4];
        readonly int[] m_levelDeltas = { -10, -1, 1, 10 };
        readonly BevelledButtonWidget m_levelType;
        readonly BevelledButtonWidget m_levelRelease;

        // immortal tab
        readonly BevelledButtonWidget m_healthButton;
        readonly BevelledButtonWidget m_foodButton;

        // fly tab
        readonly BevelledButtonWidget m_flyButton;

        static Widget Spacer()
        {
            return new CanvasWidget { Size = new Vector2(float.PositiveInfinity, 0f) };
        }

        static Widget Centered(LabelWidget label, float width, float height)
        {
            label.HorizontalAlignment = WidgetAlignment.Center;
            label.VerticalAlignment = WidgetAlignment.Center;
            CanvasWidget box = new CanvasWidget { Size = new Vector2(width, height) };
            box.AddChildren(label);
            return box;
        }

        public ModMenuDialog(ModMenuLauncher launcher)
        {
            m_launcher = launcher;
            m_player = launcher.Player;
            EnsureEntries(m_player);

            Color accent = new Color(86, 156, 255);
            Color panelFill = new Color(14, 18, 30, 242);
            Color dim = new Color(160, 170, 190);

            CanvasWidget window = new CanvasWidget
            {
                Size = new Vector2(660f, 450f),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Center
            };
            window.AddChildren(new RectangleWidget { FillColor = panelFill, OutlineColor = accent });

            StackPanelWidget content = new StackPanelWidget
            {
                Direction = LayoutDirection.Vertical,
                Margin = new Vector2(14f, 10f)
            };

            // ---- header ----
            StackPanelWidget header = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            header.AddChildren(new LabelWidget { Text = "MOD MENU", Color = accent, VerticalAlignment = WidgetAlignment.Center });
            header.AddChildren(Spacer());
            m_closeButton = new BevelledButtonWidget { Text = "Close", Size = new Vector2(100f, 38f), Margin = new Vector2(3f, 0f) };
            header.AddChildren(m_closeButton);
            content.AddChildren(header);

            // ---- tab buttons ----
            StackPanelWidget tabs = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            for (int i = 0; i < TabCount; i++)
            {
                m_tabButtons[i] = new BevelledButtonWidget { Size = new Vector2(150f, 38f), Margin = new Vector2(3f, 2f) };
                tabs.AddChildren(m_tabButtons[i]);
            }
            content.AddChildren(tabs);

            // ---- tab 0: items ----
            StackPanelWidget items = new StackPanelWidget { Direction = LayoutDirection.Vertical };

            StackPanelWidget categoryRow = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            m_prevCategory = new BevelledButtonWidget { Text = "<", Size = new Vector2(60f, 38f), Margin = new Vector2(3f, 0f) };
            m_nextCategory = new BevelledButtonWidget { Text = ">", Size = new Vector2(60f, 38f), Margin = new Vector2(3f, 0f) };
            m_categoryLabel = new LabelWidget { Color = Color.White };
            m_searchButton = new BevelledButtonWidget { Text = "Search", Size = new Vector2(110f, 38f), Margin = new Vector2(3f, 0f) };
            categoryRow.AddChildren(m_prevCategory);
            categoryRow.AddChildren(Centered(m_categoryLabel, 260f, 38f));
            categoryRow.AddChildren(m_nextCategory);
            categoryRow.AddChildren(Spacer());
            categoryRow.AddChildren(m_searchButton);
            items.AddChildren(categoryRow);

            for (int i = 0; i < PageSize; i++)
            {
                m_icons[i] = new BlockIconWidget
                {
                    Size = new Vector2(34f, 34f),
                    VerticalAlignment = WidgetAlignment.Center,
                    Margin = new Vector2(4f, 0f)
                };
                m_rowButtons[i] = new BevelledButtonWidget { Size = new Vector2(560f, 38f), Margin = new Vector2(2f, 1f) };
                m_rows[i] = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
                m_rows[i].AddChildren(m_icons[i]);
                m_rows[i].AddChildren(m_rowButtons[i]);
                items.AddChildren(m_rows[i]);
            }

            StackPanelWidget bottomRow = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            m_prevPage = new BevelledButtonWidget { Text = "<", Size = new Vector2(56f, 38f), Margin = new Vector2(3f, 0f) };
            m_nextPage = new BevelledButtonWidget { Text = ">", Size = new Vector2(56f, 38f), Margin = new Vector2(3f, 0f) };
            m_pageLabel = new LabelWidget { Color = dim };
            bottomRow.AddChildren(m_prevPage);
            bottomRow.AddChildren(m_nextPage);
            bottomRow.AddChildren(Centered(m_pageLabel, 110f, 38f));
            bottomRow.AddChildren(Spacer());
            m_amountButtons = new BevelledButtonWidget[m_amounts.Length];
            for (int i = 0; i < m_amounts.Length; i++)
            {
                m_amountButtons[i] = new BevelledButtonWidget { Size = new Vector2(70f, 38f), Margin = new Vector2(3f, 0f) };
                bottomRow.AddChildren(m_amountButtons[i]);
            }
            m_giveButton = new BevelledButtonWidget { Text = "GIVE", Size = new Vector2(96f, 38f), Margin = new Vector2(6f, 0f) };
            bottomRow.AddChildren(m_giveButton);
            items.AddChildren(bottomRow);
            m_panels[0] = items;

            // ---- tab 1: level ----
            StackPanelWidget level = new StackPanelWidget { Direction = LayoutDirection.Vertical };
            m_levelInfo = new LabelWidget { Color = Color.White, Margin = new Vector2(4f, 6f) };
            m_levelLock = new LabelWidget { Color = dim, Margin = new Vector2(4f, 2f) };
            level.AddChildren(m_levelInfo);
            level.AddChildren(m_levelLock);

            StackPanelWidget levelRow = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            for (int i = 0; i < m_levelButtons.Length; i++)
            {
                string text = m_levelDeltas[i] > 0 ? "+" + m_levelDeltas[i] : m_levelDeltas[i].ToString();
                m_levelButtons[i] = new BevelledButtonWidget { Text = text, Size = new Vector2(100f, 42f), Margin = new Vector2(3f, 6f) };
                levelRow.AddChildren(m_levelButtons[i]);
            }
            level.AddChildren(levelRow);

            StackPanelWidget levelRow2 = new StackPanelWidget { Direction = LayoutDirection.Horizontal };
            m_levelType = new BevelledButtonWidget { Text = "Type number", Size = new Vector2(200f, 42f), Margin = new Vector2(3f, 6f) };
            m_levelRelease = new BevelledButtonWidget { Text = "Release", Size = new Vector2(160f, 42f), Margin = new Vector2(3f, 6f) };
            levelRow2.AddChildren(m_levelType);
            levelRow2.AddChildren(m_levelRelease);
            level.AddChildren(levelRow2);

            m_levelDiag = new LabelWidget { Color = dim, FontScale = 0.6f, Margin = new Vector2(4f, 6f) };
            level.AddChildren(m_levelDiag);
            m_panels[1] = level;

            // ---- tab 2: immortal ----
            StackPanelWidget immortal = new StackPanelWidget { Direction = LayoutDirection.Vertical };
            immortal.AddChildren(new LabelWidget { Text = "Keeps the value full every frame.", Color = dim, Margin = new Vector2(4f, 8f) });
            m_healthButton = new BevelledButtonWidget { Size = new Vector2(320f, 46f), Margin = new Vector2(3f, 6f) };
            m_foodButton = new BevelledButtonWidget { Size = new Vector2(320f, 46f), Margin = new Vector2(3f, 6f) };
            immortal.AddChildren(m_healthButton);
            immortal.AddChildren(m_foodButton);
            immortal.AddChildren(new LabelWidget { Text = "Very large damage in one hit may still kill.", Color = dim, FontScale = 0.7f, Margin = new Vector2(4f, 8f) });
            m_panels[2] = immortal;

            // ---- tab 3: fly ----
            StackPanelWidget fly = new StackPanelWidget { Direction = LayoutDirection.Vertical };
            fly.AddChildren(new LabelWidget { Text = "Fly in survival mode.", Color = dim, Margin = new Vector2(4f, 8f) });
            m_flyButton = new BevelledButtonWidget { Size = new Vector2(320f, 46f), Margin = new Vector2(3f, 6f) };
            fly.AddChildren(m_flyButton);
            fly.AddChildren(new LabelWidget { Text = "Jump to go up. If nothing happens, tell the developer.", Color = dim, FontScale = 0.7f, Margin = new Vector2(4f, 8f) });
            m_panels[3] = fly;

            for (int i = 0; i < TabCount; i++)
                content.AddChildren(m_panels[i]);

            // ---- status ----
            m_statusLabel = new LabelWidget { Color = dim, FontScale = 0.7f, Margin = new Vector2(2f, 4f) };
            content.AddChildren(m_statusLabel);

            window.AddChildren(content);
            AddChildren(window);

            RefreshAmountButtons();
            SelectCategory(0);
            RefreshToggles();
            RefreshLevel();
            ShowTab(0);
        }

        // ------------------------------------------------------------ data
        static void EnsureEntries(ComponentPlayer player)
        {
            if (s_byCategory != null) return;

            SubsystemTerrain terrain = player.Project.FindSubsystem<SubsystemTerrain>(true);
            Dictionary<string, List<Entry>> byCategory = new Dictionary<string, List<Entry>>();
            HashSet<int> seen = new HashSet<int>();

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

                    string category = block.GetCategory(value);
                    if (string.IsNullOrEmpty(category)) category = "Other";

                    if (!byCategory.TryGetValue(category, out List<Entry> list))
                    {
                        list = new List<Entry>();
                        byCategory[category] = list;
                    }
                    list.Add(new Entry { Value = value, Name = name, Lower = name.ToLowerInvariant() });
                }
            }

            foreach (List<Entry> list in byCategory.Values)
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            List<string> categories = new List<string>(byCategory.Keys);
            categories.Sort(StringComparer.OrdinalIgnoreCase);

            s_byCategory = byCategory;
            s_categories = categories;
        }

        // ------------------------------------------------------------ tabs
        void ShowTab(int tab)
        {
            m_tab = tab;
            for (int i = 0; i < TabCount; i++)
            {
                m_panels[i].IsVisible = i == tab;
                m_tabButtons[i].Text = i == tab ? "[" + TabNames[i] + "]" : TabNames[i];
            }
            if (tab == 0) RefreshPage();
            else if (tab == 1) RefreshLevel();
            else m_statusLabel.Text = "";
        }

        // ------------------------------------------------------------ items
        void SelectCategory(int index)
        {
            int count = s_categories.Count;
            m_categoryIndex = ((index % count) + count) % count;
            m_categoryLabel.Text = s_categories[m_categoryIndex];
            ApplyFilter();
        }

        void ApplyFilter()
        {
            m_filtered.Clear();
            foreach (Entry entry in s_byCategory[s_categories[m_categoryIndex]])
            {
                if (m_filter.Length == 0 || entry.Lower.Contains(m_filter))
                    m_filtered.Add(entry);
            }
            m_page = 0;
            if (m_tab == 0) RefreshPage();
        }

        int PageCount()
        {
            return Math.Max(1, (m_filtered.Count + PageSize - 1) / PageSize);
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
            m_pageLabel.Text = "Page " + (m_page + 1) + "/" + PageCount();
            string filterText = m_filter.Length == 0 ? "" : "  |  filter: " + m_filter;
            string selectedText = m_selected == null ? "" : "  |  selected: " + m_selected.Name;
            m_statusLabel.Text = m_filtered.Count + " items" + filterText + selectedText;
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

        // ------------------------------------------------------------ level
        void RefreshLevel()
        {
            if (m_launcher.TryGetLevelMember(out object target, out string member))
            {
                object current = Rf.Get(target, member);
                m_levelInfo.Text = "Current level: " + (current ?? "?");
                m_levelDiag.Text = "using " + target.GetType().Name + "." + member;
            }
            else
            {
                m_levelInfo.Text = "Level setting not found in this game version";
                m_levelDiag.Text = m_launcher.LevelDiagnostics();
            }
            m_levelLock.Text = m_launcher.LevelLock.HasValue
                ? "Locked at level " + m_launcher.LevelLock.Value
                : "Not locked (the game controls the level)";
        }

        void ChangeLevel(double delta)
        {
            double baseValue = 1;
            if (m_launcher.LevelLock.HasValue)
            {
                baseValue = m_launcher.LevelLock.Value;
            }
            else if (m_launcher.TryGetLevelMember(out object target, out string member))
            {
                object current = Rf.Get(target, member);
                if (current != null)
                {
                    try { baseValue = System.Convert.ToDouble(current); } catch (Exception) { }
                }
            }
            SetLevel(baseValue + delta);
        }

        void SetLevel(double value)
        {
            if (value < 1) value = 1;
            if (value > 9999) value = 9999;
            m_launcher.LevelLock = Math.Round(value);
            RefreshLevel();
        }

        // ------------------------------------------------------------ toggles
        void RefreshToggles()
        {
            m_healthButton.Text = "Health never drops: " + (m_launcher.LockHealth ? "ON" : "OFF");
            m_foodButton.Text = "Never hungry: " + (m_launcher.LockFood ? "ON" : "OFF");
            m_flyButton.Text = "Fly: " + (m_launcher.Fly ? "ON" : "OFF");
        }

        // ------------------------------------------------------------ update
        public override void Update()
        {
            if (m_closeButton.IsClicked || Input.Cancel)
            {
                DialogsManager.HideDialog(this);
                return;
            }

            for (int i = 0; i < TabCount; i++)
            {
                if (m_tabButtons[i].IsClicked) ShowTab(i);
            }

            if (m_tab == 0) UpdateItems();
            else if (m_tab == 1) UpdateLevel();
            else UpdateToggles();
        }

        void UpdateItems()
        {
            if (m_prevCategory.IsClicked) SelectCategory(m_categoryIndex - 1);
            if (m_nextCategory.IsClicked) SelectCategory(m_categoryIndex + 1);

            if (m_searchButton.IsClicked)
            {
                DialogsManager.ShowDialog(m_player.GuiWidget, new TextBoxDialog("Search in " + s_categories[m_categoryIndex], m_filter, 30, text =>
                {
                    m_filter = (text ?? "").Trim().ToLowerInvariant();
                    ApplyFilter();
                }));
            }

            if (m_prevPage.IsClicked && m_page > 0)
            {
                m_page--;
                RefreshPage();
            }
            if (m_nextPage.IsClicked && m_page < PageCount() - 1)
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

            if (m_giveButton.IsClicked) Give();
        }

        void UpdateLevel()
        {
            for (int i = 0; i < m_levelButtons.Length; i++)
            {
                if (m_levelButtons[i].IsClicked) ChangeLevel(m_levelDeltas[i]);
            }

            if (m_levelType.IsClicked)
            {
                DialogsManager.ShowDialog(m_player.GuiWidget, new TextBoxDialog("Set level (1-9999)", "", 4, text =>
                {
                    if (double.TryParse((text ?? "").Trim(), out double value)) SetLevel(value);
                }));
            }

            if (m_levelRelease.IsClicked)
            {
                m_launcher.LevelLock = null;
                RefreshLevel();
            }
        }

        void UpdateToggles()
        {
            if (m_tab == 2)
            {
                if (m_healthButton.IsClicked)
                {
                    m_launcher.LockHealth = !m_launcher.LockHealth;
                    RefreshToggles();
                }
                if (m_foodButton.IsClicked)
                {
                    m_launcher.LockFood = !m_launcher.LockFood;
                    RefreshToggles();
                }
            }
            else if (m_tab == 3)
            {
                if (m_flyButton.IsClicked)
                {
                    m_launcher.Fly = !m_launcher.Fly;
                    RefreshToggles();
                }
            }
        }
    }
}
