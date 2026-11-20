import type { DeviceDriver, SupportedClient } from "./registry.ts";

interface LazyDriver extends DeviceDriver {
  brandOf?(client: SupportedClient): string;
}

interface DriverSlot {
  readonly order: number;
  readonly vids: readonly number[];
  readonly load: () => Promise<readonly LazyDriver[]>;
  cached?: Promise<readonly LazyDriver[]>;
}

const slot = (order: number, vids: readonly number[], load: () => Promise<readonly LazyDriver[]>): DriverSlot =>
  ({ order, vids, load });

/**
 * Lazy driver catalog preserving the exact matching order of `DEVICE_DRIVERS` in `registry.ts`.
 * Each entry dynamically imports only the driver module(s) for connected USB vendor IDs so initial page load avoids
 * parsing/executing ~1.3 MB of unused mouse protocols.
 */
const DRIVER_SLOTS: readonly DriverSlot[] = [
  slot(0, [0x258a], async () => {
    const { AulaSc380ProHidClient } = await import("./aula/hid.ts");
    return [{ brand: "AULA", supports: (d) => AulaSc380ProHidClient.isSupported(d), create: (d) => new AulaSc380ProHidClient(d), score: () => 9 }];
  }),
  slot(1, [0x1b80], async () => {
    const { RedgearX13HidClient } = await import("./redgear/hid.ts");
    return [{ brand: "Redgear", supports: (d) => RedgearX13HidClient.isSupported(d), create: (d) => new RedgearX13HidClient(d), score: () => 7 }];
  }),
  slot(2, [0x372e], async () => {
    const { BytechHidClient } = await import("./bytech/hid.ts");
    return [{ brand: "IPI", supports: (d) => BytechHidClient.isSupported(d), create: (d) => new BytechHidClient(d), score: () => 9 }];
  }),
  slot(3, [0x0b05], async () => {
    const { AsusHidClient } = await import("./asus/hid.ts");
    return [{ brand: "ASUS", supports: (d) => AsusHidClient.isSupported(d), create: (d) => new AsusHidClient(d), score: () => 10 }];
  }),
  slot(4, [0x260d], async () => {
    const { DareuHidClient } = await import("./dareu/hid.ts");
    return [{ brand: "Dareu", supports: (d) => DareuHidClient.isSupported(d), create: (d) => new DareuHidClient(d), score: () => 9 }];
  }),
  slot(5, [0x04d9], async () => {
    const { RedragonHidClient } = await import("./redragon/hid.ts");
    return [{ brand: "Redragon", supports: (d) => RedragonHidClient.isSupported(d), create: (d) => new RedragonHidClient(d), score: () => 9 }];
  }),
  slot(6, [0x258a], async () => {
    const { RedragonM690ProHidClient } = await import("./redragon/m690-pro-hid.ts");
    return [{ brand: "Redragon", supports: (d) => RedragonM690ProHidClient.isSupported(d), create: (d) => new RedragonM690ProHidClient(d), score: () => 9 }];
  }),
  slot(7, [0x25a7], async () => {
    const { RedragonM719ProHidClient } = await import("./redragon/m719-pro-hid.ts");
    return [{ brand: "Redragon", supports: (d) => RedragonM719ProHidClient.isSupported(d), create: (d) => new RedragonM719ProHidClient(d), score: (d) => RedragonM719ProHidClient.supportScore(d) }];
  }),
  slot(8, [0x04d9], async () => {
    const { FaterHidClient } = await import("./fater/hid.ts");
    return [{ brand: "Fater", supports: (d) => FaterHidClient.isSupported(d), create: (d) => new FaterHidClient(d), score: () => 7 }];
  }),
  slot(9, [0x0bda], async () => {
    const { MotospeedHidClient } = await import("./motospeed/hid.ts");
    return [{ brand: "Motospeed", supports: (d) => MotospeedHidClient.isSupported(d), create: (d) => new MotospeedHidClient(d), score: () => 7 }];
  }),
  slot(10, [0x0483], async () => {
    const { ZaunkoenigHidClient } = await import("./zaunkoenig/hid.ts");
    return [{ brand: "Zaunkoenig", supports: (d) => ZaunkoenigHidClient.isSupported(d), create: (d) => new ZaunkoenigHidClient(d), score: () => 10 }];
  }),
  slot(11, [0x1b1c], async () => {
    const { CorsairHidClient } = await import("./corsair/hid.ts");
    return [{ brand: "Corsair", supports: (d) => CorsairHidClient.isSupported(d), create: (d) => new CorsairHidClient(d), score: () => 8 }];
  }),
  slot(12, [0x1b1c], async () => {
    const { CorsairBragiHidClient } = await import("./corsair/bragi-hid.ts");
    return [{ brand: "Corsair", supports: (d) => CorsairBragiHidClient.isSupported(d), create: (d) => new CorsairBragiHidClient(d), score: () => 8 }];
  }),
  slot(13, [0x361d], async () => {
    const { FinalmouseHidClient } = await import("./finalmouse/hid.ts");
    return [{ brand: "Finalmouse", supports: (d) => FinalmouseHidClient.isSupported(d), create: (d) => new FinalmouseHidClient(d), score: () => 10 }];
  }),
  slot(14, [0x3367], async () => {
    const { EggOp1HidClient } = await import("./endgame/egg-op1-hid.ts");
    return [{ brand: "Endgame Gear", supports: (d) => EggOp1HidClient.isSupported(d), create: (d) => new EggOp1HidClient(d), score: () => 100 }];
  }),
  slot(15, [0x3367], async () => {
    const { EggOtHidClient } = await import("./endgame/egg-ot-hid.ts");
    return [{ brand: "Endgame Gear", supports: (d) => EggOtHidClient.isSupported(d), create: (d) => new EggOtHidClient(d), score: () => 100 }];
  }),
  slot(16, [0x3367], async () => {
    const { eggWeCreate, eggWeIsSupported, eggWeSupportScore } = await import("./endgame/egg-we-control.ts");
    return [{ brand: "Endgame Gear", supports: eggWeIsSupported, create: eggWeCreate, score: eggWeSupportScore }];
  }),
  slot(17, [0x3710], async () => {
    const { PulsarXs1HidClient } = await import("./pulsar/pulsar-xs1-hid.ts");
    return [{ brand: "Pulsar", supports: (d) => PulsarXs1HidClient.isSupported(d), create: (d) => new PulsarXs1HidClient(d), score: () => 8 }];
  }),
  slot(18, [0x3710], async () => {
    const { PulsarProHidClient } = await import("./pulsar/pulsar-pro-hid.ts");
    return [{ brand: "Pulsar", supports: (d) => PulsarProHidClient.isSupported(d), create: (d) => new PulsarProHidClient(d), score: () => 8 }];
  }),
  slot(19, [0x25a7], async () => {
    const { PulsarAresonHidClient } = await import("./pulsar/pulsar-areson-hid.ts");
    return [{ brand: "Pulsar", supports: (d) => PulsarAresonHidClient.isSupported(d), create: (d) => new PulsarAresonHidClient(d), score: () => 8 }];
  }),
  slot(20, [0x3554], async () => {
    const { GravaStarHidClient } = await import("./gravastar/hid.ts");
    return [{ brand: "GravaStar", supports: (d) => GravaStarHidClient.isSupported(d), create: (d) => new GravaStarHidClient(d), score: () => 7 }];
  }),
  slot(21, [0x3710, 0x3554], async () => {
    const { PulsarHidClient } = await import("./pulsar/pulsar-hid.ts");
    return [{ brand: "Pulsar", supports: (d) => PulsarHidClient.isSupported(d), create: (d) => new PulsarHidClient(d), score: () => 7 }];
  }),
  slot(22, [0x3554], async () => {
    const { TeevolutionHidClient } = await import("./teevolution/hid.ts");
    return [{ brand: "Teevolution", supports: (d) => TeevolutionHidClient.isSupported(d), create: (d) => new TeevolutionHidClient(d), score: () => 7 }];
  }),
  slot(23, [0x3554], async () => {
    const { VgnF2HidClient } = await import("./vgn/hid.ts");
    return [{ brand: "VGN", supports: (d) => VgnF2HidClient.isSupported(d), create: (d) => new VgnF2HidClient(d), score: () => 7 }];
  }),
  slot(24, [0x3057], async () => {
    const { VaxeeHidClient } = await import("./vaxee/hid.ts");
    return [{ brand: "VAXEE", supports: (d) => VaxeeHidClient.isSupported(d), create: (d) => new VaxeeHidClient(d), score: () => 7 }];
  }),
  slot(25, [0x046d], async () => {
    const { LogitechHidppClient } = await import("./logitech/hidpp.ts");
    return [{ brand: "Logitech", supports: (d) => LogitechHidppClient.isSupported(d), create: (d) => new LogitechHidppClient(d), score: (d) => LogitechHidppClient.supportScore(d) }];
  }),
  slot(26, [0x36a7], async () => {
    const { WLMouseBeastX4kHidClient } = await import("./wlmouse/beast-x-4k-hid.ts");
    return [{ brand: "WLMouse", supports: (d) => WLMouseBeastX4kHidClient.isSupported(d), create: (d) => new WLMouseBeastX4kHidClient(d), score: () => 6 }];
  }),
  slot(27, [0x36a7], async () => {
    const { WLMouseHidClient } = await import("./wlmouse/hid.ts");
    return [{ brand: "WLMouse", supports: (d) => WLMouseHidClient.isSupported(d), create: (d) => new WLMouseHidClient(d), score: () => 5 }];
  }),
  slot(28, [0x373e, 0x37b0], async () => {
    const { LamzuHidClient } = await import("./lamzu/hid.ts");
    return [{
      brand: "Lamzu",
      supports: (d) => LamzuHidClient.isSupported(d),
      create: (d) => new LamzuHidClient(d),
      score: () => 5,
      brandOf: (client) => (client as InstanceType<typeof LamzuHidClient>).deviceBrand(),
    }];
  }),
  slot(29, [0x3554], async () => {
    const { NoirM1NexHidClient } = await import("./noir/m1-nex-hid.ts");
    return [{ brand: "Noir Gear", supports: (d) => NoirM1NexHidClient.isSupported(d), create: (d) => new NoirM1NexHidClient(d), score: () => 8 }];
  }),
  slot(30, [0x3554], async () => {
    const { LamzuAtlantisHidClient } = await import("./lamzu-atlantis/hid.ts");
    return [{ brand: "Lamzu", supports: (d) => LamzuAtlantisHidClient.isSupported(d), create: (d) => new LamzuAtlantisHidClient(d), score: () => 5 }];
  }),
  slot(31, [0x3554], async () => {
    const { ScyroxHidClient } = await import("./scyrox/hid.ts");
    return [{ brand: "Scyrox", supports: (d) => ScyroxHidClient.isSupported(d), create: (d) => new ScyroxHidClient(d), score: () => 6 }];
  }),
  slot(32, [0x2fe3], async () => {
    const { ModdoHidClient } = await import("./moddo/hid.ts");
    return [{ brand: "moddoMOUSE", supports: (d) => ModdoHidClient.isSupported(d), create: (d) => new ModdoHidClient(d), score: () => 5 }];
  }),
  slot(33, [0x1915, 0x093a], async () => {
    const { NinjutsoHidClient } = await import("./ninjutso/hid.ts");
    return [{ brand: "Ninjutso", supports: (d) => NinjutsoHidClient.isSupported(d), create: (d) => new NinjutsoHidClient(d), score: () => 7 }];
  }),
  slot(34, [0x1915], async () => {
    const { OrbitalHidClient } = await import("./orbital/hid.ts");
    return [{ brand: "Orbital", supports: (d) => OrbitalHidClient.isSupported(d), create: (d) => new OrbitalHidClient(d), score: () => 6 }];
  }),
  slot(35, [0x1915], async () => {
    const { RawmHidClient } = await import("./rawm/hid.ts");
    return [{ brand: "RAWM", supports: (d) => RawmHidClient.isSupported(d), create: (d) => new RawmHidClient(d), score: () => 7 }];
  }),
  slot(36, [0x1532], async () => {
    const { RazerHidClient } = await import("./razer/hid.ts");
    return [{ brand: "Razer", supports: (d) => RazerHidClient.isSupported(d), create: (d) => new RazerHidClient(d), score: () => 6 }];
  }),
  slot(37, [0x1532], async () => {
    const { RazerCobraHidClient } = await import("./razer/cobra-hid.ts");
    return [{ brand: "Razer", supports: (d) => RazerCobraHidClient.isSupported(d), create: (d) => new RazerCobraHidClient(d), score: () => 6 }];
  }),
  slot(38, [0x1532], async () => {
    const { RazerViperMiniHidClient } = await import("./razer/viper-mini-hid.ts");
    return [{ brand: "Razer", supports: (d) => RazerViperMiniHidClient.isSupported(d), create: (d) => new RazerViperMiniHidClient(d), score: () => 6 }];
  }),
  slot(39, [0x1532], async () => {
    const { RazerViperHidClient } = await import("./razer/viper-hid.ts");
    return [{ brand: "Razer", supports: (d) => RazerViperHidClient.isSupported(d), create: (d) => new RazerViperHidClient(d), score: () => 6 }];
  }),
  slot(40, [0x373b], async () => {
    const { AtkBitmouseHidClient } = await import("./atk/bitmouse-hid.ts");
    return [{ brand: "ATK", supports: (d) => AtkBitmouseHidClient.isSupported(d), create: (d) => new AtkBitmouseHidClient(d), score: () => 7 }];
  }),
  slot(41, [0x3554, 0x373b], async () => {
    const { AtkHidClient } = await import("./atk/hid.ts");
    return [{
      brand: "ATK",
      supports: (d) => AtkHidClient.isSupported(d),
      create: (d) => new AtkHidClient(d),
      score: () => 5,
      brandOf: (client) => (client as InstanceType<typeof AtkHidClient>).deviceBrand(),
    }];
  }),
  slot(42, [0x1d57], async () => {
    const { DeluxHidClient } = await import("./delux/hid.ts");
    return [{
      brand: "Delux",
      supports: (d) => DeluxHidClient.isSupported(d),
      create: (d) => new DeluxHidClient(d),
      score: () => 6,
      brandOf: (client) => (client as InstanceType<typeof DeluxHidClient>).deviceBrand(),
    }];
  }),
  slot(43, [0x1d57, 0x25a7, 0x373e], async () => {
    const { AttackSharkHidClient } = await import("./attackshark/hid.ts");
    return [{
      brand: "Attack Shark",
      supports: (d) => AttackSharkHidClient.isSupported(d),
      create: (d) => new AttackSharkHidClient(d),
      score: () => 5,
      brandOf: (client) => (client as InstanceType<typeof AttackSharkHidClient>).deviceBrand(),
    }];
  }),
  slot(44, [0x1532], async () => {
    const { RazerViperV4ProHidClient } = await import("./razer/viper-v4-pro-hid.ts");
    return [{ brand: "Razer", supports: (d) => RazerViperV4ProHidClient.isSupported(d), create: (d) => new RazerViperV4ProHidClient(d), score: () => 7 }];
  }),
  slot(45, [0x3434], async () => {
    const { Keychron8kHidClient } = await import("./keychron/mouse-8k-hid.ts");
    return [{ brand: "Keychron", supports: (d) => Keychron8kHidClient.isSupported(d), create: (d) => new Keychron8kHidClient(d), score: () => 7 }];
  }),
  slot(46, [0x3434], async () => {
    const { Keychron1kHidClient } = await import("./keychron/mouse-1k-hid.ts");
    return [{ brand: "Keychron", supports: (d) => Keychron1kHidClient.isSupported(d), create: (d) => new Keychron1kHidClient(d), score: () => 7 }];
  }),
  slot(47, [0x3434], async () => {
    const { Keychron4kHidClient } = await import("./keychron/mouse-4k-hid.ts");
    return [{ brand: "Keychron", supports: (d) => Keychron4kHidClient.isSupported(d), create: (d) => new Keychron4kHidClient(d), score: () => 7 }];
  }),
  slot(48, [0x3434], async () => {
    const { Keychron8kNordicHidClient } = await import("./keychron/mouse-8k-nordic-hid.ts");
    return [{ brand: "Keychron", supports: (d) => Keychron8kNordicHidClient.isSupported(d), create: (d) => new Keychron8kNordicHidClient(d), score: () => 7 }];
  }),
  slot(49, [0x3434], async () => {
    const { KeychronNapeHidClient } = await import("./keychron/nape-hid.ts");
    return [{ brand: "Keychron", supports: (d) => KeychronNapeHidClient.isSupported(d), create: (d) => new KeychronNapeHidClient(d), score: () => 6 }];
  }),
  slot(50, [0x3151], async () => {
    const { GearHubHidClient } = await import("./gearhub/hid.ts");
    return [{ brand: "Lingbao", supports: (d) => GearHubHidClient.isSupported(d), create: (d) => new GearHubHidClient(d), score: () => 7 }];
  }),
  slot(51, [0x3151], async () => {
    const { FantechHidClient } = await import("./fantech/hid.ts");
    return [{ brand: "Fantech", supports: (d) => FantechHidClient.isSupported(d), create: (d) => new FantechHidClient(d), score: () => 5 }];
  }),
  slot(52, [0x31e3], async () => {
    const { WootingHidClient } = await import("./wooting/hid.ts");
    return [{ brand: "Wooting", supports: (d) => WootingHidClient.isSupported(d), create: (d) => new WootingHidClient(d), score: () => 6 }];
  }),
  slot(53, [0x3879], async () => {
    const { WallhackMouseHidClient } = await import("./wallhack/mouse-hid.ts");
    return [{ brand: "WALLHACK", supports: (d) => WallhackMouseHidClient.isSupported(d), create: (d) => new WallhackMouseHidClient(d), score: () => 8 }];
  }),
  slot(54, [0x3879, 0x1caa], async () => {
    const { WallhackKeyboardHidClient } = await import("./wallhack/keyboard-hid.ts");
    return [{ brand: "WALLHACK", supports: (d) => WallhackKeyboardHidClient.isSupported(d), create: (d) => new WallhackKeyboardHidClient(d), score: () => 8 }];
  }),
  slot(55, [0x33e4], async () => {
    const { GWolvesHidClient } = await import("./gwolves/hid.ts");
    return [{ brand: "G-Wolves", supports: (d) => GWolvesHidClient.isSupported(d), create: (d) => new GWolvesHidClient(d), score: () => 7 }];
  }),
  slot(56, [0x33e4], async () => {
    const { GWolvesXviHidClient } = await import("./gwolves/xvi-hid.ts");
    return [{ brand: "G-Wolves", supports: (d) => GWolvesXviHidClient.isSupported(d), create: (d) => new GWolvesXviHidClient(d), score: () => 7 }];
  }),
  slot(57, [0x33e4], async () => {
    const { GWolvesXviNewHidClient } = await import("./gwolves/xvi-new-hid.ts");
    return [{ brand: "G-Wolves", supports: (d) => GWolvesXviNewHidClient.isSupported(d), create: (d) => new GWolvesXviNewHidClient(d), score: () => 7 }];
  }),
  slot(58, [0x1038], async () => {
    const { STEELSERIES_DRIVERS } = await import("./steelseries-drivers.ts");
    return STEELSERIES_DRIVERS;
  }),
  slot(82, [0x093a], async () => {
    const { GloriousHidClient } = await import("./glorious/hid.ts");
    return [{ brand: "Glorious", supports: (d) => GloriousHidClient.isSupported(d), create: (d) => new GloriousHidClient(d), score: () => 5 }];
  }),
  slot(83, [0x258a, 0x22d4, 0x320f, 0x3794], async () => {
    const { GloriousClassicHidClient } = await import("./glorious/classic-hid.ts");
    return [{ brand: "Glorious", supports: (d) => GloriousClassicHidClient.isSupported(d), create: (d) => new GloriousClassicHidClient(d), score: () => 5 }];
  }),
  slot(84, [0x258a], async () => {
    const { GloriousCore2HidClient } = await import("./glorious/core2-hid.ts");
    return [{ brand: "Glorious", supports: (d) => GloriousCore2HidClient.isSupported(d), create: (d) => new GloriousCore2HidClient(d), score: () => 5 }];
  }),
  slot(85, [0x258a, 0x3794], async () => {
    const { GloriousSinowealthHidClient } = await import("./glorious/sinowealth-hid.ts");
    return [{ brand: "Glorious", supports: (d) => GloriousSinowealthHidClient.isSupported(d), create: (d) => new GloriousSinowealthHidClient(d), score: () => 5 }];
  }),
  slot(86, [0x2023], async () => {
    const { MchoseA5ProMaxHidClient } = await import("./mchose/a5-gen1-hid.ts");
    return [{ brand: "MCHOSE", supports: (d) => MchoseA5ProMaxHidClient.isSupported(d), create: (d) => new MchoseA5ProMaxHidClient(d), score: () => 8 }];
  }),
  slot(87, [0x3837, 0x5253], async () => {
    const { MchoseHidClient } = await import("./mchose/hid.ts");
    return [{ brand: "MCHOSE", supports: (d) => MchoseHidClient.isSupported(d), create: (d) => new MchoseHidClient(d), score: () => 7 }];
  }),
  slot(88, [0x3837], async () => {
    const { MchoseDockHidClient } = await import("./mchose/dock-hid.ts");
    return [{ brand: "MCHOSE", supports: (d) => MchoseDockHidClient.isSupported(d), create: (d) => new MchoseDockHidClient(d), score: () => 7 }];
  }),
  slot(89, [0x3837], async () => {
    const { MchoseV3HidClient } = await import("./mchose/v3-hid.ts");
    return [{ brand: "MCHOSE", supports: (d) => MchoseV3HidClient.isSupported(d), create: (d) => new MchoseV3HidClient(d), score: () => 7 }];
  }),
  slot(90, [0xa8a4, 0xa8a5], async () => {
    const [{ KsnakeHidClient }, { isNoirM2NexDevice }] = await Promise.all([import("./ksnake/hid.ts"), import("../ksnake/index.ts")]);
    return [{
      brand: "K-snake",
      supports: (d) => KsnakeHidClient.isSupported(d),
      create: (d) => new KsnakeHidClient(d),
      score: () => 5,
      brandOf: (client) => isNoirM2NexDevice(client.device) ? "Noir Gear" : "K-snake",
    }];
  }),
  slot(91, [0xa8a4, 0xa8a5], async () => {
    const { AjazzHidClient } = await import("./ajazz/hid.ts");
    return [{ brand: "AJAZZ", supports: (d) => AjazzHidClient.isSupported(d), create: (d) => new AjazzHidClient(d), score: () => 7 }];
  }),
  slot(92, [0x363c], async () => {
    const { AjazzAj200pHidClient } = await import("./ajazz/aj200p-hid.ts");
    return [{ brand: "AJAZZ", supports: (d) => AjazzAj200pHidClient.isSupported(d), create: (d) => new AjazzAj200pHidClient(d), score: () => 7 }];
  }),
  slot(93, [0x045e], async () => {
    const { MicrosoftHidClient } = await import("./microsoft/hid.ts");
    return [{ brand: "Microsoft", supports: (d) => MicrosoftHidClient.isSupported(d), create: (d) => new MicrosoftHidClient(d), score: () => 5 }];
  }),
  slot(94, [0x093a], async () => {
    const { IncottHidClient } = await import("./incott/hid.ts");
    return [{ brand: "Incott", supports: (d) => IncottHidClient.isSupported(d), create: (d) => new IncottHidClient(d), score: () => 8 }];
  }),
  slot(95, [0x0951, 0x03f0], async () => {
    const { HyperXHidClient } = await import("./hyperx/hid.ts");
    return [{ brand: "HyperX", supports: (d) => HyperXHidClient.isSupported(d), create: (d) => new HyperXHidClient(d), score: () => 5 }];
  }),
  slot(96, [0x04f3], async () => {
    const { KyuProMx1Client } = await import("./ryunix/kyu-pro-mx1-hid.ts");
    return [{ brand: "Ryunix", supports: (d) => KyuProMx1Client.isSupported(d), create: (d) => new KyuProMx1Client(d), score: () => 7 }];
  }),
  slot(97, [0x04f3], async () => {
    const { AmketteXsHidClient } = await import("./amkette/xs-hid.ts");
    return [{
      brand: "Amkette",
      supports: (d) => AmketteXsHidClient.isSupported(d),
      create: (d) => new AmketteXsHidClient(d),
      score: () => 7,
      brandOf: (client) => (client as InstanceType<typeof AmketteXsHidClient>).deviceBrand(),
    }];
  }),
  slot(98, [0x24ae], async () => {
    const { RapooHidClient } = await import("./rapoo/hid.ts");
    return [{ brand: "Rapoo", supports: (d) => RapooHidClient.isSupported(d), create: (d) => new RapooHidClient(d), score: () => 7 }];
  }),
  slot(99, [0x2516], async () => {
    const { CoolerMasterHidClient } = await import("./coolermaster/hid.ts");
    return [{ brand: "Cooler Master", supports: (d) => CoolerMasterHidClient.isSupported(d), create: (d) => new CoolerMasterHidClient(d), score: () => 7 }];
  }),
  slot(100, [0x1a2c], async () => {
    const { AjazzAk820HidClient } = await import("./ajazz/ak820-hid.ts");
    return [{ brand: "AJAZZ", supports: (d) => AjazzAk820HidClient.isSupported(d), create: (d) => new AjazzAk820HidClient(d), score: () => 7 }];
  }),
  slot(101, [0x3770], async () => {
    const { HitscanXviHidClient } = await import("./hitscan/xvi-hid.ts");
    return [{ brand: "Hitscan", supports: (d) => HitscanXviHidClient.isSupported(d), create: (d) => new HitscanXviHidClient(d), score: () => 10 }];
  }),
  slot(102, [0x3770], async () => {
    const { HitscanHidClient } = await import("./hitscan/hid.ts");
    return [{ brand: "Hitscan", supports: (d) => HitscanHidClient.isSupported(d), create: (d) => new HitscanHidClient(d), score: () => 7 }];
  }),
  slot(103, [0x1e7d], async () => {
    const { RoccatHidClient } = await import("./roccat/hid.ts");
    return [{ brand: "ROCCAT", supports: (d) => RoccatHidClient.isSupported(d), create: (d) => new RoccatHidClient(d), score: () => 6 }];
  }),
  slot(104, [0x264a], async () => {
    const { ThermaltakeHidClient, thermaltakeChannel } = await import("./thermaltake/hid.ts");
    const channelScore = { config: 7, short: 5, none: 2 } as const;
    return [{
      brand: "Thermaltake",
      supports: (d) => ThermaltakeHidClient.isSupported(d),
      create: (d) => new ThermaltakeHidClient(d),
      score: (d) => channelScore[thermaltakeChannel(d) ?? "none"],
    }];
  }),
];

