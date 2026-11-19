/** Where a link opens the PC room (DESIGN.md › PC): one of its sections, scrolled into view (Shaders held there while
    what is above it loads). */
export type PcLink = 'overview' | 'graphics' | 'shaders' | 'upscalers' | 'display' | 'board' | 'network' | 'control' | 'performance' | 'apps' | 'drives' | 'drivers' | 'compress' | 'autopilot'

/** Game runtimes, by what people call them (they live in PC › Updates). */
const RUNTIMES = [
  'runtimes', 'redistributables', 'dependencies', 'install runtimes', 'update runtimes', 'repair visual c++', 'missing dll', 'msvcp140', 'vcruntime140',
  'visual c++', 'vc++', 'vcredist', 'vc_redist', 'msvc', 'directx', 'directx 12', 'dx12', 'd3dx9', 'xinput', 'xaudio', 'gameinput', 'directstorage',
  'agility sdk', '.net', 'dotnet', '.net framework', 'desktop runtime', 'xna', 'openal', 'physx', 'webview2', 'vulkan',
]

/**
 * Each section of the room: its anchor (`data-pc-part`), its name and the words Ctrl+K finds it by. Kept apart from the
 * room so Ctrl+K reads it without loading the room's code.
 */
export const PC_SECTIONS: readonly { id: PcLink; label: string; keywords: readonly string[] }[] = [
  { id: 'overview', label: 'This PC', keywords: ['this pc', 'about', 'specs', 'processor', 'cpu', 'cores', 'graphics card', 'gpu', 'video memory', 'vram', 'memory', 'ram', 'windows version', 'windows update', 'build', 'best settings'] },
  { id: 'drivers', label: 'Updates', keywords: ['updates', 'update', 'check for updates', 'windows update', 'windows version', 'drivers', 'driver', 'update drivers', 'update all', 'driver update', 'graphics driver', 'chipset', 'chipset driver', 'bios', 'firmware', 'ssd firmware', 'ethernet driver', 'wi-fi driver', 'audio driver', 'bluetooth driver', 'usb driver', 'windows update drivers', 'driver easy', ...RUNTIMES] },
  { id: 'graphics', label: 'Graphics', keywords: ['nvidia', 'radeon', 'intel', 'driver profile', 'g-sync', 'upscalers', 'dlss', 'fsr', 'xess'] },
  { id: 'shaders', label: 'Shaders', keywords: ['shaders', 'compile', 'shader cache'] },
  { id: 'control', label: 'Control Panel', keywords: ['control panel', 'nvidia control panel', 'nvcp', 'manage 3d settings', '3d settings', 'nvidia settings', 'amd software', 'adrenalin', 'radeon settings', 'intel graphics', 'arc control', 'graphics command center'] },
  { id: 'display', label: 'Display', keywords: ['screen', 'monitor', 'refresh rate', 'hz', 'hdr', 'color', 'scaling', 'rtx video', 'super resolution', 'adjust desktop color settings', 'change resolution', 'digital vibrance'] },
  { id: 'board', label: 'Motherboard', keywords: ['motherboard', 'bios', 'uefi', 'restart into bios', 'recovery', 'advanced startup', 'secure boot', 'tpm', 'memory integrity'] },
  { id: 'network', label: 'Network', keywords: ['network', 'internet', 'internet optimize', 'optimize network', 'ethernet', 'wi-fi', 'wifi', 'wireless', 'network adapter', 'link speed', 'connection', 'ping', 'latency', 'lag', 'packet loss', 'energy-efficient ethernet', 'eee', 'green ethernet', 'dma coalescing', 'network buffers', 'cable', 'background scan'] },
  { id: 'autopilot', label: 'Autopilot', keywords: ['autopilot', 'game optimizer', 'optimize games', 'game booster', 'boost', 'stutter', 'stutters', 'frame times', '1% low', 'lows', 'session report', 'game mode', 'background apps', 'process priority', 'efficiency mode', 'core parking', 'park control', 'standby list', 'islc', 'memory cleaner', 'dpc latency', 'vram'] },
  { id: 'performance', label: 'Performance', keywords: ['performance', 'power plan', 'gpu scheduling', 'resizable bar', 'rebar', 'interrupts', 'msi mode', 'system tuning', 'hpet', 'platform clock', 'timer', 'win32priorityseparation', 'scheduling', 'page file', 'pagefile', 'virtual memory', 'largesystemcache', 'services', 'memory integrity', 'hvci', 'core isolation', 'vbs', 'restore point'] },
  { id: 'drives', label: 'Drives', keywords: ['drives', 'storage', 'ssd', 'nvme', 'hard drive', 'hdd', 'disk', 'disk space', 'free space', 'drive health', 'smart', 'temperature', 'firmware', 'pcie', 'bypassio', 'trim', 'retrim', 'optimize drives', 'defrag', 'write cache', 'old shader caches', 'directstorage fast path', 'samsung magician'] },
  { id: 'compress', label: 'Compress games', keywords: ['compress', 'compress games', 'compression', 'compactgui', 'compact', 'compact os', 'wof', 'lzx', 'xpress', 'free up space', 'save space', 'shrink games', 'smaller games', 'uncompress'] },
  { id: 'apps', label: 'Apps', keywords: ['apps', 'uninstall', 'programs', 'remove', 'leftovers', 'left behind', 'clean up', 'junk', 'bloatware', 'uninstaller', 'startup', 'startup apps', 'start with windows', 'starts with windows', 'autostart', 'run at startup', 'boot apps', 'recycle bin', 'temp files', 'unused apps', 'last used', 'removed', 'put back', 'undo uninstall', 'broken apps'] },
]
