using System.Collections.Generic;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Attributes;
using System.Windows.Forms;

namespace QuickPortal
{
    public class QuickPortalSettings : ISettings
    {
        [Menu("Enable", "Enables to QuickPortal plugin")]
        public ToggleNode Enable { get; set; } = new ToggleNode(true);

        [Menu("Action Delay (ms)", "Delay in milliseconds between mouse move/click actions")]
        public RangeNode<int> ActionDelay { get; set; } = new RangeNode<int>(150, 50, 500);

        [Menu("Hotkey", "Key to trigger portal activation")]
        public HotkeyNodeV2 Hotkey { get; set; } = new HotkeyNodeV2(Keys.F7);

        [Menu("Inventory Key", "Key used to open inventory (default: I)")]
        public HotkeyNodeV2 InventoryKey { get; set; } = new HotkeyNodeV2(Keys.I);

        [Menu("Auto Open Inventory & Use Scroll", "If no portal is on ground, open inventory and use a portal scroll")]
        public ToggleNode AutoOpenInventoryAndUseScroll { get; set; } = new ToggleNode(true);

        [Menu("Lock Cursor", "Lock cursor to game window during portal execution")]
        public ToggleNode LockCursor { get; set; } = new ToggleNode(true);

        [Menu("Screen Range Only", "Only use portals visible on screen")]
        public ToggleNode ScreenRangeOnly { get; set; } = new ToggleNode(true);

        [Menu("Portal Timeout (sec)", "Maximum time to wait for portal to appear after using scroll")]
        public RangeNode<int> PortalTimeout { get; set; } = new RangeNode<int>(5, 1, 10);

        public List<string> PortalMetadata { get; set; }
    }
}