const BY_VID = new Map<number, DriverSlot[]>();
for (const entry of DRIVER_SLOTS) {
  for (const vid of entry.vids) {
    let list = BY_VID.get(vid);
    if (!list) {
      list = [];
      BY_VID.set(vid, list);
    }
    list.push(entry);
  }
}

function loadSlot(entry: DriverSlot): Promise<readonly LazyDriver[]> {
  return (entry.cached ??= entry.load().catch((error) => {
    entry.cached = undefined;
    throw error;
  }));
}

export interface LazyDriverRegistry {
  clientSupportScore(device: HIDDevice): number;
  createSupportedClient(device: HIDDevice): SupportedClient | null;
  deviceBrand(client: SupportedClient): string;
}

/**
 * Dynamically imports only the driver chunks whose `vendorId` matches a connected device in `devices`,
 * returning a synchronous registry view over the matching loaded drivers.
 */
export async function loadRegistryForDevices(devices: readonly HIDDevice[]): Promise<LazyDriverRegistry> {
  const vids = new Set<number>();
  for (const device of devices) vids.add(device.vendorId);
  const matchingSlots = new Set<DriverSlot>();
  for (const vid of vids) {
    for (const entry of BY_VID.get(vid) ?? []) matchingSlots.add(entry);
  }
  const ordered = [...matchingSlots].sort((a, b) => a.order - b.order);
  const loaded = (await Promise.all(ordered.map(loadSlot))).flat();

  const driverFor = (device: HIDDevice): LazyDriver | undefined =>
    loaded.find((driver) => driver.supports(device));

  return {
    clientSupportScore: (device) => driverFor(device)?.score(device) ?? 0,
    createSupportedClient: (device) => driverFor(device)?.create(device) ?? null,
    deviceBrand: (client) => {
      const driver = driverFor(client.device);
      if (!driver) return "Unknown";
      return driver.brandOf ? driver.brandOf(client) : driver.brand;
    },
  };
}
