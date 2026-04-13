using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Input = ExileCore.Input;
using Vector2 = System.Numerics.Vector2;
using Vector2N = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace QuickPortal
{
    public class QuickPortal : BaseSettingsPlugin<QuickPortalSettings>
    {
        private static class GamePaths
        {
            public const string PortalScroll = "Metadata/Items/Currency/CurrencyPortal";
        }

        private static class ItemNames
        {
            public const string PortalScroll = "Portal Scroll";
        }

        private readonly List<string> _detectedPortalPaths = new();
        private DateTime _lastDebugUpdate = DateTime.MinValue;

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClipCursor(out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private class PortalState
        {
            public Vector2N? OriginalMousePosition;
            public bool IsProcessingPortal;
            public bool WaitingForPortalToAppear;
            public DateTime PortalWaitStartTime;
            public RECT OriginalClipRect;
            public bool CursorLocked;
            public DateTime LastStatusUpdate;
            public Vector2N? LockedCursorPosition;
            public bool ShouldRestorePosition;

            public void Reset()
            {
                OriginalMousePosition = null;
                IsProcessingPortal = false;
                WaitingForPortalToAppear = false;
                PortalWaitStartTime = default;
                OriginalClipRect = default;
                CursorLocked = false;
                LockedCursorPosition = null;
                ShouldRestorePosition = false;
                LastStatusUpdate = default;
            }
        }

        private readonly PortalState _state = new PortalState();
        private readonly object _stateLock = new object();

        private int _selectedTab = 0;
        private static readonly string[] SidebarItems = ["General", "Visual", "Debug"];

        private static readonly Dictionary<string, string> SliderEditBuffers = new();
        private static string _activeSliderEditId = null;

        public override void OnLoad()
        {
            Order = -50;
        }

        public override bool Initialise()
        {
            if (Settings?.Hotkey?.Value != null)
            {
                Input.RegisterKey(Settings.Hotkey.Value);
            }

            Input.ReleaseKey += OnKeyRelease;
            LogMessage("QuickPortal plugin initialized");

            return true;
        }

        private void OnKeyRelease(object sender, Keys keys)
        {
            if (!Settings.Enable.Value) return;

            lock (_stateLock)
            {
                if (_state.IsProcessingPortal || _state.WaitingForPortalToAppear) return;
            }

            if (Settings.Hotkey.Value != null && keys == Settings.Hotkey.Value)
            {
                TryUsePortal();
            }
        }

        private bool IsTownOrHideout()
        {
            var area = GameController?.Game?.IngameState?.Data?.CurrentArea;
            return area != null && (area.IsTown || area.IsHideout);
        }

        private bool IsOnScreen(LabelOnGround label)
        {
            if (label?.Label == null || label.Label.Address == 0) return false;

            var rect = label.Label.GetClientRect();
            var windowRect = GameController.Window.GetWindowRectangle();

            return rect.X >= windowRect.Left && rect.Y >= windowRect.Top &&
                   rect.X + rect.Width <= windowRect.Right &&
                   rect.Y + rect.Height <= windowRect.Bottom;
        }

        private bool IsPortalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            return path.Contains("Portal", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("portal", StringComparison.OrdinalIgnoreCase);
        }

        private LabelOnGround FindNearestPortal()
        {
            var gameUi = GameController?.Game?.IngameState?.IngameUi;
            if (gameUi == null) return null;

            var groundLabels = gameUi.ItemsOnGroundLabels;
            if (groundLabels == null) return null;

            var playerPos = GameController.Game.IngameState.Data.LocalPlayer?.GridPos;
            if (playerPos == null) return null;

            if (Settings.DebugMode.Value)
            {
                UpdateDebugPortalList(groundLabels);
            }

            return groundLabels
                .Where(label => IsPortalPath(label?.ItemOnGround?.Path))
                .Where(label => label?.Label != null && label.Label.Address != 0)
                .Where(label => !Settings.ScreenRangeOnly.Value || IsOnScreen(label))
                .OrderBy(label => label.ItemOnGround.GridPos.Distance(playerPos.Value))
                .FirstOrDefault();
        }

        private void UpdateDebugPortalList(IEnumerable<LabelOnGround> groundLabels)
        {
            if (DateTime.UtcNow - _lastDebugUpdate < TimeSpan.FromSeconds(1)) return;
            _lastDebugUpdate = DateTime.UtcNow;

            _detectedPortalPaths.Clear();
            foreach (var label in groundLabels)
            {
                var path = label?.ItemOnGround?.Path;
                if (!string.IsNullOrEmpty(path) && IsPortalPath(path))
                {
                    if (!_detectedPortalPaths.Contains(path))
                    {
                        _detectedPortalPaths.Add(path);
                    }
                }
            }
        }

        private void LockCursor()
        {
            lock (_stateLock)
            {
                if (_state.CursorLocked || !Settings.LockCursor.Value) return;

                try
                {
                    var windowRect = GameController.Window.GetWindowRectangle();
                    var lockRect = new RECT
                    {
                        Left = (int)windowRect.Left,
                        Top = (int)windowRect.Top,
                        Right = (int)windowRect.Right,
                        Bottom = (int)windowRect.Bottom
                    };

                    GetClipCursor(out _state.OriginalClipRect);
                    ClipCursor(ref lockRect);
                    _state.CursorLocked = true;

                    _state.LockedCursorPosition = Input.MousePositionNum;
                    _state.ShouldRestorePosition = true;

                    LogMessage("Cursor locked to game window");
                }
                catch (Exception ex)
                {
                    LogError($"Failed to lock cursor: {ex.Message}");
                }
            }
        }

        private void UnlockCursor()
        {
            lock (_stateLock)
            {
                if (!_state.CursorLocked) return;

                try
                {
                    ClipCursor(IntPtr.Zero);
                    _state.CursorLocked = false;
                    _state.LockedCursorPosition = null;
                    _state.ShouldRestorePosition = false;

                    LogMessage("Cursor unlocked");
                }
                catch (Exception ex)
                {
                    LogError($"Failed to unlock cursor: {ex.Message}");
                }
            }
        }

        private void SetCursorPositionWithLock(Vector2N position)
        {
            Input.SetCursorPos(position);
            lock (_stateLock)
            {
                if (_state.CursorLocked)
                {
                    _state.LockedCursorPosition = position;
                }
            }
        }

        private void RestoreMousePosition()
        {
            lock (_stateLock)
            {
                if (_state.OriginalMousePosition.HasValue)
                {
                    try
                    {
                        Input.SetCursorPos(_state.OriginalMousePosition.Value);
                        LogMessage("Mouse position restored");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to restore mouse position: {ex.Message}");
                    }
                    finally
                    {
                        _state.OriginalMousePosition = null;
                    }
                }
            }
        }

        private void TryUsePortal()
        {
            if (IsTownOrHideout())
            {
                LogMessage("Cannot use portal in town or hideout");
                return;
            }

            lock (_stateLock)
            {
                _state.OriginalMousePosition = Input.MousePositionNum;
                _state.IsProcessingPortal = true;
                _state.WaitingForPortalToAppear = false;
                _state.LastStatusUpdate = DateTime.UtcNow;
            }

            LockCursor();

            try
            {
                var portalLabel = FindNearestPortal();

                if (portalLabel?.Label != null)
                {
                    LogMessage("Found portal, clicking...");
                    ThreadPool.QueueUserWorkItem(_ => ClickPortal(portalLabel));
                }
                else if (Settings.AutoOpenInventoryAndUseScroll.Value)
                {
                    LogMessage("No portal found, using scroll from inventory...");
                    ThreadPool.QueueUserWorkItem(_ => UsePortalScrollFromInventory());
                }
                else
                {
                    LogMessage("No portal found and auto-use disabled");
                    AbortPortal();
                }
            }
            catch (Exception ex)
            {
                LogError($"Error in TryUsePortal: {ex.Message}");
                AbortPortal();
            }
        }

        private void UsePortalScrollFromInventory()
        {
            try
            {
                var gameUi = GameController?.Game?.IngameState?.IngameUi;
                if (gameUi == null)
                {
                    LogError("Game UI not available");
                    AbortPortal();
                    return;
                }

                if (gameUi.InventoryPanel.IsVisible)
                {
                    FindAndClickPortalScroll();
                    return;
                }

                Input.KeyDown(Settings.InventoryKey.Value);
                Thread.Sleep(Settings.ActionDelay.Value);
                Input.KeyUp(Settings.InventoryKey.Value);
                Thread.Sleep(Settings.ActionDelay.Value * 2);

                FindAndClickPortalScroll();
            }
            catch (Exception ex)
            {
                LogError($"Error using portal scroll: {ex.Message}");
                AbortPortal();
            }
        }

        private void FindAndClickPortalScroll()
        {
            try
            {
                var inventory = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories[(int)InventorySlotE.MainInventory1];
                if (inventory?.Inventory == null)
                {
                    LogError("Inventory not available");
                    AbortPortal();
                    return;
                }

                var items = inventory.Inventory.InventorySlotItems;
                if (items == null)
                {
                    LogError("Inventory items not available");
                    AbortPortal();
                    return;
                }

                var portalScroll = items
                    .FirstOrDefault(item =>
                    {
                        if (item?.Item == null) return false;
                        var baseComp = item.Item.GetComponent<Base>();
                        if (baseComp == null) return false;
                        return baseComp.Name == ItemNames.PortalScroll || item.Item.Path == GamePaths.PortalScroll;
                    });

                if (portalScroll == null)
                {
                    LogError("No portal scroll found in inventory");
                    AbortPortal();
                    return;
                }

                var inventoryPanel = GameController?.Game?.IngameState?.IngameUi?.InventoryPanel[InventoryIndex.PlayerInventory];
                if (inventoryPanel == null)
                {
                    LogError("Inventory panel not available");
                    AbortPortal();
                    return;
                }

                var inventoryItems = inventoryPanel.VisibleInventoryItems;
                if (inventoryItems == null)
                {
                    LogError("Inventory items UI not available");
                    AbortPortal();
                    return;
                }

                var portalScrollElement = inventoryItems
                    .FirstOrDefault(invItem => invItem?.Item?.Address == portalScroll.Item.Address);

                if (portalScrollElement == null)
                {
                    LogError("Portal scroll element not found in UI");
                    AbortPortal();
                    return;
                }

                var scrollRect = portalScrollElement.GetClientRect();
                var scrollScreenPos = new Vector2N(scrollRect.Center.X, scrollRect.Center.Y);
                var delay = Settings.ActionDelay.Value;

                SetCursorPositionWithLock(scrollScreenPos);
                Thread.Sleep(delay);
                Input.Click(MouseButtons.Right);
                Thread.Sleep(delay * 2);

                Input.KeyDown(Settings.InventoryKey.Value);
                Thread.Sleep(delay);
                Input.KeyUp(Settings.InventoryKey.Value);
                Thread.Sleep(delay * 2);

                lock (_stateLock)
                {
                    _state.PortalWaitStartTime = DateTime.UtcNow;
                    _state.WaitingForPortalToAppear = true;
                }

                LogMessage("Portal scroll used, waiting for portal to appear...");
            }
            catch (Exception ex)
            {
                LogError($"Error finding and clicking portal scroll: {ex.Message}");
                AbortPortal();
            }
        }

        private void ClickPortal(LabelOnGround portalLabel)
        {
            try
            {
                if (portalLabel?.Label == null || portalLabel.Label.Address == 0)
                {
                    LogError("Portal label not valid");
                    AbortPortal();
                    return;
                }

                var labelRect = portalLabel.Label.GetClientRect();
                var portalScreenPos = new Vector2N(labelRect.Center.X, labelRect.Center.Y);
                var delay = Settings.ActionDelay.Value;

                SetCursorPositionWithLock(portalScreenPos);
                Thread.Sleep(delay);
                Input.Click(MouseButtons.Left);
                Thread.Sleep(delay);

                LogMessage("Portal clicked successfully");
                CompletePortal();
            }
            catch (Exception ex)
            {
                LogError($"Error clicking portal: {ex.Message}");
                AbortPortal();
            }
        }

        private void WaitForPortalAndClick()
        {
            var timeout = TimeSpan.FromSeconds(5);
            var checkInterval = 100;

            while (DateTime.UtcNow - _state.PortalWaitStartTime < timeout)
            {
                var gameUi = GameController.Game.IngameState.IngameUi;
                if (gameUi == null) continue;

                var groundLabels = gameUi.ItemsOnGroundLabels;
                if (groundLabels == null) continue;

                var playerPos = GameController.Game.IngameState.Data.LocalPlayer?.GridPos;
                LabelOnGround portalLabel = null;

                if (playerPos != null)
                {
                    portalLabel = groundLabels
                        .Where(label => IsPortalPath(label?.ItemOnGround?.Path))
                        .Where(label => label?.Label != null && label.Label.Address != 0)
                        .Where(label => !Settings.ScreenRangeOnly.Value || IsOnScreen(label))
                        .OrderBy(label => label.ItemOnGround.GridPos.Distance(playerPos.Value))
                        .FirstOrDefault();
                }

                if (portalLabel?.Label != null)
                {
                    _state.WaitingForPortalToAppear = false;
                    ClickPortal(portalLabel);
                    return;
                }

                Thread.Sleep(checkInterval);
            }

            AbortPortal();
        }

        private void CompletePortal()
        {
            RestoreMousePosition();
            UnlockCursor();
            lock (_stateLock)
            {
                _state.Reset();
            }
        }

        private void AbortPortal()
        {
            LogMessage("Aborting portal operation");
            RestoreMousePosition();
            UnlockCursor();
            lock (_stateLock)
            {
                _state.Reset();
            }
        }

        public override Job Tick()
        {
            lock (_stateLock)
            {
                if (_state.CursorLocked && _state.ShouldRestorePosition && _state.LockedCursorPosition.HasValue)
                {
                    var currentPos = Input.MousePositionNum;
                    var lockedPos = _state.LockedCursorPosition.Value;

                    if (Math.Abs(currentPos.X - lockedPos.X) > 0.1f || Math.Abs(currentPos.Y - lockedPos.Y) > 0.1f)
                    {
                        Input.SetCursorPos(lockedPos);
                    }
                }
            }

            lock (_stateLock)
            {
                if (_state.WaitingForPortalToAppear)
                {
                    var timeoutSeconds = Settings.PortalTimeout.Value;
                    if ((DateTime.UtcNow - _state.PortalWaitStartTime).TotalSeconds > timeoutSeconds)
                    {
                        LogMessage($"Portal wait timeout ({timeoutSeconds}s)");
                        AbortPortal();
                        return null;
                    }
                }
            }

            lock (_stateLock)
            {
                if (_state.WaitingForPortalToAppear)
                {
                    var portalLabel = FindNearestPortal();
                    if (portalLabel?.Label != null)
                    {
                        LogMessage("Portal appeared, clicking...");
                        _state.WaitingForPortalToAppear = false;
                        ThreadPool.QueueUserWorkItem(_ => ClickPortal(portalLabel));
                    }
                }
            }

            return null;
        }

        public override void Render()
        {
            lock (_stateLock)
            {
                if (_state.IsProcessingPortal && Settings.Enable.Value)
                {
                    var position = new Vector2N(20, 20);
                    var status = _state.WaitingForPortalToAppear
                        ? $"Waiting for portal... ({Settings.PortalTimeout.Value - (int)(DateTime.UtcNow - _state.PortalWaitStartTime).TotalSeconds}s)"
                        : "Processing portal...";
                    Graphics.DrawText(status, position, Color.White);
                }
            }
        }

        public override void OnUnload()
        {
            UnlockCursor();
            RestoreMousePosition();
            Input.ReleaseKey -= OnKeyRelease;
            LogMessage("QuickPortal plugin unloaded");
        }

        private void LogMessage(string message)
        {
        }

        private void LogError(string message)
        {
        }

        #region Themed Settings UI

        public override void DrawSettings()
        {
            PushSettingsStyle();

            var sidebarHeight = ImGui.GetContentRegionAvail().Y - 60f;
            if (ImGui.BeginChild("Sidebar", new Vector2(140f, sidebarHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.None))
            {
                for (var i = 0; i < SidebarItems.Length; i++)
                {
                    DrawSidebarItem(SidebarItems[i], i);
                }
            }
            ImGui.EndChild();
            ImGui.SameLine();

            if (ImGui.BeginChild("Content", new Vector2(ImGui.GetContentRegionAvail().X - 4f, sidebarHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.None))
            {
                switch (_selectedTab)
                {
                    case 0:
                        DrawGeneralTab();
                        break;
                    case 1:
                        DrawVisualTab();
                        break;
                    case 2:
                        DrawDebugTab();
                        break;
                }
            }
            ImGui.EndChild();

            PopSettingsStyle();
        }

        private void DrawSidebarItem(string label, int index)
        {
            var color = index == _selectedTab
                ? new Vector4(0.40f, 0.85f, 1.00f, 1f)
                : new Vector4(0.50f, 0.65f, 0.80f, 1f);

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            if (ImGui.Selectable(label, _selectedTab == index))
            {
                _selectedTab = index;
            }
            ImGui.PopStyleColor();
        }

        private void DrawGeneralTab()
        {
            ImGui.Text("General Settings");
            ImGui.Separator();

            Settings.Enable.Value = UiHelpers.Checkbox("Enable Plugin", Settings.Enable.Value);
            Settings.AutoOpenInventoryAndUseScroll.Value = UiHelpers.Checkbox("Auto Use Portal Scroll", Settings.AutoOpenInventoryAndUseScroll.Value);
            Settings.LockCursor.Value = UiHelpers.Checkbox("Lock Cursor During Portal", Settings.LockCursor.Value);
            Settings.ScreenRangeOnly.Value = UiHelpers.Checkbox("Screen Range Only", Settings.ScreenRangeOnly.Value);

            ImGui.Spacing();
            ImGui.Text("Timing");

            var actionDelay = Settings.ActionDelay.Value;
            ModernSlider("Action Delay (ms)", ref actionDelay, 50, 500);
            Settings.ActionDelay.Value = actionDelay;

            var portalTimeout = Settings.PortalTimeout.Value;
            ModernSlider("Portal Timeout (sec)", ref portalTimeout, 1, 10);
            Settings.PortalTimeout.Value = portalTimeout;

            var checkInterval = Settings.PortalCheckInterval.Value;
            ModernSlider("Check Interval (ms)", ref checkInterval, 50, 500);
            Settings.PortalCheckInterval.Value = checkInterval;
        }

        private void DrawVisualTab()
        {
            ImGui.Text("Hotkeys");
            ImGui.Separator();

            ImGui.Text("Portal Hotkey");
            ImGui.TextDisabled($"Current: {Settings.Hotkey.Value}");
            Settings.Hotkey.DrawPickerButton("Change Portal Hotkey");

            ImGui.Spacing();
            ImGui.Text("Inventory Key");
            ImGui.TextDisabled($"Current: {Settings.InventoryKey.Value}");
            Settings.InventoryKey.DrawPickerButton("Change Inventory Key");
        }

        private void DrawDebugTab()
        {
            ImGui.Text("Debug Information");
            ImGui.Separator();

            Settings.DebugMode.Value = UiHelpers.Checkbox("Enable Debug Mode", Settings.DebugMode.Value);

            ImGui.Spacing();
            ImGui.Text("Detected Portal Paths:");

            if (_detectedPortalPaths.Count == 0)
            {
                ImGui.TextDisabled("No portals detected yet. Enable debug mode and find a portal.");
            }
            else
            {
                foreach (var path in _detectedPortalPaths)
                {
                    ImGui.BulletText(path);
                }
            }

            ImGui.Spacing();
            ImGui.Text("Portal Detection Pattern:");
            ImGui.TextDisabled("Detects any object with 'Portal' (case-insensitive) in its path");
            ImGui.TextDisabled("Examples: MultiplexPortal, SuperPortal, PortalObject, etc.");
        }

        private static void PushSettingsStyle()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 3f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 4f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.02f, 0.08f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.03f, 0.10f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.20f, 0.50f, 0.70f, 0.50f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.06f, 0.18f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.10f, 0.30f, 0.50f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.15f, 0.40f, 0.65f, 1f));
            ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.40f, 0.85f, 1.00f, 1f));
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(0.20f, 0.70f, 0.90f, 1f));
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, new Vector4(0.40f, 0.85f, 1.00f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.08f, 0.25f, 0.45f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f, 0.40f, 0.65f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.55f, 0.80f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.08f, 0.30f, 0.55f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.15f, 0.45f, 0.75f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.25f, 0.60f, 0.90f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.20f, 0.45f, 0.65f, 0.40f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0.03f, 0.08f, 0.12f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(0.20f, 0.50f, 0.70f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.35f, 0.65f, 0.85f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, new Vector4(0.50f, 0.80f, 1.00f, 1f));
        }

        private static void PopSettingsStyle()
        {
            ImGui.PopStyleVar(9);
            ImGui.PopStyleColor(20);
        }

        private bool ModernSlider(string id, ref int value, int min, int max)
        {
            if (_activeSliderEditId == id)
            {
                return HandleSliderTextInput(id, ref value, min, max);
            }

            float floatValue = value;
            if (!ModernSliderFloat(id, ref floatValue, min, max, out var valueClicked))
            {
                if (valueClicked)
                {
                    _activeSliderEditId = id;
                    SliderEditBuffers[id] = value.ToString();
                }
                return false;
            }

            value = (int)Math.Round((double)floatValue);
            return true;
        }

        private static bool HandleSliderTextInput(string id, ref int value, int min, int max)
        {
            if (!SliderEditBuffers.TryGetValue(id, out var buffer))
            {
                buffer = value.ToString();
                SliderEditBuffers[id] = buffer;
            }

            ImGui.SetKeyboardFocusHere();
            if (ImGui.InputText($"##{id}_edit", ref buffer, 10, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
            {
                if (int.TryParse(buffer, out var newValue))
                {
                    value = Math.Clamp(newValue, min, max);
                }
                _activeSliderEditId = null;
                SliderEditBuffers.Remove(id);
                return true;
            }

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsItemHovered())
            {
                _activeSliderEditId = null;
                SliderEditBuffers.Remove(id);
            }

            SliderEditBuffers[id] = buffer;
            return false;
        }

        private static bool ModernSliderFloat(string id, ref float value, float min, float max, out bool valueClicked)
        {
            valueClicked = false;
            var labelSize = ImGui.CalcTextSize(id);
            var valueText = ((int)value).ToString();
            var valueSize = ImGui.CalcTextSize(valueText);
            var height = Math.Max(labelSize.Y, valueSize.Y) + 6f;
            var cursor = ImGui.GetCursorScreenPos();
            var totalWidth = ImGui.GetContentRegionAvail().X;

            var labelPosition = cursor;
            var valuePosition = new Vector2(cursor.X + totalWidth - valueSize.X, cursor.Y + (height - valueSize.Y) * 0.5f);
            var lineStartX = cursor.X + labelSize.X + 12f;
            var lineEndX = cursor.X + totalWidth - valueSize.X - 12f;
            var lineLength = lineEndX - lineStartX;
            var lineY = cursor.Y + height * 0.5f;

            var valueRectMin = valuePosition;
            var valueRectMax = new Vector2(valuePosition.X + valueSize.X, valuePosition.Y + valueSize.Y);

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var mousePos = ImGui.GetMousePos();
                if (mousePos.X >= valueRectMin.X && mousePos.X <= valueRectMax.X &&
                    mousePos.Y >= valueRectMin.Y && mousePos.Y <= valueRectMax.Y)
                {
                    valueClicked = true;
                }
            }

            var sliderButtonWidth = totalWidth;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, height + 2f));
            ImGui.PushStyleColor(ImGuiCol.Button, 0);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0);
            ImGui.PushStyleColor(ImGuiCol.Text, 0);
            var clicked = ImGui.InvisibleButton($"##{id}", new Vector2(sliderButtonWidth, height));
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar();

            var drawList = ImGui.GetWindowDrawList();
            drawList.AddText(labelPosition, UiHelpers.PackColor(0.70f, 0.85f, 1.00f, 1f), id);
            drawList.AddText(valuePosition, UiHelpers.PackColor(0.40f, 0.90f, 1.00f, 1f), valueText);

            if (lineLength <= 0f || max <= min)
            {
                return false;
            }

            var normalized = Math.Clamp((value - min) / (max - min), 0f, 1f);
            var dotX = lineStartX + normalized * lineLength;

            drawList.AddLine(new Vector2(lineStartX, lineY), new Vector2(lineEndX, lineY), UiHelpers.PackColor(0.08f, 0.25f, 0.40f, 1f), 2f);
            drawList.AddLine(new Vector2(lineStartX, lineY), new Vector2(dotX, lineY), UiHelpers.PackColor(0.20f, 0.75f, 0.95f, 0.7f), 2f);

            var isActive = ImGui.IsItemActive();
            var isHovered = ImGui.IsItemHovered();
            var dotRadius = isActive ? 7f : isHovered ? 6.5f : 5.5f;
            var dotColor = isActive
                ? UiHelpers.PackColor(0.40f, 0.90f, 1.00f, 1.0f)
                : isHovered
                    ? UiHelpers.PackColor(0.30f, 0.80f, 0.95f, 1.0f)
                    : UiHelpers.PackColor(0.20f, 0.70f, 0.90f, 1.0f);

            drawList.AddCircleFilled(new Vector2(dotX, lineY), dotRadius, dotColor);
            drawList.AddCircleFilled(new Vector2(dotX, lineY), dotRadius - 2f, UiHelpers.PackColor(0.02f, 0.08f, 0.15f, 1f));
            drawList.AddCircleFilled(new Vector2(dotX, lineY), dotRadius - 3.5f, dotColor);

            if (!valueClicked && (isActive || (clicked && isHovered)))
            {
                var mousePosition = ImGui.GetMousePos();
                var newNormalized = Math.Clamp((mousePosition.X - lineStartX) / lineLength, 0f, 1f);
                value = Math.Clamp(min + newNormalized * (max - min), min, max);
                return true;
            }

            return false;
        }

        #endregion
    }

    internal static class UiHelpers
    {
        public static bool Checkbox(string label, bool value)
        {
            ImGui.Checkbox(label, ref value);
            return value;
        }

        public static int IntDrag(string label, int value, int minValue, int maxValue, float dragSpeed)
        {
            var currentValue = value;
            ImGui.DragInt(label, ref currentValue, dragSpeed, minValue, maxValue);
            return currentValue;
        }

        public static float FloatDrag(string label, float value, float minValue, float maxValue, float dragSpeed)
        {
            var currentValue = value;
            ImGui.DragFloat(label, ref currentValue, dragSpeed, minValue, maxValue);
            return currentValue;
        }

        public static Color ColorPicker(string label, Color inputColor)
        {
            var color = inputColor.ToVector4();
            var pickerColor = new Vector4(color.X, color.Y, color.Z, color.W);
            if (ImGui.ColorEdit4(label, ref pickerColor, ImGuiColorEditFlags.AlphaBar))
            {
                return new Color(pickerColor.X, pickerColor.Y, pickerColor.Z, pickerColor.W);
            }
            return inputColor;
        }

        public static uint PackColor(float red, float green, float blue, float alpha)
        {
            return (uint)(alpha * 255) << 24 | (uint)(blue * 255) << 16 | (uint)(green * 255) << 8 | (uint)(red * 255);
        }
    }
}
