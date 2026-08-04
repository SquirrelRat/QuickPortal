using System;
using System.Linq;
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

namespace QuickPortal
{
    public class QuickPortal : BaseSettingsPlugin<QuickPortalSettings>
    {
        private const string PortalScrollPath = "Metadata/Items/Currency/CurrencyPortal";
        private const string PortalScrollName = "Portal Scroll";
        private class PortalState
        {
            public Vector2? OriginalMousePosition;
            public bool IsProcessingPortal;
            public bool WaitingForPortalToAppear;
            public DateTime PortalWaitStartTime;
            public bool CursorLocked;
            public Vector2? LockedCursorPosition;

            public void Reset()
            {
                OriginalMousePosition = null;
                IsProcessingPortal = false;
                WaitingForPortalToAppear = false;
                PortalWaitStartTime = default;
                CursorLocked = false;
                LockedCursorPosition = null;
            }
        }

        private readonly PortalState _state = new PortalState();
        private SyncTask<bool> _portalTask;
        private IStatusDisposable _activeInputBlock;
        private string _pendingMetadata = "";

        public override void OnLoad() => Order = -50;

        public override bool Initialise()
        {
            if (Settings?.Hotkey?.Value != null) Input.RegisterKey(Settings.Hotkey.Value);
            Settings.Hotkey.OnValueChanged += RegisterHotkey;
            LogMessage("QuickPortal plugin initialized");
            return true;
        }

        private void RegisterHotkey() => Input.RegisterKey(Settings.Hotkey.Value);

        private bool IsTownOrHideout() =>
            GameController?.Game?.IngameState?.Data?.CurrentArea is { } area && (area.IsTown || area.IsHideout);

        private bool IsOnScreen(LabelOnGround label)
        {
            var labelElem = label?.Label;
            if (labelElem == null || labelElem.Address == 0) return false;

            var rect = labelElem.GetClientRect();
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            var windowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero };
            windowRect.Inflate(-36, -36);
            return windowRect.Contains(rect.Center.X, rect.Center.Y);
        }

        private bool IsPortal(Entity entity) =>
            entity != null &&
            (Settings.PortalMetadata?.Contains(entity.Path) == true || IsLegacyPortal(entity));

        private bool IsLegacyPortal(Entity entity) =>
            entity?.Path is { } path &&
            path.Contains("Portal", StringComparison.OrdinalIgnoreCase) &&
            entity.GetComponent<Portal>() != null;

        private LabelOnGround FindNearestPortal()
        {
            var gameUi = GameController?.Game?.IngameState?.IngameUi;
            var groundLabels = gameUi?.ItemsOnGroundLabels;
            var playerPos = GameController.Game.IngameState.Data.LocalPlayer?.GridPosNum;
            if (groundLabels == null || playerPos == null) return null;

            return groundLabels
                .Where(label => IsPortal(label?.ItemOnGround))
                .Where(label => label?.Label != null && label.Label.Address != 0)
                .Where(label => !Settings.ScreenRangeOnly.Value || IsOnScreen(label))
                .OrderBy(label => Vector2.Distance(label.ItemOnGround.GridPosNum, playerPos.Value))
                .FirstOrDefault();
        }

        private Vector2 ToScreen(Vector2 clientPos) =>
            GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num() + clientPos;

        private void TryUsePortal()
        {
            if (IsTownOrHideout())
            {
                LogMessage("Cannot use portal in town or hideout");
                return;
            }

            if (_state.IsProcessingPortal) return;
            _state.OriginalMousePosition = Input.MousePositionNum;
            _state.IsProcessingPortal = true;
            _state.WaitingForPortalToAppear = false;
            _portalTask = RunPortalAsync();
        }

        private async SyncTask<bool> RunPortalAsync()
        {
            try
            {
                if (Settings.LockCursor.Value)
                {
                    _activeInputBlock = TryBlockUserMouse();
                    MarkCursorLocked();
                }

                var portalLabel = FindNearestPortal();
                if (portalLabel?.Label != null)
                {
                    LogMessage("Found portal, clicking...");
                    return await ClickPortalAsync(portalLabel);
                }

                if (Settings.AutoOpenInventoryAndUseScroll.Value)
                {
                    LogMessage("No portal found, using scroll from inventory...");
                    if (!await UsePortalScrollFromInventoryAsync()) return false;
                    return await WaitForPortalAndClickAsync();
                }

                LogMessage("No portal found and auto-use disabled");
                return false;
            }
            catch (Exception ex)
            {
                LogError(ex.ToString());
                return false;
            }
            finally
            {
                Cleanup();
            }
        }

        private IStatusDisposable TryBlockUserMouse()
        {
            try
            {
                return Input.InputManager?.BlockUserMouseInput();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void MarkCursorLocked()
        {
            if (!Settings.LockCursor.Value) return;
            _state.CursorLocked = true;
            _state.LockedCursorPosition = Input.MousePositionNum;
        }

        private void SetCursorPosition(Vector2 position)
        {
            if (_state.CursorLocked) _state.LockedCursorPosition = position;
            Input.SetCursorPos(position);
        }

        private void Cleanup()
        {
            _activeInputBlock?.Dispose();
            _activeInputBlock = null;
            _state.CursorLocked = false;
            _state.LockedCursorPosition = null;
            if (_state.OriginalMousePosition is { } pos) Input.SetCursorPos(pos);
            _state.Reset();
        }

        private async SyncTask<bool> WaitMs(int milliseconds)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < milliseconds)
            {
                await TaskUtils.NextFrame();
            }

            return true;
        }

        private async SyncTask<bool> ClickAtAsync(Vector2 position, MouseButtons button, int delay)
        {
            SetCursorPosition(position);
            await WaitMs(delay);
            Input.Click(button);
            await WaitMs(delay);
            return true;
        }

