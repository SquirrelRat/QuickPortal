# QuickPortal

A PoE ExileAPI plugin that automatically clicks portals or uses portal scrolls from inventory.

## Features

- Automatic portal detection and clicking
- Auto-use portal scroll from inventory when no portal found
- Cursor locking during execution
- Configurable timeouts and delays
- Thread-safe operations

## Settings

| Setting | Default | Description |
|---------|----------|-------------|
| Enable | True | Enable the plugin |
| Action Delay (ms) | 150 | Delay between mouse actions |
| Hotkey | F7 | Key to trigger portal usage |
| Inventory Key | I | Key to open inventory |
| Auto Open Inventory & Use Scroll | True | Auto-use scroll if no portal found |
| Lock Cursor | True | Lock cursor to game window |
| Screen Range Only | True | Only use portals on screen |
| Portal Timeout (sec) | 5 | Maximum wait time for portal |
| Portal Check Interval (ms) | 100 | How often to check for portal |

## Usage

1. Set your preferred hotkey (default: F7)
2. Press hotkey to use nearest portal
3. If no portal exists, plugin will use a scroll from inventory
4. Cursor position is restored after completion
