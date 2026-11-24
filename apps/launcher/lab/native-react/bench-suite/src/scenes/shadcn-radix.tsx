import React, { useState } from 'react';
import { createRoot } from 'react-dom/client';
import * as Accordion from '@radix-ui/react-accordion';
import * as AlertDialog from '@radix-ui/react-alert-dialog';
import * as Avatar from '@radix-ui/react-avatar';
import * as Checkbox from '@radix-ui/react-checkbox';
import * as Collapsible from '@radix-ui/react-collapsible';
import * as Dialog from '@radix-ui/react-dialog';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import * as HoverCard from '@radix-ui/react-hover-card';
import * as Popover from '@radix-ui/react-popover';
import * as Progress from '@radix-ui/react-progress';
import * as RadioGroup from '@radix-ui/react-radio-group';
import * as ScrollArea from '@radix-ui/react-scroll-area';
import * as Select from '@radix-ui/react-select';
import * as Separator from '@radix-ui/react-separator';
import * as Slider from '@radix-ui/react-slider';
import * as Switch from '@radix-ui/react-switch';
import * as Tabs from '@radix-ui/react-tabs';
import * as Toggle from '@radix-ui/react-toggle';
import * as ToggleGroup from '@radix-ui/react-toggle-group';
import * as Tooltip from '@radix-ui/react-tooltip';
import { CompSlot, collectComponentMetrics, resetCompStatuses } from './ErrorCard';