        private async SyncTask<bool> UsePortalScrollFromInventoryAsync()
        {
            var inventoryVisible = GameController?.Game?.IngameState?.IngameUi?.InventoryPanel.IsVisible ?? false;
            if (!inventoryVisible)
            {
                await ToggleInventoryAsync();
            }

            return await FindAndClickPortalScrollAsync();
        }

        private async SyncTask<bool> ToggleInventoryAsync()
        {
            var key = Settings.InventoryKey.Value.Key;
            Input.KeyDown(key);
            await WaitMs(Settings.ActionDelay.Value);
            Input.KeyUp(key);
            await WaitMs(Settings.ActionDelay.Value * 2);
            return true;
        }

        private async SyncTask<bool> FindAndClickPortalScrollAsync()
        {
            var inventory = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories[(int)InventorySlotE.MainInventory1];
            var items = inventory?.Inventory?.InventorySlotItems;
            if (items == null)
            {
                LogError("Inventory not available");
                return false;
            }

            var portalScroll = items.FirstOrDefault(item =>
                item?.Item is { } it &&
                (it.GetComponent<Base>()?.Name == PortalScrollName || it.Path == PortalScrollPath));
            if (portalScroll == null)
            {
                LogError("No portal scroll found in inventory");
                return false;
            }

            var inventoryItems = GameController?.Game?.IngameState?.IngameUi?.InventoryPanel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems;
            var scrollElement = inventoryItems?.FirstOrDefault(inv => inv?.Item?.Address == portalScroll.Item.Address);
            if (scrollElement == null)
            {
                LogError("Portal scroll element not found in UI");
                return false;
            }

            var delay = Settings.ActionDelay.Value;
            var pos = ToScreen(new Vector2(scrollElement.GetClientRect().Center.X, scrollElement.GetClientRect().Center.Y));
            await ClickAtAsync(pos, MouseButtons.Right, delay);

            await ToggleInventoryAsync();

            _state.PortalWaitStartTime = DateTime.UtcNow;
            _state.WaitingForPortalToAppear = true;
            LogMessage("Portal scroll used, waiting for portal to appear...");
            return true;
        }

        private async SyncTask<bool> WaitForPortalAndClickAsync()
        {
            var appeared = await TaskUtils.CheckEveryFrame(
                () => FindNearestPortal()?.Label != null,
                TimeSpan.FromSeconds(Settings.PortalTimeout.Value));

            if (!appeared)
            {
                LogMessage($"Portal wait timeout ({Settings.PortalTimeout.Value}s)");
                return false;
            }

            var portalLabel = FindNearestPortal();
            if (portalLabel?.Label == null)
            {
                LogError("Portal label not valid");
                return false;
            }

            LogMessage("Portal appeared, clicking...");
            return await ClickPortalAsync(portalLabel);
        }

        private async SyncTask<bool> ClickPortalAsync(LabelOnGround portalLabel)
        {
            if (portalLabel?.Label == null || portalLabel.Label.Address == 0)
            {
                LogError("Portal label not valid");
                return false;
            }

            var rect = portalLabel.Label.GetClientRect();
            var pos = ToScreen(new Vector2(rect.Center.X, rect.Center.Y));
            await ClickAtAsync(pos, MouseButtons.Left, Settings.ActionDelay.Value);
            LogMessage("Portal clicked successfully");
            return true;
        }

        public override Job Tick()
        {
            TaskUtils.RunOrRestart(ref _portalTask, () => null);

            if (_state.CursorLocked && _state.LockedCursorPosition is { } locked)
            {
                var cur = Input.MousePositionNum;
                if (Math.Abs(cur.X - locked.X) > 0.1f || Math.Abs(cur.Y - locked.Y) > 0.1f)
                {
                    Input.SetCursorPos(locked);
                }
            }

            if (Settings.Enable.Value && Settings.Hotkey.PressedOnce()) TryUsePortal();

            return null;
        }

        public override void Render()
        {
            if (_state.IsProcessingPortal && Settings.Enable.Value)
            {
                var status = _state.WaitingForPortalToAppear
                    ? $"Waiting for portal... ({Math.Max(0, Settings.PortalTimeout.Value - (int)(DateTime.UtcNow - _state.PortalWaitStartTime).TotalSeconds)}s)"
                    : "Processing portal...";
                Graphics.DrawText(status, new Vector2(20, 20), Color.White);
            }
        }

        public override void OnUnload()
        {
            _portalTask = null;
            if (Settings.Hotkey != null) Settings.Hotkey.OnValueChanged -= RegisterHotkey;
            Cleanup();
            LogMessage("QuickPortal plugin unloaded");
        }

        public override void DrawSettings()
        {
            base.DrawSettings();

            ImGui.Separator();
            ImGui.Text("Portal Metadata");
            ImGui.TextDisabled("Metadata paths matched as portals. Add your own or remove defaults.");

            Settings.PortalMetadata ??= new System.Collections.Generic.List<string>();

            var toRemove = -1;
            for (var i = 0; i < Settings.PortalMetadata.Count; i++)
            {
                ImGui.PushID(i);
                ImGui.Text(Settings.PortalMetadata[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) toRemove = i;
                ImGui.PopID();
            }

            if (toRemove >= 0) Settings.PortalMetadata.RemoveAt(toRemove);

            var submit = ImGui.InputText("##addMetadata", ref _pendingMetadata, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            submit |= ImGui.Button("Add");
            if (submit && AddPortalMetadata(_pendingMetadata))
            {
                _pendingMetadata = "";
            }
        }

        private bool AddPortalMetadata(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (Settings.PortalMetadata.Contains(value)) return false;
            Settings.PortalMetadata.Add(value);
            return true;
        }
    }
}
