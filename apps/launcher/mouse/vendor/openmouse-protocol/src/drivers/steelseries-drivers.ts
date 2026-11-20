import { SteelSeriesRival3HidClient } from "./steelseries/hid.ts";
import { SteelSeriesRival100HidClient } from "./steelseries/rival-100-hid.ts";
import { SteelSeriesRival110HidClient } from "./steelseries/rival-110-hid.ts";
import { SteelSeriesRival300HidClient } from "./steelseries/rival-300-hid.ts";
import { SteelSeriesAerox3HidClient } from "./steelseries/aerox3-hid.ts";
import { SteelSeriesAerox3WirelessHidClient } from "./steelseries/aerox3-wireless-hid.ts";
import { SteelSeriesRival3WirelessHidClient } from "./steelseries/rival3-wireless-hid.ts";
import { SteelSeriesAerox5HidClient } from "./steelseries/aerox5-hid.ts";
import { SteelSeriesAerox5WirelessHidClient } from "./steelseries/aerox5-wireless-hid.ts";
import { SteelSeriesRival650HidClient } from "./steelseries/rival650-hid.ts";
import { SteelSeriesRival600HidClient } from "./steelseries/rival-600-hid.ts";
import { SteelSeriesAerox9WirelessHidClient } from "./steelseries/aerox9-wireless-hid.ts";
import { SteelSeriesRival310HidClient } from "./steelseries/rival310-hid.ts";
import { SteelSeriesPrimePlusHidClient } from "./steelseries/prime-plus-hid.ts";
import { SteelSeriesPrimeMiniWirelessHidClient } from "./steelseries/prime-mini-wireless-hid.ts";
import { SteelSeriesPrimeMiniHidClient } from "./steelseries/prime-mini-hid.ts";
import { SteelSeriesSenseiTenHidClient } from "./steelseries/sensei-ten-hid.ts";
import { SteelSeriesSenseiRivalProHidClient } from "./steelseries/sensei-rival-pro-hid.ts";
import { SteelSeriesSensei310HidClient } from "./steelseries/sensei-310-hid.ts";
import { SteelSeriesRival5HidClient } from "./steelseries/rival-5-hid.ts";
import { SteelSeriesRival3Gen2HidClient } from "./steelseries/rival-3-v2-hid.ts";
import { SteelSeriesRival3WirelessV2HidClient } from "./steelseries/rival-3-wireless-v2-hid.ts";
import { SteelSeriesAerox3WirelessGen2HidClient } from "./steelseries/aerox-3-wireless-gen2-hid.ts";
import { SteelSeriesSenseiRawHidClient } from "./steelseries/sensei-raw-hid.ts";
import type { DeviceDriver } from "./registry.ts";

export const STEELSERIES_DRIVERS: readonly DeviceDriver[] = [
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival3HidClient.isSupported(device), create: (device) => new SteelSeriesRival3HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival100HidClient.isSupported(device), create: (device) => new SteelSeriesRival100HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival110HidClient.isSupported(device), create: (device) => new SteelSeriesRival110HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival300HidClient.isSupported(device), create: (device) => new SteelSeriesRival300HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesAerox3HidClient.isSupported(device), create: (device) => new SteelSeriesAerox3HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesAerox3WirelessHidClient.isSupported(device), create: (device) => new SteelSeriesAerox3WirelessHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival3WirelessHidClient.isSupported(device), create: (device) => new SteelSeriesRival3WirelessHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesAerox5HidClient.isSupported(device), create: (device) => new SteelSeriesAerox5HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesAerox5WirelessHidClient.isSupported(device), create: (device) => new SteelSeriesAerox5WirelessHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival650HidClient.isSupported(device), create: (device) => new SteelSeriesRival650HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival600HidClient.isSupported(device), create: (device) => new SteelSeriesRival600HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesAerox9WirelessHidClient.isSupported(device), create: (device) => new SteelSeriesAerox9WirelessHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival310HidClient.isSupported(device), create: (device) => new SteelSeriesRival310HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesPrimePlusHidClient.isSupported(device), create: (device) => new SteelSeriesPrimePlusHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesPrimeMiniWirelessHidClient.isSupported(device), create: (device) => new SteelSeriesPrimeMiniWirelessHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesPrimeMiniHidClient.isSupported(device), create: (device) => new SteelSeriesPrimeMiniHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesSenseiTenHidClient.isSupported(device), create: (device) => new SteelSeriesSenseiTenHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesSenseiRivalProHidClient.isSupported(device), create: (device) => new SteelSeriesSenseiRivalProHidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesSensei310HidClient.isSupported(device), create: (device) => new SteelSeriesSensei310HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival5HidClient.isSupported(device), create: (device) => new SteelSeriesRival5HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival3Gen2HidClient.isSupported(device), create: (device) => new SteelSeriesRival3Gen2HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesRival3WirelessV2HidClient.isSupported(device), create: (device) => new SteelSeriesRival3WirelessV2HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesAerox3WirelessGen2HidClient.isSupported(device), create: (device) => new SteelSeriesAerox3WirelessGen2HidClient(device), score: () => 6 },
  { brand: "SteelSeries", supports: (device) => SteelSeriesSenseiRawHidClient.isSupported(device), create: (device) => new SteelSeriesSenseiRawHidClient(device), score: () => 6 },
];