function ShadcnRadixGallery() {
  const [sw, setSw] = useState(false);
  const [chk, setChk] = useState(false);
  const [tog, setTog] = useState(false);
  const [colOpen, setColOpen] = useState(false);
  const [dlgOpen, setDlgOpen] = useState(false);
  const [popOpen, setPopOpen] = useState(false);
  const [sliderVal, setSliderVal] = useState([55]);

  return (
    <Tooltip.Provider>
      <div className="w-[1280px] min-h-[800px] bg-[#050508] text-zinc-100 p-4 flex flex-col gap-3">
        <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
          <div className="text-base font-semibold">shadcn/ui + Radix UI Primitives Gallery (20 Unmodified Packages)</div>
          <div className="text-xs text-zinc-400">Tailwind v4 + @radix-ui/react-*</div>
        </div>

        <div className="grid grid-cols-5 gap-3">
          <CompSlot name="RadixSwitch" category="Inputs">
            <div className="flex items-center gap-2">
              <Switch.Root
                id="rx-switch"
                checked={sw}
                onCheckedChange={setSw}
                className="w-10 h-5 rounded-full bg-zinc-700 data-[state=checked]:bg-blue-500 relative"
              >
                <Switch.Thumb className="block w-4 h-4 rounded-full bg-white translate-x-0.5 data-[state=checked]:translate-x-5" />
              </Switch.Root>
              <span className="text-xs">{sw ? 'On' : 'Off'}</span>
            </div>
          </CompSlot>

          <CompSlot name="RadixCheckbox" category="Inputs">
            <div className="flex items-center gap-2">
              <Checkbox.Root
                id="rx-check"
                checked={chk}
                onCheckedChange={(v) => setChk(Boolean(v))}
                className="w-5 h-5 rounded border border-zinc-600 bg-zinc-900 flex items-center justify-center"
              >
                <Checkbox.Indicator className="text-xs text-blue-400">✓</Checkbox.Indicator>
              </Checkbox.Root>
              <span className="text-xs">{chk ? 'Checked' : 'Unchecked'}</span>
            </div>
          </CompSlot>

          <CompSlot name="RadixToggle" category="Inputs">
            <Toggle.Root
              id="rx-toggle"
              pressed={tog}
              onPressedChange={setTog}
              className="px-3 py-1 rounded-md border border-zinc-700 text-xs data-[state=on]:bg-blue-600"
            >
              {tog ? 'Pressed' : 'Toggle'}
            </Toggle.Root>
          </CompSlot>

          <CompSlot name="RadixToggleGroup" category="Inputs">
            <ToggleGroup.Root type="single" defaultValue="b" className="flex gap-1">
              <ToggleGroup.Item value="a" className="px-2 py-1 rounded bg-zinc-800 text-xs">L</ToggleGroup.Item>
              <ToggleGroup.Item value="b" className="px-2 py-1 rounded bg-blue-600 text-xs">C</ToggleGroup.Item>
              <ToggleGroup.Item value="c" className="px-2 py-1 rounded bg-zinc-800 text-xs">R</ToggleGroup.Item>
            </ToggleGroup.Root>
          </CompSlot>

          <CompSlot name="RadixRadioGroup" category="Inputs">
            <RadioGroup.Root defaultValue="1" className="flex gap-3">
              <label className="flex items-center gap-1 text-xs">
                <RadioGroup.Item value="1" className="w-4 h-4 rounded-full border border-zinc-500 flex items-center justify-center">
                  <RadioGroup.Indicator className="w-2 h-2 rounded-full bg-blue-500" />
                </RadioGroup.Item>
                Fast
              </label>
              <label className="flex items-center gap-1 text-xs">
                <RadioGroup.Item value="2" className="w-4 h-4 rounded-full border border-zinc-500 flex items-center justify-center">
                  <RadioGroup.Indicator className="w-2 h-2 rounded-full bg-blue-500" />
                </RadioGroup.Item>
                Quality
              </label>
            </RadioGroup.Root>
          </CompSlot>

          <CompSlot name="RadixSlider" category="Inputs">
            <Slider.Root value={sliderVal} onValueChange={setSliderVal} max={100} className="relative flex items-center h-5 w-full">
              <Slider.Track className="bg-zinc-800 relative grow rounded-full h-1.5">
                <Slider.Range className="absolute bg-blue-500 rounded-full h-full" />
              </Slider.Track>
              <Slider.Thumb className="block w-4 h-4 rounded-full bg-white shadow" />
            </Slider.Root>
          </CompSlot>

          <CompSlot name="RadixProgress" category="Feedback">
            <Progress.Root value={66} className="relative overflow-hidden bg-zinc-800 rounded-full w-full h-2">
              <Progress.Indicator className="bg-emerald-500 h-full w-2/3" />
            </Progress.Root>
          </CompSlot>

          <CompSlot name="RadixAvatar" category="Display">
            <div className="flex items-center gap-2">
              <Avatar.Root className="w-8 h-8 rounded-full bg-indigo-600 inline-flex items-center justify-center text-xs font-semibold">
                <Avatar.Fallback>EX</Avatar.Fallback>
              </Avatar.Root>
              <span className="text-xs text-zinc-300">Erix</span>
            </div>
          </CompSlot>

          <CompSlot name="RadixSeparator" category="Layout">
            <div className="flex flex-col gap-1 text-xs">
              <span>Section A</span>
              <Separator.Root className="bg-zinc-700 h-px w-full" />
              <span>Section B</span>
            </div>
          </CompSlot>

          <CompSlot name="RadixScrollArea" category="Layout">
            <ScrollArea.Root className="h-14 w-full overflow-hidden rounded border border-zinc-800 p-1.5 text-xs">
              <ScrollArea.Viewport className="w-full h-full">
                <div>Row 1 · Steam</div>
                <div>Row 2 · Epic</div>
                <div>Row 3 · GOG</div>
                <div>Row 4 · Riot</div>
              </ScrollArea.Viewport>
            </ScrollArea.Root>
          </CompSlot>

          <CompSlot name="RadixTabs" category="Navigation" className="col-span-2">
            <Tabs.Root defaultValue="t1">
              <Tabs.List className="flex gap-1 border-b border-zinc-800 pb-1.5">
                <Tabs.Trigger value="t1" className="px-2.5 py-1 text-xs rounded data-[state=active]:bg-zinc-800">Overview</Tabs.Trigger>
                <Tabs.Trigger value="t2" className="px-2.5 py-1 text-xs rounded data-[state=active]:bg-zinc-800">Metrics</Tabs.Trigger>
              </Tabs.List>
              <Tabs.Content value="t1" className="pt-2 text-xs text-zinc-300">Radix tab overview content.</Tabs.Content>
              <Tabs.Content value="t2" className="pt-2 text-xs text-zinc-300">Radix tab metrics active.</Tabs.Content>
            </Tabs.Root>
          </CompSlot>

          <CompSlot name="RadixAccordion" category="Layout" className="col-span-2">
            <Accordion.Root type="single" defaultValue="item-1" collapsible className="flex flex-col gap-1">
              <Accordion.Item value="item-1" className="border border-zinc-800 rounded px-2 py-1">
                <Accordion.Header>
                  <Accordion.Trigger className="text-xs font-medium w-full text-left">DOM Compatibility</Accordion.Trigger>
                </Accordion.Header>
                <Accordion.Content className="text-xs text-zinc-400 pt-1">Requires portals, focus trap, Popper.</Accordion.Content>
              </Accordion.Item>
              <Accordion.Item value="item-2" className="border border-zinc-800 rounded px-2 py-1">
                <Accordion.Header>
                  <Accordion.Trigger className="text-xs font-medium w-full text-left">Pointer Events</Accordion.Trigger>
                </Accordion.Header>
                <Accordion.Content className="text-xs text-zinc-400 pt-1">Accordion item two opened.</Accordion.Content>
              </Accordion.Item>
            </Accordion.Root>
          </CompSlot>

          <CompSlot name="RadixCollapsible" category="Layout">
            <Collapsible.Root open={colOpen} onOpenChange={setColOpen}>
              <Collapsible.Trigger id="rx-col-btn" className="px-2.5 py-1 rounded bg-zinc-800 text-xs">
                {colOpen ? 'Hide' : 'Expand'}
              </Collapsible.Trigger>
              <Collapsible.Content className="text-xs text-zinc-400 pt-1">Collapsible body shown.</Collapsible.Content>
            </Collapsible.Root>
          </CompSlot>

          <CompSlot name="RadixDialog" category="Overlays">
            <Dialog.Root open={dlgOpen} onOpenChange={setDlgOpen}>
              <Dialog.Trigger id="rx-dlg-btn" className="px-2.5 py-1 rounded bg-blue-600 text-xs">
                Open Dialog
              </Dialog.Trigger>
              <Dialog.Portal>
                <Dialog.Overlay className="fixed inset-0 bg-black/60" />
                <Dialog.Content className="fixed top-20 left-20 bg-zinc-900 border border-zinc-700 p-4 rounded-xl">
                  <Dialog.Title className="text-sm font-semibold">Radix Dialog</Dialog.Title>
                  <Dialog.Description className="text-xs text-zinc-400">Radix dialog portal open.</Dialog.Description>
                </Dialog.Content>
              </Dialog.Portal>
            </Dialog.Root>
          </CompSlot>

          <CompSlot name="RadixAlertDialog" category="Overlays">
            <AlertDialog.Root>
              <AlertDialog.Trigger className="px-2.5 py-1 rounded bg-red-900/60 border border-red-700 text-xs">Alert</AlertDialog.Trigger>
              <AlertDialog.Portal>
                <AlertDialog.Content className="bg-zinc-900 p-4 rounded">Confirm action</AlertDialog.Content>
              </AlertDialog.Portal>
            </AlertDialog.Root>
          </CompSlot>

          <CompSlot name="RadixPopover" category="Overlays">
            <Popover.Root open={popOpen} onOpenChange={setPopOpen}>
              <Popover.Trigger id="rx-pop-btn" className="px-2.5 py-1 rounded bg-zinc-800 text-xs">Popover</Popover.Trigger>
              <Popover.Portal>
                <Popover.Content className="bg-zinc-900 border border-zinc-700 p-2 rounded text-xs">
                  Radix popover content
                </Popover.Content>
              </Popover.Portal>
            </Popover.Root>
          </CompSlot>

          <CompSlot name="RadixDropdownMenu" category="Overlays">
            <DropdownMenu.Root>
              <DropdownMenu.Trigger className="px-2.5 py-1 rounded bg-zinc-800 text-xs">Menu</DropdownMenu.Trigger>
              <DropdownMenu.Portal>
                <DropdownMenu.Content className="bg-zinc-900 border border-zinc-700 p-1 rounded text-xs">
                  <DropdownMenu.Item>Profile</DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu.Portal>
            </DropdownMenu.Root>
          </CompSlot>

          <CompSlot name="RadixSelect" category="Inputs">
            <Select.Root defaultValue="dx12">
              <Select.Trigger className="px-2 py-1 rounded bg-zinc-800 text-xs border border-zinc-700">
                <Select.Value />
              </Select.Trigger>
              <Select.Portal>
                <Select.Content className="bg-zinc-900 border border-zinc-700 rounded p-1 text-xs">
                  <Select.Viewport>
                    <Select.Item value="dx12"><Select.ItemText>DirectX 12</Select.ItemText></Select.Item>
                    <Select.Item value="vk"><Select.ItemText>Vulkan</Select.ItemText></Select.Item>
                  </Select.Viewport>
                </Select.Content>
              </Select.Portal>
            </Select.Root>
          </CompSlot>

          <CompSlot name="RadixHoverCard" category="Overlays">
            <HoverCard.Root>
              <HoverCard.Trigger className="text-xs underline text-blue-400">@exo</HoverCard.Trigger>
              <HoverCard.Portal>
                <HoverCard.Content className="bg-zinc-900 p-2 rounded text-xs">Exo Launcher</HoverCard.Content>
              </HoverCard.Portal>
            </HoverCard.Root>
          </CompSlot>

          <CompSlot name="RadixTooltip" category="Overlays">
            <Tooltip.Root>
              <Tooltip.Trigger className="px-2.5 py-1 rounded bg-zinc-800 text-xs">Tooltip</Tooltip.Trigger>
              <Tooltip.Portal>
                <Tooltip.Content className="bg-zinc-800 px-2 py-1 rounded text-xs">Helpful tip</Tooltip.Content>
              </Tooltip.Portal>
            </Tooltip.Root>
          </CompSlot>
        </div>
      </div>
    </Tooltip.Provider>
  );
}

