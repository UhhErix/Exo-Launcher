import React, { useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Accordion,
  Alert,
  Avatar,
  Badge,
  Breadcrumbs,
  Button,
  ButtonGroup,
  Card,
  Checkbox,
  Chip,
  CloseButton,
  Disclosure,
  EmptyState,
  Input,
  Kbd,
  Label,
  Link,
  Meter,
  Modal,
  NumberField,
  Pagination,
  Popover,
  ProgressBar,
  ProgressCircle,
  Radio,
  RadioGroup,
  Separator,
  Skeleton,
  Slider,
  Spinner,
  Switch,
  Table,
  Tabs,
  Tag,
  TagGroup,
  TextArea,
  ToggleButton,
  Tooltip,
} from '@heroui/react';
import { CompSlot, collectComponentMetrics, resetCompStatuses } from './ErrorCard';

function HeroUiGallery() {
  const [clicks, setClicks] = useState(0);
  const [swOn, setSwOn] = useState(false);
  const [checked, setChecked] = useState(false);
  const [toggleOn, setToggleOn] = useState(false);
  const [radioVal, setRadioVal] = useState('dx12');
  const [sliderVal, setSliderVal] = useState(64);
  const [modalOpen, setModalOpen] = useState(false);
  const [popOpen, setPopOpen] = useState(false);

  return (
    <div className="w-[1280px] min-h-[800px] bg-[#050508] text-zinc-100 p-4 flex flex-col gap-3">
      <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
        <div className="text-base font-semibold">HeroUI v3.2.8 Real Component Gallery (Unmodified)</div>
        <div className="text-xs text-zinc-400">Tailwind v4 + react-aria-components + @heroui/styles</div>
      </div>

      <div className="grid grid-cols-6 gap-2.5">
        <CompSlot name="Button" category="Inputs">
          <div className="flex gap-1.5 flex-wrap">
            <Button id="hero-btn" variant="primary" size="sm" onPress={() => setClicks((c) => c + 1)}>
              Clicks: {clicks}
            </Button>
            <Button variant="secondary" size="sm">Sec</Button>
            <Button variant="danger" size="sm">Del</Button>
          </div>
        </CompSlot>

        <CompSlot name="ButtonGroup" category="Inputs">
          <ButtonGroup size="sm" variant="secondary">
            <Button>One</Button>
            <Button>Two</Button>
          </ButtonGroup>
        </CompSlot>

        <CompSlot name="ToggleButton" category="Inputs">
          <ToggleButton id="hero-toggle" isSelected={toggleOn} onChange={setToggleOn} size="sm">
            {toggleOn ? 'Active' : 'Toggle'}
          </ToggleButton>
        </CompSlot>

        <CompSlot name="CloseButton" category="Inputs">
          <div className="flex items-center gap-2">
            <CloseButton aria-label="Close" />
            <span className="text-xs text-zinc-400">Dismiss</span>
          </div>
        </CompSlot>

        <CompSlot name="Switch" category="Inputs">
          <Switch id="hero-switch" isSelected={swOn} onChange={setSwOn}>
            <Switch.Control>
              <Switch.Thumb />
            </Switch.Control>
            <Label className="text-xs">{swOn ? 'Enabled' : 'Disabled'}</Label>
          </Switch>
        </CompSlot>

        <CompSlot name="Checkbox" category="Inputs">
          <Checkbox id="hero-check" isSelected={checked} onChange={setChecked}>
            <Checkbox.Control>
              <Checkbox.Indicator />
            </Checkbox.Control>
            <Checkbox.Content>
              <Label className="text-xs">{checked ? 'Checked' : 'Opt in'}</Label>
            </Checkbox.Content>
          </Checkbox>
        </CompSlot>

        <CompSlot name="RadioGroup" category="Inputs">
          <RadioGroup value={radioVal} onChange={setRadioVal} aria-label="Backend" orientation="horizontal">
            <Radio value="dx12">
              <Radio.Control><Radio.Indicator /></Radio.Control>
              <Radio.Content><Label className="text-xs">DX12</Label></Radio.Content>
            </Radio>
            <Radio value="vk">
              <Radio.Control><Radio.Indicator /></Radio.Control>
              <Radio.Content><Label className="text-xs">VK</Label></Radio.Content>
            </Radio>
          </RadioGroup>
        </CompSlot>

        <CompSlot name="Slider" category="Inputs">
          <Slider value={sliderVal} onChange={(v) => setSliderVal(Array.isArray(v) ? v[0] : v)} minValue={0} maxValue={100}>
            <Label className="text-xs">FPS Cap: {sliderVal}</Label>
            <Slider.Track>
              <Slider.Fill />
              <Slider.Thumb />
            </Slider.Track>
          </Slider>
        </CompSlot>

        <CompSlot name="Input" category="Forms">
          <Input placeholder="Search library..." aria-label="Search" />
        </CompSlot>

        <CompSlot name="TextArea" category="Forms">
          <TextArea placeholder="Notes..." rows={2} aria-label="Notes" />
        </CompSlot>

        <CompSlot name="NumberField" category="Forms">
          <NumberField defaultValue={144} aria-label="Refresh">
            <NumberField.Group>
              <NumberField.DecrementButton />
              <NumberField.Input />
              <NumberField.IncrementButton />
            </NumberField.Group>
          </NumberField>
        </CompSlot>

        <CompSlot name="Chip" category="Display">
          <div className="flex gap-1.5 flex-wrap">
            <Chip color="accent" size="sm">DX12</Chip>
            <Chip color="success" size="sm">Native</Chip>
            <Chip color="warning" size="sm">Lab</Chip>
          </div>
        </CompSlot>

        <CompSlot name="Avatar" category="Display">
          <div className="flex items-center gap-2">
            <Avatar size="sm"><Avatar.Fallback>EX</Avatar.Fallback></Avatar>
            <Avatar size="sm"><Avatar.Fallback>RN</Avatar.Fallback></Avatar>
          </div>
        </CompSlot>

        <CompSlot name="Badge" category="Display">
          <div className="flex items-center gap-3">
            <Badge.Anchor>
              <Avatar size="sm"><Avatar.Fallback>U</Avatar.Fallback></Avatar>
              <Badge color="success" size="sm">3</Badge>
            </Badge.Anchor>
            <span className="text-xs text-zinc-400">Online</span>
          </div>
        </CompSlot>

        <CompSlot name="Kbd" category="Display">
          <div className="flex items-center gap-1.5">
            <Kbd><Kbd.Abbr keyValue="command" /><Kbd.Content>K</Kbd.Content></Kbd>
            <span className="text-xs text-zinc-400">Palette</span>
          </div>
        </CompSlot>

        <CompSlot name="Link" category="Display">
          <Link href="#exo" className="text-xs">getexo.app <Link.Icon /></Link>
        </CompSlot>

        <CompSlot name="Spinner" category="Feedback">
          <div className="flex items-center gap-2">
            <Spinner size="sm" />
            <span className="text-xs text-zinc-400">Syncing</span>
          </div>
        </CompSlot>

        <CompSlot name="Skeleton" category="Feedback">
          <div className="flex flex-col gap-1.5">
            <Skeleton className="h-3 w-full rounded" />
            <Skeleton className="h-3 w-2/3 rounded" />
          </div>
        </CompSlot>

        <CompSlot name="ProgressBar" category="Feedback">
          <ProgressBar value={72} aria-label="Download">
            <ProgressBar.Track><ProgressBar.Fill /></ProgressBar.Track>
          </ProgressBar>
        </CompSlot>

        <CompSlot name="ProgressCircle" category="Feedback">
          <div className="flex items-center gap-2">
            <ProgressCircle value={68} size="sm" aria-label="Score">
              <ProgressCircle.Track>
                <ProgressCircle.TrackCircle />
                <ProgressCircle.FillCircle />
              </ProgressCircle.Track>
            </ProgressCircle>
            <span className="text-xs">68%</span>
          </div>
        </CompSlot>

        <CompSlot name="Meter" category="Feedback">
          <Meter value={42} aria-label="VRAM">
            <Meter.Track><Meter.Fill /></Meter.Track>
          </Meter>
        </CompSlot>

        <CompSlot name="Separator" category="Layout">
          <div className="flex flex-col gap-1 text-xs">
            <span>Top</span>
            <Separator />
            <span>Bottom</span>
          </div>
        </CompSlot>

        <CompSlot name="Breadcrumbs" category="Navigation">
          <Breadcrumbs>
            <Breadcrumbs.Item>Exo</Breadcrumbs.Item>
            <Breadcrumbs.Item>Library</Breadcrumbs.Item>
          </Breadcrumbs>
        </CompSlot>

        <CompSlot name="TagGroup" category="Display">
          <TagGroup aria-label="Tags">
            <TagGroup.List>
              <Tag id="fps">FPS</Tag>
              <Tag id="hdr">HDR</Tag>
            </TagGroup.List>
          </TagGroup>
        </CompSlot>

        <CompSlot name="Card" category="Layout" className="col-span-2">
          <Card variant="secondary">
            <Card.Header>
              <Card.Title className="text-xs">Counter-Strike 2</Card.Title>
              <Card.Description className="text-[11px]">Steam · 412 hrs played</Card.Description>
            </Card.Header>
            <Card.Content className="text-xs text-zinc-400">Ready to launch with Reflex + Low Latency.</Card.Content>
          </Card>
        </CompSlot>

        <CompSlot name="Alert" category="Feedback" className="col-span-2">
          <Alert status="accent">
            <Alert.Indicator />
            <Alert.Content>
              <Alert.Title className="text-xs">Shader cache warmed</Alert.Title>
              <Alert.Description className="text-[11px]">DX12 PSO compilation finished in 110ms.</Alert.Description>
            </Alert.Content>
          </Alert>
        </CompSlot>

        <CompSlot name="Tabs" category="Navigation" className="col-span-2">
          <Tabs defaultSelectedKey="lib">
            <Tabs.ListContainer>
              <Tabs.List aria-label="Rooms">
                <Tabs.Tab id="lib">Library<Tabs.Indicator /></Tabs.Tab>
                <Tabs.Tab id="store">Store<Tabs.Indicator /></Tabs.Tab>
                <Tabs.Tab id="pc">PC<Tabs.Indicator /></Tabs.Tab>
              </Tabs.List>
            </Tabs.ListContainer>
            <Tabs.Panel id="lib" className="text-xs text-zinc-300 pt-1">215 games indexed.</Tabs.Panel>
            <Tabs.Panel id="store" className="text-xs text-zinc-300 pt-1">Steam top sellers.</Tabs.Panel>
            <Tabs.Panel id="pc" className="text-xs text-zinc-300 pt-1">RTX 3070 · Ryzen 5600X.</Tabs.Panel>
          </Tabs>
        </CompSlot>

        <CompSlot name="Accordion" category="Layout" className="col-span-2">
          <Accordion defaultExpandedKeys={['a1']}>
            <Accordion.Item id="a1">
              <Accordion.Heading>
                <Accordion.Trigger className="text-xs">Renderer<Accordion.Indicator /></Accordion.Trigger>
              </Accordion.Heading>
              <Accordion.Panel><Accordion.Body className="text-xs text-zinc-400">Stylo + Taffy + Vello.</Accordion.Body></Accordion.Panel>
            </Accordion.Item>
            <Accordion.Item id="a2">
              <Accordion.Heading>
                <Accordion.Trigger className="text-xs">Memory<Accordion.Indicator /></Accordion.Trigger>
              </Accordion.Heading>
              <Accordion.Panel><Accordion.Body className="text-xs text-zinc-400">0 child processes.</Accordion.Body></Accordion.Panel>
            </Accordion.Item>
          </Accordion>
        </CompSlot>

        <CompSlot name="Disclosure" category="Layout">
          <Disclosure>
            <Disclosure.Heading>
              <Disclosure.Trigger className="text-xs">Advanced <Disclosure.Indicator /></Disclosure.Trigger>
            </Disclosure.Heading>
            <Disclosure.Content><Disclosure.Body className="text-xs">Flags ready</Disclosure.Body></Disclosure.Content>
          </Disclosure>
        </CompSlot>

        <CompSlot name="Modal" category="Overlays">
          <Modal isOpen={modalOpen} onOpenChange={setModalOpen}>
            <Button id="hero-modal-trigger" size="sm" variant="secondary" onPress={() => setModalOpen(true)}>
              Open Modal
            </Button>
            <Modal.Backdrop>
              <Modal.Container>
                <Modal.Dialog>
                  <Modal.Header><Modal.Heading>HeroUI Modal</Modal.Heading></Modal.Header>
                  <Modal.Body>Portal overlay active.</Modal.Body>
                  <Modal.Footer><Button size="sm" onPress={() => setModalOpen(false)}>Close</Button></Modal.Footer>
                </Modal.Dialog>
              </Modal.Container>
            </Modal.Backdrop>
          </Modal>
        </CompSlot>

        <CompSlot name="Popover" category="Overlays">
          <Popover isOpen={popOpen} onOpenChange={setPopOpen}>
            <Popover.Trigger>
              <Button id="hero-pop-trigger" size="sm" variant="secondary" onPress={() => setPopOpen((v) => !v)}>
                Popover
              </Button>
            </Popover.Trigger>
            <Popover.Content>
              <Popover.Dialog>
                <Popover.Heading className="text-xs">Quick info</Popover.Heading>
                <div className="text-xs">Floating popover body</div>
              </Popover.Dialog>
            </Popover.Content>
          </Popover>
        </CompSlot>

        <CompSlot name="Tooltip" category="Overlays">
          <Tooltip>
            <Button size="sm" variant="ghost">Hover tip</Button>
            <Tooltip.Content>Tooltip label</Tooltip.Content>
          </Tooltip>
        </CompSlot>

        <CompSlot name="Pagination" category="Navigation" className="col-span-2">
          <Pagination>
            <Pagination.Content>
              <Pagination.Item><Pagination.Previous><Pagination.PreviousIcon /></Pagination.Previous></Pagination.Item>
              <Pagination.Item><Pagination.Link isActive>1</Pagination.Link></Pagination.Item>
              <Pagination.Item><Pagination.Link>2</Pagination.Link></Pagination.Item>
              <Pagination.Item><Pagination.Next><Pagination.NextIcon /></Pagination.Next></Pagination.Item>
            </Pagination.Content>
          </Pagination>
        </CompSlot>

        <CompSlot name="Table" category="Display" className="col-span-2">
          <Table aria-label="Benchmarks">
            <Table.ScrollContainer>
              <Table.Content>
                <Table.Header>
                  <Table.Column isRowHeader>Engine</Table.Column>
                  <Table.Column>RAM</Table.Column>
                </Table.Header>
                <Table.Body>
                  <Table.Row id="r1"><Table.Cell>Rune</Table.Cell><Table.Cell>145 MB</Table.Cell></Table.Row>
                  <Table.Row id="r2"><Table.Cell>WebView2</Table.Cell><Table.Cell>534 MB</Table.Cell></Table.Row>
                </Table.Body>
              </Table.Content>
            </Table.ScrollContainer>
          </Table>
        </CompSlot>

        <CompSlot name="EmptyState" category="Display" className="col-span-2">
          <EmptyState className="py-1">
            <div className="text-xs text-zinc-400">No active downloads</div>
          </EmptyState>
        </CompSlot>
      </div>
    </div>
  );
}

