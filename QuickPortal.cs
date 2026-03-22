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
using SharpDX;
using Input = ExileCore.Input;
using Vector2N = System.Numerics.Vector2;

namespace QuickPortal
{
    public class QuickPortal : BaseSettingsPlugin<QuickPortalSettings>
    {
        private static class GamePaths
        {
            public const string PortalObject = "Metadata/MiscellaneousObjects/MultiplexPortal";
            public const string PortalScroll = "Metadata/Items/Currency/CurrencyPortal";
        }

        private static class ItemNames
        {
            public const string PortalScroll = "Portal Scroll";
        }

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

        private LabelOnGround FindNearestPortal()
        {
            var gameUi = GameController?.Game?.IngameState?.IngameUi;
            if (gameUi == null) return null;

            var groundLabels = gameUi.ItemsOnGroundLabels;
            if (groundLabels == null) return null;

            var playerPos = GameController.Game.IngameState.Data.LocalPlayer?.GridPos;
            if (playerPos == null) return null;

            return groundLabels
                .Where(label => label?.ItemOnGround?.Path == GamePaths.PortalObject)
                .Where(label => label?.Label != null && label.Label.Address != 0)
                .Where(label => !Settings.ScreenRangeOnly.Value || IsOnScreen(label))
                .OrderBy(label => label.ItemOnGround.GridPos.Distance(playerPos.Value))
                .FirstOrDefault();
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
                        .Where(label => label?.ItemOnGround?.Path == GamePaths.PortalObject)
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
    }
}