resetCompStatuses();
createRoot(document.getElementById('root')!).render(<ShadcnRadixGallery />);

(globalThis as any).__trigger_interactions = () => {
  try { (document.querySelector('#rx-switch') as HTMLElement)?.click(); } catch {}
  try { (document.querySelector('#rx-check') as HTMLElement)?.click(); } catch {}
  try { (document.querySelector('#rx-toggle') as HTMLElement)?.click(); } catch {}
  try { (document.querySelector('#rx-col-btn') as HTMLElement)?.click(); } catch {}
  try { (document.querySelectorAll('[data-comp="RadixTabs"] [role="tab"]')[1] as HTMLElement)?.click(); } catch {}
  try { (document.querySelectorAll('[data-comp="RadixAccordion"] button')[1] as HTMLElement)?.click(); } catch {}
  try { document.getElementById('rx-dlg-btn')?.click(); } catch {}
};

(globalThis as any).__run_interactions = () => {
  const res: Record<string, boolean> = {};
  res.RadixSwitch = Boolean(document.querySelector('[data-comp="RadixSwitch"]')?.textContent?.includes('On'));
  res.RadixCheckbox = Boolean(document.querySelector('[data-comp="RadixCheckbox"]')?.textContent?.includes('Checked'));
  res.RadixToggle = Boolean(document.querySelector('[data-comp="RadixToggle"]')?.textContent?.includes('Pressed'));
  res.RadixCollapsible = Boolean(document.querySelector('[data-comp="RadixCollapsible"]')?.textContent?.includes('Hide'));
  res.RadixTabs = Boolean(document.querySelector('[data-comp="RadixTabs"]')?.textContent?.includes('metrics active'));
  res.RadixAccordion = Boolean(document.querySelector('[data-comp="RadixAccordion"]')?.textContent?.includes('item two opened'));
  res.RadixDialog = Boolean(document.body.textContent?.includes('Radix dialog portal open'));
  return collectComponentMetrics(res);
};