resetCompStatuses();
const root = document.getElementById('root')!;
createRoot(root).render(<HeroUiGallery />);

(globalThis as any).__trigger_interactions = () => {
  try { document.getElementById('hero-btn')?.click(); } catch {}
  try { (document.querySelector('[data-comp="Switch"] input, [data-comp="Switch"] button, #hero-switch') as HTMLElement)?.click(); } catch {}
  try { (document.querySelector('[data-comp="Checkbox"] input, #hero-check') as HTMLElement)?.click(); } catch {}
  try { (document.querySelectorAll('[data-comp="Tabs"] [role="tab"]')[1] as HTMLElement)?.click(); } catch {}
  try { (document.querySelectorAll('[data-comp="Accordion"] button')[1] as HTMLElement)?.click(); } catch {}
  try { document.getElementById('hero-modal-trigger')?.click(); } catch {}
};

(globalThis as any).__run_interactions = () => {
  const res: Record<string, boolean> = {};
  res.Button = Boolean(document.getElementById('hero-btn')?.textContent?.includes('Clicks: 1'));
  res.Switch = Boolean(document.querySelector('[data-comp="Switch"]')?.textContent?.includes('Enabled'));
  res.Checkbox = Boolean(document.querySelector('[data-comp="Checkbox"]')?.textContent?.includes('Checked'));
  res.Tabs = Boolean(document.querySelector('[data-comp="Tabs"]')?.textContent?.includes('Steam top sellers'));
  res.Accordion = Boolean(document.querySelector('[data-comp="Accordion"]')?.textContent?.includes('0 child processes'));
  res.Modal = Boolean(document.body.textContent?.includes('Portal overlay active'));
  return collectComponentMetrics(res);
};
