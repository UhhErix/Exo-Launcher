pub mod anticheat;
pub mod base64;
pub mod device_types;
pub mod discord_chat_types;
pub mod discord_types;
pub mod events;
pub mod game_types;
pub mod keyboard_layouts;
pub mod media_types;
pub mod pc_types;
pub mod pipe;
pub mod remote_art;
pub mod settings_types;
pub mod types;
pub mod wire_codec;
pub use exo_wire;

pub use anticheat::{AntiCheatClass, is_anti_cheat_process, normalize_process_name};
pub use device_types::{
    AudioEndpoint, AudioFormatInfo, AudioFormatOption, AudioFormatsReply, AudioReadReply,
    AudioVoiceInfo, ButtonBounce, ButtonCheck, ControllerCalibrateReply, ControllerCalibration,
    ControllerFirmware, ControllerGameProfile, ControllerGameProfilesReply, ControllerLayerTool,
    ControllerLayers, ControllerMeasureReply, ControllerPolling, ControllerQualityReply,
    ControllerReadReply, ControllerSettingsReply, ControllerSlot, ControllerSoundReply,
    ControllerStateEvent, ControllerTestEdgeReply, ControllerTestRestReply, ControllerTested,
    EqApoState, EqCustomBand, EqGameSound, EqHeadphone, EqHeadphonesReply, EqLibraryGame,
    EqNightState, EqOtherOutput, EqOutputInfo, EqPlayingInfo, EqStateReply, HidCollectionInfo,
    HidDeviceInfo, HidInputEvent, HidReply, MouseDataReply, PadOverrides, PadSettings,
    SteamInputStateReply, StickEdge, StickRest, TriggerSweep,
};
pub use discord_chat_types::{
    DiscordAppChoice, DiscordAppCommand, DiscordAppComponent, DiscordAppFileRef,
    DiscordAppGalleryItem, DiscordAppInfo, DiscordAppMedia, DiscordAppModal, DiscordAppModalApp,
    DiscordAppOption, DiscordAppSelectOption, DiscordAppsReply, DiscordBadge,
    DiscordChannelInviteReply, DiscordConnection, DiscordDmLineEvent, DiscordEmojiFavoritesReply,
    DiscordEmojiGuild, DiscordEmojisReply, DiscordFavoriteGif, DiscordFileTextReply,
    DiscordForYouItem, DiscordForYouReply, DiscordGif, DiscordGifFavoritesReply, DiscordGifsReply,
    DiscordGiftCard, DiscordGuildEmoji, DiscordImageReply, DiscordInteractionEvent,
    DiscordInteractionMeta, DiscordInviteCard, DiscordMentionChannel, DiscordMentionRole,
    DiscordMentionableReply, DiscordMentionsReply, DiscordMessageCall, DiscordMessageSnapshot,
    DiscordMessageSnapshotInner, DiscordMessageThread, DiscordMutualGuild, DiscordNotifyClearEvent,
    DiscordNotifyEvent, DiscordOpenEvent, DiscordPlaceRef, DiscordPoll, DiscordPollAnswer,
    DiscordPollAnswerCount, DiscordPollMedia, DiscordPollResults, DiscordPollVoteEvent,
    DiscordProfile, DiscordProfileBio, DiscordProfileExtras, DiscordProfileGuildMember,
    DiscordProfilePresence, DiscordProfileReply, DiscordProfileRole, DiscordProfileUser,
    DiscordProfileVoice, DiscordReactionCountDetails, DiscordReactorsReply, DiscordRoleChoice,
    DiscordRolePerson, DiscordRolePersonCan, DiscordSaveData, DiscordSavedMessage,
    DiscordSavedReply, DiscordServerCardReply, DiscordSticker, DiscordStickerGroup,
    DiscordStickerItem, DiscordStickersReply, DiscordThreadCreateReply, DiscordThreadCreated,
    FriendsBadgeReply, SpotifyLinkReply,
};
pub use discord_types::{
    DiscordAccount, DiscordAccountConnection, DiscordAccountItem, DiscordAccountReply,
    DiscordAccountSettings, DiscordAccountsReply, DiscordAdminMfa, DiscordAdminReply,
    DiscordAttachment, DiscordAudioDevice, DiscordAudioReply, DiscordAudioUser,
    DiscordAuthorizedApp, DiscordCallEvent, DiscordCallSong, DiscordCallSongsReply,
    DiscordChangedEvent, DiscordChannel, DiscordChannels, DiscordClientSettings,
    DiscordColorsReply, DiscordCustomStatus, DiscordDm, DiscordDmMember, DiscordEmbed,
    DiscordEmbedAuthor, DiscordEmbedField, DiscordEmbedFooter, DiscordEmbedMedia,
    DiscordEmbedProvider, DiscordEvent, DiscordEventEntityMeta, DiscordEventEntry,
    DiscordFontReply, DiscordForumTag, DiscordFriend, DiscordFriendActionReply,
    DiscordFriendsReply, DiscordGoLiveReply, DiscordGoLiveStateEvent, DiscordGoLiveStatsEvent,
    DiscordGuide, DiscordGuideAction, DiscordGuideEmoji, DiscordGuideWelcome, DiscordGuild,
    DiscordGuildSoundboard, DiscordHome, DiscordKeybind, DiscordMe, DiscordMember,
    DiscordMemberItem, DiscordMemberList, DiscordMemberRow, DiscordMemberRowInner,
    DiscordMemberSearch, DiscordMessage, DiscordMessageEvent, DiscordMessageMember,
    DiscordMessageRef, DiscordMessagesReply, DiscordMicHeard, DiscordMicLevel,
    DiscordMicMonitorReply, DiscordModeration, DiscordMyVoiceReply, DiscordPaymentChoice,
    DiscordPinsReply, DiscordPrep, DiscordPresenceEvent, DiscordRailItem, DiscordReaction,
    DiscordReactionEmoji, DiscordReactionEvent, DiscordReportButton, DiscordReportElement,
    DiscordReportMenu, DiscordReportNode, DiscordReportReply, DiscordResource, DiscordRich,
    DiscordRole, DiscordRoleBrief, DiscordRoleIcon, DiscordSearchReply, DiscordSearchResult,
    DiscordSendReply, DiscordServerReply, DiscordServerTagOption, DiscordSession,
    DiscordShopCatalog, DiscordShopCategory, DiscordShopCountryPrices, DiscordShopItem,
    DiscordShopPrice, DiscordShopPriceEntry, DiscordShopProduct, DiscordShopReply,
    DiscordShopStyles, DiscordShopTierPrices, DiscordSignInStep, DiscordSoundItem,
    DiscordSoundboardReply, DiscordStateEvent, DiscordStatus, DiscordThread, DiscordThreadMetadata,
    DiscordThreadOwner, DiscordThreadSummary, DiscordThreadsReply, DiscordTtsEvent,
    DiscordTypingEvent, DiscordUnreadChannel, DiscordUnreadEvent, DiscordUnreadsReply,
    DiscordUploadReply, DiscordUser, DiscordUserSettingsReply, DiscordVideoEvent,
    DiscordVideoFrame, DiscordVideoFrameMeta, DiscordVoice, DiscordVoiceMember, DiscordVoiceReply,
    DiscordVoiceStages, GoLiveSource,
};
pub use events::{
    AppUpdateStateEvent, AutopilotReportEvent, CaptureReelEvent, CaptureSavedEvent,
    CaptureShrunkEvent, GameRunStateEvent, GpuDriverInstallEvent, InstallQueueEvent,
    LaunchStatusEvent, MusicVolumeEvent, NotesOpenEvent, PausedDownloadItem, RequestId,
    ShellBootstrap, StoreChangedEvent, StoreSaleEvent,
};
pub use game_types::{
    AntiCheatLaunch, ArtworkMutationResponse, CompressProgressEvent, CompressState,
    CompressStateReply, DequeueReply, DlssFileOutcome, DlssNewerItem, DlssNewerReply,
    DlssRunResult, DlssStatus, DlssStatusItem, DriveHeat, FrameGenMod, GameAchievementsEvent,
    GameContent, GameGraphicsFeaturesState, GameOptimizeEvent, GameSettingsGroup,
    GameSettingsState, GpuGame, GraphicsFeatureRow, HdrAutoHdr, HdrGame, InstallResponse,
    KeepStatus, MovingProgress, OpenUrlReply, PropertyChoice, ShaderStatus, ShaderWarmed,
    UninstallResponse,
};
pub use media_types::{
    ArtistAbout, ArtistAboutCity, ArtistAboutLink, CaptureEditReply, CaptureHotkeys, CaptureItem,
    CaptureItemReply, CaptureListReply, CaptureLiveState, CaptureReelReply, CaptureSaveImageReply,
    CaptureSendReply, CaptureSettingsDto, CaptureStatusReply, CaptureTextReply, Concert,
    CreditGroup, CreditPerson, JamMember, JamSession, LyricsLine, LyricsWord, MusicControlReply,
    MusicMatchReply, MusicMatchSong, MusicPlayback, MusicPlaybackReply, MusicStatusEvent,
    MusicVolumeReply, NoteInfo, NotesActReply, NotesListReply, NotesOpenReply, NotesPendingReply,
    NotesPhotoReply, NotesReadReply, NotesSaveAsReply, NotesSaveReply, NotesSpellReply,
    NotesTextFilesReply, PlayerActReply, PlayerAlbumRef, PlayerArtist, PlayerCard, PlayerDevice,
    PlayerFolderRef, PlayerFriend, PlayerFriendContext, PlayerFriendTrack, PlayerFriendUser,
    PlayerHeader, PlayerLink, PlayerOption, PlayerRating, PlayerSection, PlayerStartReply,
    PlayerStateData, PlayerStateReply, PlayerTrack, PlayerViewData, PlayerViewReply, SpotifyAudio,
    SpotifyAudioReply, SpotifyEq, SpotifyEqFilter, format_duration_ms,
};
pub use pc_types::{
    Afmf3State, AppAutoCleared, AppCleanReply, AppConfirmReply, AppFind, AppLeftover, AppSkip,
    AppUndoReply, AppsIconsReply, AppsListReply, AppsPackagesPlanReply, AppsPackagesRemoveReply,
    AppsPackagesReply, AutoTuneState, AutopilotDownloadsPaused, AutopilotFinding,
    AutopilotGameMode, AutopilotLive, AutopilotMemoryIntegrity, AutopilotMouseAccel, AutopilotNow,
    AutopilotReport, AutopilotSetReply, AutopilotStandbyPurge, AutopilotState,
    AutopilotTimerGlobal, BackupDay, BackupNote, BackupOffer, BackupStatus, BiosStaged,
    BiosUpdateReply, ClientsEpic, ClientsStatus, ClientsSteam, ColorControl, CompressActReply,
    CompressAllReply, CompressList, CompressListGame, CompressSettings, CompressSettingsReply,
    DevicesImageReply, DevicesListReply, DisplayAdjust, DisplayBest, DisplayDdc, DisplayScreen,
    DisplayState, DriveFix, DriveGameEntry, DriveHealth, DriveInfo, DriveLink, DriveSpaceBreakdown,
    DriveTemp, DriveVendor, DriveVerdict, DriveVolume, DriverAdvised, DriverEntry,
    DriverEntryAdvised, DriverLatest, DriverProgress, DriverSkipped, DriverState, DriverStep,
    DriversState, DrivesReply, ExoAiFans, ExoAiGaming, ExoAiState, FpsCounterReply,
    GpuAdapterUsage, GpuEngineSlice, GpuFanChannelCurve, GpuFanPoint, GpuFeature, GpuManualProfile,
    GpuManualTuningState, GpuProcessUsage, GpuProcessesState, GpuProfile, GpuProfileItem,
    GpuProfileScreen, GpuStabilityProgress, GpuTuningCapabilities, GpuTuningChangedEvent,
    GpuTuningLive, GpuTuningRange, GpuUsageAnswer, GpuVfPoint, HdrCalibrateResult, HdrCalibration,
    HdrCalibrationProgress, HdrDisplayInfo, HdrLaptopCheckItem, HdrLaptopChecks, HdrPeakHealth,
    HdrState, HdrWarningEvent, InstalledAppItem, InstallerCacheCleanReply, InstallerCacheReply,
    InterruptDevice, InterruptsState, KeyboardBackupResult, KeyboardBackupSummary,
    KeyboardBackupsReply, KeyboardProfileSlot, KeyboardReadReply, LatencyReport, LatencySample,
    NetCheck, NetSample, OutputColor, OverlayAdvisorItem, OverlayAdvisorState, PcHealth,
    PcHealthItem, PeripheralCard, PeripheralPort, QosGame, QosState, RestorePointReply,
    RuntimeItem, RuntimeResult, RuntimesInstallReply, RuntimesListReply, RuntimesProgress,
    ShaderEstimate, ShaderPrepareGame, ShaderRebuild, ShaderSnapGame, ShadersAddonStatus,
    ShadersEstimatesReply, ShadersPrepareGamesReply, ShadersSnapshot, StartupItem,
    StartupListReply, StartupRemovedItem, SystemAbout, SystemTuningState, TuneMeasure, TunePart,
    TuningItem, TuningMigration, UpdateCheckReply, UpdateProgress, WindowsApp, WindowsAppLine,
    WindowsReleaseInfo,
};
pub use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::value::RawValue;
use serde_json::{Value, json};
pub use settings_types::{
    AccountsProgressEvent, AmazonAccountInfo, BackupActionReply, BackupStoppedEvent,
    ComponentsFailedEvent, EpicAccountsInfo, EpicAddedAccount, ExoAccountFileReply,
    ExoAccountHeldItem, ExoAccountOutcome, ExoAccountOverview, ExoAccountPasskeyRow,
    ExoAccountPasskeyStatus, ExoAccountProbe, ExoAccountSession, ExoAccountSessionsReply,
    ExoAccountSyncState, GogAccountInfo, JunkCleanReply, JunkItem, JunkScanReply, LeftoverSummary,
    NativeAuthProviderStatus, NativeAuthProvidersReply, PickFolderReply, ReplacedApp,
    ReplacedRemoveReply, ReplacedScanReply, ReportPreviewReply, ReportSendReply, RiotAccountEntry,
    RiotAccountsInfo, RiotBaseline, RiotBaselineReply, RiotExportImport, RiotHoursRefresh,
    ShadersEventPayload, SteamAccountEntry, SteamAccountReply, SteamAccountsInfo, SteamSignInStep,
    StoreAccounts, StoreGiftsReply, StoreLocalCheck, StoreStatus, StoresAuthReply,
    TelemetrySentReply, WebClientOptions,
};
use std::borrow::Cow;
use std::collections::{HashMap, VecDeque};
use std::fmt;
use std::io;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, mpsc};
use std::time::Duration;
pub use types::{
    ActionResult, ArtBase, CapLabActionReply, CapLabArmMetrics, CapLabStatus, ExoAccountState,
    FolderArtReply, FrameCapBackend, FrameCapStutter, FrameCapStutterBreakdown, FrameCapSuggestion,
    FrameCapTier, FrameGenRoute, Game, GameExtras, GameFrameCap, GameFrameGen, GameGetReply,
    GameProperties, GamePropertySetReply, GameVariant, HostContract, HostEvent, InstallProgress,
    KeyPreview, LauncherSettings, LibraryFolder, LibraryPayload, LibraryRefreshAllReply,
    PickPathReply, RiotAccounts, RiotSavedAccount, StoreAccountsReply, StoreArt, StoreGame,
    StoreGift, StoreGifts, StoreHome, StoreLowest, StoreMedia, StoreOffer, StoreOtherOffers,
    StoreRecentReviews, StoreSearchReply, StoreShelf, StoreSpec, StoreTile, StoreWishlistReply,
    UninstallReply,
};

pub const PROTOCOL_VERSION: u32 = 1;
const MAX_INBOUND_BYTES: usize = 32 * 1024 * 1024;
const MAX_MEDIA_META_BYTES: usize = 64 * 1024;
const MAX_MEDIA_FRAME_BYTES: usize = 8 * 1024 * 1024;

pub const METHOD_APP_CHECK_UPDATE: &str = "app.checkUpdate";
pub const METHOD_APP_INSTALL_UPDATE: &str = "app.installUpdate";
pub const METHOD_APP_OPEN_LOGS: &str = "app.openLogs";
pub const METHOD_APP_REPAIR_CACHE: &str = "app.repairCache";
pub const METHOD_APPLEMUSIC_ACT: &str = "applemusic.act";
pub const METHOD_APPLEMUSIC_START: &str = "applemusic.start";
pub const METHOD_APPLEMUSIC_VIEW: &str = "applemusic.view";
pub const METHOD_APPS_ALLOW_EDGE: &str = "apps.allowEdge";
pub const METHOD_APPS_CLEAN: &str = "apps.clean";
pub const METHOD_APPS_CONFIRM: &str = "apps.confirm";
pub const METHOD_APPS_ICONS: &str = "apps.icons";
pub const METHOD_APPS_INSTALLER_CACHE: &str = "apps.installerCache";
pub const METHOD_APPS_INSTALLER_CACHE_CLEAN: &str = "apps.installerCacheClean";
pub const METHOD_APPS_LEAVE: &str = "apps.leave";
pub const METHOD_APPS_LIST: &str = "apps.list";
pub const METHOD_APPS_OPEN_DEFAULTS: &str = "apps.openDefaults";
pub const METHOD_APPS_PACKAGE_ADD_BACK: &str = "apps.packageAddBack";
pub const METHOD_APPS_PACKAGES: &str = "apps.packages";
pub const METHOD_APPS_PACKAGES_PLAN: &str = "apps.packagesPlan";
pub const METHOD_APPS_PACKAGES_REMOVE: &str = "apps.packagesRemove";
pub const METHOD_APPS_RESTORE_POINT: &str = "apps.restorePoint";
pub const METHOD_APPS_SCAN: &str = "apps.scan";
pub const METHOD_APPS_TIDY: &str = "apps.tidy";
pub const METHOD_APPS_UNDO: &str = "apps.undo";
pub const METHOD_APPS_UNINSTALL: &str = "apps.uninstall";
pub const METHOD_ART_BASE: &str = "art.base";
pub const METHOD_ART_REFETCH: &str = "art.refetch";
pub const METHOD_ART_REPLACE: &str = "art.replace";
pub const METHOD_ART_RESET: &str = "art.reset";
pub const METHOD_AUDIO_FORMATS: &str = "audio.formats";
pub const METHOD_COMPRESS_ACT: &str = "compress.act";
pub const METHOD_COMPRESS_STATE: &str = "compress.state";
pub const METHOD_DLSS_CONSENT: &str = "dlss.consent";
pub const METHOD_DLSS_DOWNLOAD_NEWER: &str = "dlss.downloadNewer";
pub const METHOD_DLSS_FRAME_GEN: &str = "dlss.frameGen";
pub const METHOD_DLSS_NEWER: &str = "dlss.newer";
pub const METHOD_DLSS_RESTORE: &str = "dlss.restore";
pub const METHOD_DLSS_UPDATE_ALL: &str = "dlss.updateAll";
pub const METHOD_DLSS_STATUS: &str = "dlss.status";
pub const METHOD_GPU_GAME: &str = "gpu.game";
pub const METHOD_HDR_GAME: &str = "hdr.game";
pub const METHOD_KEEP_STATUS: &str = "keep.status";
pub const METHOD_AUDIO_READ: &str = "audio.read";
pub const METHOD_AUDIO_SET_FORMAT: &str = "audio.setFormat";
pub const METHOD_AUDIO_SET_MUTE: &str = "audio.setMute";
pub const METHOD_AUDIO_SET_VOLUME: &str = "audio.setVolume";
pub const METHOD_AUTH_NATIVE_PROVIDERS: &str = "auth.nativeProviders";
pub const METHOD_AUTOPILOT_SET: &str = "autopilot.set";
pub const METHOD_AUTOPILOT_STATE: &str = "autopilot.state";
pub const METHOD_BIOS_UPDATE: &str = "bios.update";
pub const METHOD_CAPTURE_ACT: &str = "capture.act";
pub const METHOD_CAPTURE_EDIT: &str = "capture.edit";
pub const METHOD_CAPTURE_ITEM: &str = "capture.item";
pub const METHOD_CAPTURE_LIST: &str = "capture.list";
pub const METHOD_CAPTURE_REEL: &str = "capture.reel";
pub const METHOD_CAPTURE_REEL_CANCEL: &str = "capture.reel.cancel";
pub const METHOD_CAPTURE_SAVE_IMAGE: &str = "capture.saveImage";
pub const METHOD_CAPTURE_SEND: &str = "capture.send";
pub const METHOD_CAPTURE_SETTINGS: &str = "capture.settings";
pub const METHOD_CAPTURE_STATUS: &str = "capture.status";
pub const METHOD_CAPTURE_TEXT: &str = "capture.text";
pub const METHOD_COMPRESS_ALL: &str = "compress.all";
pub const METHOD_COMPRESS_LIST: &str = "compress.list";
pub const METHOD_COMPRESS_SETTINGS: &str = "compress.settings";
pub const METHOD_CONTROLLER_CALIBRATE: &str = "controller.calibrate";
pub const METHOD_CONTROLLER_GAME_PROFILES: &str = "controller.gameProfiles";
pub const METHOD_CONTROLLER_GAME_PROFILES_SAVE: &str = "controller.gameProfilesSave";
pub const METHOD_CONTROLLER_MEASURE: &str = "controller.measure";
pub const METHOD_CONTROLLER_QUALITY: &str = "controller.quality";
pub const METHOD_CONTROLLER_READ: &str = "controller.read";
pub const METHOD_CONTROLLER_SETTINGS: &str = "controller.settings";
pub const METHOD_CONTROLLER_SOUND: &str = "controller.sound";
pub const METHOD_CONTROLLER_TEST: &str = "controller.test";
pub const METHOD_CONTROLLER_WATCH: &str = "controller.watch";
pub const METHOD_DEVICES_IMAGE: &str = "devices.image";
pub const METHOD_DEVICES_LIST: &str = "devices.list";
pub const METHOD_DISCORD_ACCOUNT: &str = "discord.account";
pub const METHOD_DISCORD_ACCOUNTS: &str = "discord.accounts";
pub const METHOD_DISCORD_ACK: &str = "discord.ack";
pub const METHOD_DISCORD_ADMIN: &str = "discord.admin";
pub const METHOD_DISCORD_APPS: &str = "discord.apps";
pub const METHOD_DISCORD_AUDIO: &str = "discord.audio";
pub const METHOD_DISCORD_CALL_SONGS: &str = "discord.callSongs";
pub const METHOD_DISCORD_CAMERA: &str = "discord.camera";
pub const METHOD_DISCORD_CHANNEL: &str = "discord.channel";
pub const METHOD_DISCORD_CHANNELS: &str = "discord.channels";
pub const METHOD_DISCORD_CLIENT_SETTINGS: &str = "discord.clientSettings";
pub const METHOD_DISCORD_COLORS: &str = "discord.colors";
pub const METHOD_DISCORD_DELETE: &str = "discord.delete";
pub const METHOD_DISCORD_EDIT: &str = "discord.edit";
pub const METHOD_DISCORD_EMOJI_FAVORITES: &str = "discord.emojiFavorites";
pub const METHOD_DISCORD_EMOJIS: &str = "discord.emojis";
pub const METHOD_DISCORD_FILE_TEXT: &str = "discord.fileText";
pub const METHOD_DISCORD_FONT: &str = "discord.font";
pub const METHOD_DISCORD_FOR_YOU: &str = "discord.forYou";
pub const METHOD_DISCORD_FRIEND: &str = "discord.friend";
pub const METHOD_DISCORD_FRIENDS: &str = "discord.friends";
pub const METHOD_DISCORD_GIF_FAVORITES: &str = "discord.gifFavorites";
pub const METHOD_DISCORD_GIFS: &str = "discord.gifs";
pub const METHOD_DISCORD_GOLIVE: &str = "discord.golive";
pub const METHOD_DISCORD_HOME: &str = "discord.home";
pub const METHOD_DISCORD_IMAGE: &str = "discord.image";
pub const METHOD_DISCORD_MARK_READ: &str = "discord.markRead";
pub const METHOD_DISCORD_MEMBERS: &str = "discord.members";
pub const METHOD_DISCORD_MENTIONABLE: &str = "discord.mentionable";
pub const METHOD_DISCORD_MENTIONS: &str = "discord.mentions";
pub const METHOD_DISCORD_MESSAGE_ACTION: &str = "discord.messageAction";
pub const METHOD_DISCORD_MESSAGES: &str = "discord.messages";
pub const METHOD_DISCORD_MIC_MONITOR: &str = "discord.micMonitor";
pub const METHOD_DISCORD_MIC_TEST: &str = "discord.micTest";
pub const METHOD_DISCORD_MY_VOICE: &str = "discord.myVoice";
pub const METHOD_DISCORD_PINS: &str = "discord.pins";
pub const METHOD_DISCORD_POLL: &str = "discord.poll";
pub const METHOD_DISCORD_PROFILE: &str = "discord.profile";
pub const METHOD_DISCORD_REACT: &str = "discord.react";
pub const METHOD_DISCORD_REACTORS: &str = "discord.reactors";
pub const METHOD_DISCORD_REPORT: &str = "discord.report";
pub const METHOD_DISCORD_RING: &str = "discord.ring";
pub const METHOD_DISCORD_SAVED: &str = "discord.saved";
pub const METHOD_DISCORD_SEARCH: &str = "discord.search";
pub const METHOD_DISCORD_SEND: &str = "discord.send";
pub const METHOD_DISCORD_SERVER: &str = "discord.server";
pub const METHOD_DISCORD_SET_STATUS: &str = "discord.setStatus";
pub const METHOD_DISCORD_SHOP: &str = "discord.shop";
pub const METHOD_DISCORD_SIGN_IN: &str = "discord.signIn";
pub const METHOD_DISCORD_SIGN_OUT: &str = "discord.signOut";
pub const METHOD_DISCORD_SOUND: &str = "discord.sound";
pub const METHOD_DISCORD_SOUNDBOARD: &str = "discord.soundboard";
pub const METHOD_DISCORD_STATUS: &str = "discord.status";
pub const METHOD_DISCORD_STICKER: &str = "discord.sticker";
pub const METHOD_DISCORD_STICKER_DATA: &str = "discord.stickerData";
pub const METHOD_DISCORD_STICKERS: &str = "discord.stickers";
pub const METHOD_DISCORD_STREAM: &str = "discord.stream";
pub const METHOD_DISCORD_THREAD: &str = "discord.thread";
pub const METHOD_DISCORD_THREADS: &str = "discord.threads";
pub const METHOD_DISCORD_TYPING: &str = "discord.typing";
pub const METHOD_DISCORD_UNREADS: &str = "discord.unreads";
pub const METHOD_DISCORD_UPLOAD: &str = "discord.upload";
pub const METHOD_DISCORD_USER_SETTINGS: &str = "discord.userSettings";
pub const METHOD_DISCORD_VIEW: &str = "discord.view";
pub const METHOD_DISCORD_VOICE: &str = "discord.voice";
pub const METHOD_FRIENDS_BADGE: &str = "friends.badge";
pub const METHOD_EQ: &str = "eq";
pub const METHOD_EQ_HEADPHONES: &str = "eq.headphones";
pub const METHOD_HID_CLOSE: &str = "hid.close";
pub const METHOD_HID_LIST: &str = "hid.list";
pub const METHOD_HID_OPEN: &str = "hid.open";
pub const METHOD_HID_RECEIVE_FEATURE_REPORT: &str = "hid.receiveFeatureReport";
pub const METHOD_HID_RECEIVE_INPUT_REPORT: &str = "hid.receiveInputReport";
pub const METHOD_HID_SEND_FEATURE_REPORT: &str = "hid.sendFeatureReport";
pub const METHOD_HID_SEND_REPORT: &str = "hid.sendReport";
pub const METHOD_MOUSE_PRESETS_GET: &str = "mouse.presets.get";
pub const METHOD_MOUSE_PRESETS_SET: &str = "mouse.presets.set";
pub const METHOD_MOUSE_PROFILES_GET: &str = "mouse.profiles.get";
pub const METHOD_MOUSE_PROFILES_SET: &str = "mouse.profiles.set";
pub const METHOD_NOTES_ACT: &str = "notes.act";
pub const METHOD_NOTES_LIST: &str = "notes.list";
pub const METHOD_NOTES_OPEN: &str = "notes.open";
pub const METHOD_NOTES_PENDING: &str = "notes.pending";
pub const METHOD_NOTES_PHOTO: &str = "notes.photo";
pub const METHOD_NOTES_READ: &str = "notes.read";
pub const METHOD_NOTES_SAVE: &str = "notes.save";
pub const METHOD_NOTES_SAVE_AS: &str = "notes.saveAs";
pub const METHOD_NOTES_SESSION: &str = "notes.session";
pub const METHOD_NOTES_SPELL: &str = "notes.spell";
pub const METHOD_NOTES_TEXT_FILES: &str = "notes.textFiles";
pub const METHOD_STEAM_INPUT_STATE: &str = "steamInput.state";
pub const METHOD_EXO_ACCOUNT_SIGN_OUT: &str = "exoAccount.signOut";
pub const METHOD_BACKUP_STATUS: &str = "backup.status";
pub const METHOD_CLIENTS_STATUS: &str = "clients.status";
pub const METHOD_EXO_ACCOUNT_STATE: &str = "exoAccount.state";
pub const METHOD_EXO_ACCOUNT_SYNC: &str = "exoAccount.sync";
pub const METHOD_EXO_AI_SET: &str = "exoAi.set";
pub const METHOD_EXO_AI_STATE: &str = "exoAi.state";
pub const METHOD_FPS_COUNTER: &str = "fps.counter";
pub const METHOD_GPU_AFMF3: &str = "gpu.afmf3";
pub const METHOD_GPU_CONTROL_PANEL: &str = "gpu.controlPanel";
pub const METHOD_GPU_DISPLAY: &str = "gpu.display";
pub const METHOD_GPU_DRIVER: &str = "gpu.driver";
pub const METHOD_GPU_PROCESSES: &str = "gpu.processes";
pub const METHOD_GPU_PROFILE: &str = "gpu.profile";
pub const METHOD_GPU_TUNING_APPLY: &str = "gpu.tuning.apply";
pub const METHOD_GPU_TUNING_RESET: &str = "gpu.tuning.reset";
pub const METHOD_GPU_TUNING_STATE: &str = "gpu.tuning.state";
pub const METHOD_GPU_TUNING_TEST: &str = "gpu.tuning.test";
pub const METHOD_GPU_TUNING_UNDERVOLT: &str = "gpu.tuning.undervolt";
pub const METHOD_GPU_TUNING_WAIVER: &str = "gpu.tuning.waiver";
pub const METHOD_HDR_CALIBRATE: &str = "hdr.calibrate";
pub const METHOD_HDR_STATE: &str = "hdr.state";
pub const METHOD_JUNK_CLEAN: &str = "junk.clean";
pub const METHOD_JUNK_SCAN: &str = "junk.scan";
pub const METHOD_KEYBOARD_BACKUP: &str = "keyboard.backup";
pub const METHOD_KEYBOARD_BACKUPS: &str = "keyboard.backups";
pub const METHOD_KEYBOARD_OPEN_APP: &str = "keyboard.openApp";
pub const METHOD_KEYBOARD_PROFILE: &str = "keyboard.profile";
pub const METHOD_KEYBOARD_READ: &str = "keyboard.read";
pub const METHOD_KEYBOARD_RESTORE: &str = "keyboard.restore";
pub const METHOD_KEYBOARD_SETTINGS: &str = "keyboard.settings";
pub const METHOD_LIBRARY_GET: &str = "library.get";
pub const METHOD_LIBRARY_REFRESH: &str = "library.refresh";
pub const METHOD_GAME_GET: &str = "game.get";
pub const METHOD_GAME_EXTRAS: &str = "game.extras";
pub const METHOD_GAME_LAUNCH: &str = "game.launch";
pub const METHOD_GAME_STOP: &str = "game.stop";
pub const METHOD_GAME_INSTALL: &str = "game.install";
pub const METHOD_ACCOUNTS_LIST: &str = "accounts.list";
pub const METHOD_ACCOUNTS_SWITCH: &str = "accounts.switch";
pub const METHOD_GIFTS_ANSWER: &str = "gifts.answer";
pub const METHOD_GIFTS_LIST: &str = "gifts.list";
pub const METHOD_LIBRARY_FOLDER_ART: &str = "library.folderArt";
pub const METHOD_LIBRARY_REFRESH_ALL: &str = "library.refreshAll";
pub const METHOD_SHELL_PICK_FOLDER: &str = "shell.pickFolder";
pub const METHOD_SHELL_PICK_PROGRAM: &str = "shell.pickProgram";
pub const METHOD_GAME_UPDATE: &str = "game.update";
pub const METHOD_GAME_UNINSTALL: &str = "game.uninstall";
pub const METHOD_GAME_REPAIR: &str = "game.repair";
pub const METHOD_GAME_OPEN_FOLDER: &str = "game.openFolder";
pub const METHOD_GAME_CANCEL_INSTALL: &str = "game.cancelInstall";
pub const METHOD_GAME_DEQUEUE: &str = "game.dequeue";
pub const METHOD_GAME_PAUSE_INSTALL: &str = "game.pauseInstall";
pub const METHOD_GAME_PROGRESS: &str = "game.progress";
pub const METHOD_GAME_PROPERTIES: &str = "game.properties";
pub const METHOD_GAME_PROPERTIES_SET: &str = "game.properties.set";
pub const METHOD_GAME_RESUME_DOWNLOAD: &str = "game.resumeDownload";
pub const METHOD_GAME_SETTINGS: &str = "game.settings";
pub const METHOD_GAME_GRAPHICS_FEATURES: &str = "game.graphicsFeatures";
pub const METHOD_GAME_GRAPHICS_FEATURES_SET: &str = "game.graphicsFeatures.set";
pub const METHOD_GAME_FRAME_GEN: &str = "game.frameGen";
pub const METHOD_KEYS_PREVIEW: &str = "keys.preview";
pub const METHOD_KEYS_ACTIVATE: &str = "keys.activate";
pub const METHOD_MOUSE_PANEL_BOUNDS: &str = "mouse.panel.bounds";
pub const METHOD_MOUSE_PANEL_CLOSE: &str = "mouse.panel.close";
pub const METHOD_MOUSE_PANEL_OPEN: &str = "mouse.panel.open";
pub const METHOD_MUSIC_CONTROL: &str = "music.control";
pub const METHOD_MUSIC_MATCH: &str = "music.match";
pub const METHOD_MUSIC_PLAYBACK: &str = "music.playback";
pub const METHOD_MUSIC_SIGN_IN: &str = "music.signIn";
pub const METHOD_MUSIC_VOLUME: &str = "music.volume";
pub const METHOD_MUSIC_WARM: &str = "music.warm";
pub const METHOD_PLAYTIME_RIOT_REFRESH: &str = "playtime.riotRefresh";
pub const METHOD_REPLACED_REMOVE: &str = "replaced.remove";
pub const METHOD_RUNTIMES_CANCEL: &str = "runtimes.cancel";
pub const METHOD_RUNTIMES_INSTALL: &str = "runtimes.install";
pub const METHOD_RUNTIMES_LIST: &str = "runtimes.list";
pub const METHOD_SETTINGS_GET: &str = "settings.get";
pub const METHOD_SETTINGS_SET: &str = "settings.set";
pub const METHOD_SHADERS_ADDON: &str = "shaders.addon";
pub const METHOD_SHADERS_CALL: &str = "shaders.call";
pub const METHOD_SHADERS_ESTIMATES: &str = "shaders.estimates";
pub const METHOD_SHADERS_PREPARE_CANCEL: &str = "shaders.prepare.cancel";
pub const METHOD_SHADERS_PREPARE_GAMES: &str = "shaders.prepare.games";
pub const METHOD_SHADERS_PREPARE_START: &str = "shaders.prepare.start";
pub const METHOD_SHADERS_REBUILD: &str = "shaders.rebuild";
pub const METHOD_SHADERS_STATUS: &str = "shaders.status";
pub const METHOD_RPC_CANCEL: &str = "rpc.cancel";
pub const METHOD_SHELL_BOOTSTRAP: &str = "shell.bootstrap";
pub const METHOD_SHELL_OPEN_URL: &str = "shell.openUrl";
pub const METHOD_SHELL_READY: &str = "shell.ready";
pub const METHOD_SHELL_WINDOW_FOCUS: &str = "shell.windowFocus";
pub const METHOD_SPOTIFY_ACT: &str = "spotify.act";
pub const METHOD_SPOTIFY_LINK: &str = "spotify.link";
pub const METHOD_SPOTIFY_START: &str = "spotify.start";
pub const METHOD_SPOTIFY_VIEW: &str = "spotify.view";
pub const METHOD_STARTUP_CLEAN: &str = "startup.clean";
pub const METHOD_STARTUP_LIST: &str = "startup.list";
pub const METHOD_STARTUP_REMOVE: &str = "startup.remove";
pub const METHOD_STARTUP_RESTORE: &str = "startup.restore";
pub const METHOD_STORE_HOME: &str = "store.home";
pub const METHOD_STORE_SEARCH: &str = "store.search";
pub const METHOD_STORE_GAME: &str = "store.game";
pub const METHOD_STORE_GAME_OFFERS: &str = "store.gameOffers";
pub const METHOD_STORE_GAME_REVIEWS: &str = "store.gameReviews";
pub const METHOD_STORE_OPEN: &str = "store.open";
pub const METHOD_STORE_WISHLIST: &str = "store.wishlist";
pub const METHOD_SYSTEM_ABOUT: &str = "system.about";
pub const METHOD_SYSTEM_DRIVERS: &str = "system.drivers";
pub const METHOD_SYSTEM_DRIVES: &str = "system.drives";
pub const METHOD_SYSTEM_HEALTH: &str = "system.health";
pub const METHOD_SYSTEM_INTERRUPTS: &str = "system.interrupts";
pub const METHOD_SYSTEM_OVERLAYS: &str = "system.overlays";
pub const METHOD_SYSTEM_QOS: &str = "system.qos";
pub const METHOD_SYSTEM_TUNE: &str = "system.tune";
pub const METHOD_SYSTEM_TUNING: &str = "system.tuning";
pub const METHOD_WEB_PREFERENCES_GET: &str = "web.preferences.get";
pub const METHOD_WEB_PREFERENCES_SET: &str = "web.preferences.set";
pub const METHOD_YTMUSIC_ACT: &str = "ytmusic.act";
pub const METHOD_YTMUSIC_START: &str = "ytmusic.start";
pub const METHOD_YTMUSIC_VIEW: &str = "ytmusic.view";
pub const METHOD_ACCOUNTS_ADD: &str = "accounts.add";
pub const METHOD_ACCOUNTS_REMOVE: &str = "accounts.remove";
pub const METHOD_ACCOUNTS_SIGN_OUT: &str = "accounts.signOut";
pub const METHOD_ACCOUNTS_EPIC_ADD: &str = "accounts.epicAdd";
pub const METHOD_ACCOUNTS_EPIC_IMPORT: &str = "accounts.epicImport";
pub const METHOD_ACCOUNTS_EPIC_REMOVE: &str = "accounts.epicRemove";
pub const METHOD_ACCOUNTS_EPIC_NEW_LAUNCHER: &str = "accounts.epicNewLauncher";
pub const METHOD_ACHIEVEMENTS_PREVIEW: &str = "achievements.preview";
pub const METHOD_BACKUP_NOW: &str = "backup.now";
pub const METHOD_BACKUP_EXPORT: &str = "backup.export";
pub const METHOD_BACKUP_IMPORT: &str = "backup.import";
pub const METHOD_BACKUP_RESTORE: &str = "backup.restore";
pub const METHOD_BACKUP_DECLINE: &str = "backup.decline";
pub const METHOD_CLIENTS_FIX: &str = "clients.fix";
pub const METHOD_EXO_ACCOUNT_OVERVIEW: &str = "exoAccount.overview";
pub const METHOD_EXO_ACCOUNT_CREATE: &str = "exoAccount.create";
pub const METHOD_EXO_ACCOUNT_SIGN_IN: &str = "exoAccount.signIn";
pub const METHOD_EXO_ACCOUNT_RESET: &str = "exoAccount.reset";
pub const METHOD_EXO_ACCOUNT_CHANGE_PASSWORD: &str = "exoAccount.changePassword";
pub const METHOD_EXO_ACCOUNT_NEW_RECOVERY_CODE: &str = "exoAccount.newRecoveryCode";
pub const METHOD_EXO_ACCOUNT_SAVE_CODE: &str = "exoAccount.saveCode";
pub const METHOD_EXO_ACCOUNT_CODE_CONFIRMED: &str = "exoAccount.codeConfirmed";
pub const METHOD_EXO_ACCOUNT_SESSIONS: &str = "exoAccount.sessions";
pub const METHOD_EXO_ACCOUNT_REVOKE: &str = "exoAccount.revoke";
pub const METHOD_EXO_ACCOUNT_EXPORT: &str = "exoAccount.export";
pub const METHOD_EXO_ACCOUNT_DELETE: &str = "exoAccount.delete";
pub const METHOD_EXO_ACCOUNT_PROBE: &str = "exoAccount.probe";
pub const METHOD_EXO_ACCOUNT_PASSKEY_STATUS: &str = "exoAccount.passkeyStatus";
pub const METHOD_EXO_ACCOUNT_PASSKEY_REGISTER: &str = "exoAccount.passkeyRegister";
pub const METHOD_EXO_ACCOUNT_PASSKEY_SIGN_IN: &str = "exoAccount.passkeySignIn";
pub const METHOD_EXO_ACCOUNT_PASSKEY_DELETE: &str = "exoAccount.passkeyDelete";
pub const METHOD_GIFTS_STEAM_SIGN_IN: &str = "gifts.steamSignIn";
pub const METHOD_GIFTS_STEAM_SIGN_OUT: &str = "gifts.steamSignOut";
pub const METHOD_PLAYTIME_IMPORT_RIOT_EXPORT: &str = "playtime.importRiotExport";
pub const METHOD_PLAYTIME_SET_RIOT_BASELINE: &str = "playtime.setRiotBaseline";
pub const METHOD_REPLACED_SCAN: &str = "replaced.scan";
pub const METHOD_REPORT_PREVIEW: &str = "report.preview";
pub const METHOD_REPORT_SEND: &str = "report.send";
pub const METHOD_SHELL_SHOW_STORE: &str = "shell.showStore";
pub const METHOD_STEAM_ACCOUNT: &str = "steam.account";
pub const METHOD_STEAM_SET_OFFLINE: &str = "steam.setOffline";
pub const METHOD_STEAM_SIGN_IN_QR: &str = "steam.signInQr";
pub const METHOD_STEAM_SIGN_IN_POLL: &str = "steam.signInPoll";
pub const METHOD_STEAM_SIGN_IN_PASSWORD: &str = "steam.signInPassword";
pub const METHOD_STEAM_SIGN_IN_GUARD: &str = "steam.signInGuard";
pub const METHOD_STEAM_SIGN_IN_CANCEL: &str = "steam.signInCancel";
pub const METHOD_STEAM_SIGN_OUT: &str = "steam.signOut";
pub const METHOD_STORES_AUTH: &str = "stores.auth";
pub const METHOD_STORES_CHECK: &str = "stores.check";
pub const METHOD_STORES_MATRIX: &str = "stores.matrix";
pub const METHOD_STORES_PLAY_OFFLINE: &str = "stores.playOffline";
pub const METHOD_TELEMETRY_SENT: &str = "telemetry.sent";
pub const METHOD_TELEMETRY_DELETE: &str = "telemetry.delete";

pub const USED_METHODS: &[&str] = &[
    METHOD_APP_CHECK_UPDATE,
    METHOD_APP_INSTALL_UPDATE,
    METHOD_APP_OPEN_LOGS,
    METHOD_APP_REPAIR_CACHE,
    METHOD_APPLEMUSIC_ACT,
    METHOD_APPLEMUSIC_START,
    METHOD_APPLEMUSIC_VIEW,
    METHOD_APPS_ALLOW_EDGE,
    METHOD_APPS_CLEAN,
    METHOD_APPS_CONFIRM,
    METHOD_APPS_ICONS,
    METHOD_APPS_INSTALLER_CACHE,
    METHOD_APPS_INSTALLER_CACHE_CLEAN,
    METHOD_APPS_LEAVE,
    METHOD_APPS_LIST,
    METHOD_APPS_OPEN_DEFAULTS,
    METHOD_APPS_PACKAGE_ADD_BACK,
    METHOD_APPS_PACKAGES,
    METHOD_APPS_PACKAGES_PLAN,
    METHOD_APPS_PACKAGES_REMOVE,
    METHOD_APPS_RESTORE_POINT,
    METHOD_APPS_SCAN,
    METHOD_APPS_TIDY,
    METHOD_APPS_UNDO,
    METHOD_APPS_UNINSTALL,
    METHOD_ART_BASE,
    METHOD_ART_REFETCH,
    METHOD_ART_REPLACE,
    METHOD_ART_RESET,
    METHOD_AUDIO_FORMATS,
    METHOD_COMPRESS_ACT,
    METHOD_COMPRESS_STATE,
    METHOD_DLSS_CONSENT,
    METHOD_DLSS_DOWNLOAD_NEWER,
    METHOD_DLSS_FRAME_GEN,
    METHOD_DLSS_NEWER,
    METHOD_DLSS_RESTORE,
    METHOD_DLSS_STATUS,
    METHOD_DLSS_UPDATE_ALL,
    METHOD_GPU_GAME,
    METHOD_HDR_GAME,
    METHOD_KEEP_STATUS,
    METHOD_AUDIO_READ,
    METHOD_AUDIO_SET_FORMAT,
    METHOD_AUDIO_SET_MUTE,
    METHOD_AUDIO_SET_VOLUME,
    METHOD_AUTH_NATIVE_PROVIDERS,
    METHOD_AUTOPILOT_SET,
    METHOD_AUTOPILOT_STATE,
    METHOD_BIOS_UPDATE,
    METHOD_CAPTURE_ACT,
    METHOD_CAPTURE_EDIT,
    METHOD_CAPTURE_ITEM,
    METHOD_CAPTURE_LIST,
    METHOD_CAPTURE_REEL,
    METHOD_CAPTURE_REEL_CANCEL,
    METHOD_CAPTURE_SAVE_IMAGE,
    METHOD_CAPTURE_SEND,
    METHOD_CAPTURE_SETTINGS,
    METHOD_CAPTURE_STATUS,
    METHOD_CAPTURE_TEXT,
    METHOD_COMPRESS_ACT,
    METHOD_COMPRESS_ALL,
    METHOD_COMPRESS_LIST,
    METHOD_COMPRESS_SETTINGS,
    METHOD_CONTROLLER_CALIBRATE,
    METHOD_CONTROLLER_GAME_PROFILES,
    METHOD_CONTROLLER_GAME_PROFILES_SAVE,
    METHOD_CONTROLLER_MEASURE,
    METHOD_CONTROLLER_QUALITY,
    METHOD_CONTROLLER_READ,
    METHOD_CONTROLLER_SETTINGS,
    METHOD_CONTROLLER_SOUND,
    METHOD_CONTROLLER_TEST,
    METHOD_CONTROLLER_WATCH,
    METHOD_DEVICES_IMAGE,
    METHOD_DEVICES_LIST,
    METHOD_DISCORD_ACCOUNT,
    METHOD_DISCORD_ACCOUNTS,
    METHOD_DISCORD_ACK,
    METHOD_DISCORD_ADMIN,
    METHOD_DISCORD_APPS,
    METHOD_DISCORD_AUDIO,
    METHOD_DISCORD_CALL_SONGS,
    METHOD_DISCORD_CAMERA,
    METHOD_DISCORD_CHANNEL,
    METHOD_DISCORD_CHANNELS,
    METHOD_DISCORD_CLIENT_SETTINGS,
    METHOD_DISCORD_COLORS,
    METHOD_DISCORD_DELETE,
    METHOD_DISCORD_EDIT,
    METHOD_DISCORD_EMOJI_FAVORITES,
    METHOD_DISCORD_EMOJIS,
    METHOD_DISCORD_FILE_TEXT,
    METHOD_DISCORD_FONT,
    METHOD_DISCORD_FOR_YOU,
    METHOD_DISCORD_FRIEND,
    METHOD_DISCORD_FRIENDS,
    METHOD_DISCORD_GIF_FAVORITES,
    METHOD_DISCORD_GIFS,
    METHOD_DISCORD_GOLIVE,
    METHOD_DISCORD_HOME,
    METHOD_DISCORD_IMAGE,
    METHOD_DISCORD_MARK_READ,
    METHOD_DISCORD_MEMBERS,
    METHOD_DISCORD_MENTIONABLE,
    METHOD_DISCORD_MENTIONS,
    METHOD_DISCORD_MESSAGE_ACTION,
    METHOD_DISCORD_MESSAGES,
    METHOD_DISCORD_MIC_MONITOR,
    METHOD_DISCORD_MIC_TEST,
    METHOD_DISCORD_MY_VOICE,
    METHOD_DISCORD_PINS,
    METHOD_DISCORD_POLL,
    METHOD_DISCORD_PROFILE,
    METHOD_DISCORD_REACT,
    METHOD_DISCORD_REACTORS,
    METHOD_DISCORD_REPORT,
    METHOD_DISCORD_RING,
    METHOD_DISCORD_SAVED,
    METHOD_DISCORD_SEARCH,
    METHOD_DISCORD_SEND,
    METHOD_DISCORD_SERVER,
    METHOD_DISCORD_SET_STATUS,
    METHOD_DISCORD_SHOP,
    METHOD_DISCORD_SIGN_IN,
    METHOD_DISCORD_SIGN_OUT,
    METHOD_DISCORD_SOUND,
    METHOD_DISCORD_SOUNDBOARD,
    METHOD_DISCORD_STATUS,
    METHOD_DISCORD_STICKER,
    METHOD_DISCORD_STICKER_DATA,
    METHOD_DISCORD_STICKERS,
    METHOD_DISCORD_STREAM,
    METHOD_DISCORD_THREAD,
    METHOD_DISCORD_THREADS,
    METHOD_DISCORD_TYPING,
    METHOD_DISCORD_UNREADS,
    METHOD_DISCORD_UPLOAD,
    METHOD_DISCORD_USER_SETTINGS,
    METHOD_DISCORD_VIEW,
    METHOD_DISCORD_VOICE,
    METHOD_FRIENDS_BADGE,
    METHOD_EQ,
    METHOD_EQ_HEADPHONES,
    METHOD_HID_CLOSE,
    METHOD_HID_LIST,
    METHOD_HID_OPEN,
    METHOD_HID_RECEIVE_FEATURE_REPORT,
    METHOD_HID_RECEIVE_INPUT_REPORT,
    METHOD_HID_SEND_FEATURE_REPORT,
    METHOD_HID_SEND_REPORT,
    METHOD_MOUSE_PRESETS_GET,
    METHOD_MOUSE_PRESETS_SET,
    METHOD_MOUSE_PROFILES_GET,
    METHOD_MOUSE_PROFILES_SET,
    METHOD_NOTES_ACT,
    METHOD_NOTES_LIST,
    METHOD_NOTES_OPEN,
    METHOD_NOTES_PENDING,
    METHOD_NOTES_PHOTO,
    METHOD_NOTES_READ,
    METHOD_NOTES_SAVE,
    METHOD_NOTES_SAVE_AS,
    METHOD_NOTES_SESSION,
    METHOD_NOTES_SPELL,
    METHOD_NOTES_TEXT_FILES,
    METHOD_STEAM_INPUT_STATE,
    METHOD_EXO_ACCOUNT_SIGN_OUT,
    METHOD_EXO_ACCOUNT_STATE,
    METHOD_EXO_ACCOUNT_SYNC,
    METHOD_EXO_AI_SET,
    METHOD_EXO_AI_STATE,
    METHOD_FPS_COUNTER,
    METHOD_GPU_AFMF3,
    METHOD_GPU_CONTROL_PANEL,
    METHOD_GPU_DISPLAY,
    METHOD_GPU_DRIVER,
    METHOD_GPU_PROCESSES,
    METHOD_GPU_PROFILE,
    METHOD_GPU_TUNING_APPLY,
    METHOD_GPU_TUNING_RESET,
    METHOD_GPU_TUNING_STATE,
    METHOD_GPU_TUNING_TEST,
    METHOD_GPU_TUNING_UNDERVOLT,
    METHOD_GPU_TUNING_WAIVER,
    METHOD_HDR_CALIBRATE,
    METHOD_HDR_STATE,
    METHOD_JUNK_CLEAN,
    METHOD_JUNK_SCAN,
    METHOD_KEEP_STATUS,
    METHOD_KEYBOARD_BACKUP,
    METHOD_KEYBOARD_BACKUPS,
    METHOD_KEYBOARD_OPEN_APP,
    METHOD_KEYBOARD_PROFILE,
    METHOD_KEYBOARD_READ,
    METHOD_KEYBOARD_RESTORE,
    METHOD_KEYBOARD_SETTINGS,
    METHOD_ACCOUNTS_LIST,
    METHOD_ACCOUNTS_SWITCH,
    METHOD_GIFTS_ANSWER,
    METHOD_GIFTS_LIST,
    METHOD_LIBRARY_FOLDER_ART,
    METHOD_LIBRARY_GET,
    METHOD_LIBRARY_REFRESH,
    METHOD_LIBRARY_REFRESH_ALL,
    METHOD_SHELL_PICK_FOLDER,
    METHOD_SHELL_PICK_PROGRAM,
    METHOD_GAME_GET,
    METHOD_GAME_EXTRAS,
    METHOD_GAME_LAUNCH,
    METHOD_GAME_STOP,
    METHOD_GAME_INSTALL,
    METHOD_GAME_UPDATE,
    METHOD_GAME_UNINSTALL,
    METHOD_GAME_REPAIR,
    METHOD_GAME_OPEN_FOLDER,
    METHOD_GAME_CANCEL_INSTALL,
    METHOD_GAME_DEQUEUE,
    METHOD_GAME_PAUSE_INSTALL,
    METHOD_GAME_PROGRESS,
    METHOD_GAME_PROPERTIES,
    METHOD_GAME_PROPERTIES_SET,
    METHOD_GAME_RESUME_DOWNLOAD,
    METHOD_GAME_SETTINGS,
    METHOD_GAME_GRAPHICS_FEATURES,
    METHOD_GAME_GRAPHICS_FEATURES_SET,
    METHOD_GAME_FRAME_GEN,
    METHOD_KEYS_PREVIEW,
    METHOD_KEYS_ACTIVATE,
    METHOD_MOUSE_PANEL_BOUNDS,
    METHOD_MOUSE_PANEL_CLOSE,
    METHOD_MOUSE_PANEL_OPEN,
    METHOD_MUSIC_CONTROL,
    METHOD_MUSIC_MATCH,
    METHOD_MUSIC_PLAYBACK,
    METHOD_MUSIC_SIGN_IN,
    METHOD_MUSIC_VOLUME,
    METHOD_MUSIC_WARM,
    METHOD_PLAYTIME_RIOT_REFRESH,
    METHOD_REPLACED_REMOVE,
    METHOD_RUNTIMES_CANCEL,
    METHOD_RUNTIMES_INSTALL,
    METHOD_RUNTIMES_LIST,
    METHOD_SETTINGS_GET,
    METHOD_SETTINGS_SET,
    METHOD_SHADERS_ADDON,
    METHOD_BACKUP_STATUS,
    METHOD_CLIENTS_STATUS,
    METHOD_SHADERS_CALL,
    METHOD_SHADERS_ESTIMATES,
    METHOD_SHADERS_PREPARE_CANCEL,
    METHOD_SHADERS_PREPARE_GAMES,
    METHOD_SHADERS_PREPARE_START,
    METHOD_SHADERS_REBUILD,
    METHOD_SHADERS_STATUS,
    METHOD_SHELL_BOOTSTRAP,
    METHOD_SHELL_OPEN_URL,
    METHOD_SHELL_READY,
    METHOD_SPOTIFY_ACT,
    METHOD_SPOTIFY_LINK,
    METHOD_SPOTIFY_START,
    METHOD_SPOTIFY_VIEW,
    METHOD_STARTUP_CLEAN,
    METHOD_STARTUP_LIST,
    METHOD_STARTUP_REMOVE,
    METHOD_STARTUP_RESTORE,
    METHOD_STORE_HOME,
    METHOD_STORE_SEARCH,
    METHOD_STORE_GAME,
    METHOD_STORE_GAME_OFFERS,
    METHOD_STORE_GAME_REVIEWS,
    METHOD_STORE_OPEN,
    METHOD_STORE_WISHLIST,
    METHOD_SYSTEM_ABOUT,
    METHOD_ACCOUNTS_LIST,
    METHOD_ACCOUNTS_ADD,
    METHOD_ACCOUNTS_SWITCH,
    METHOD_ACCOUNTS_REMOVE,
    METHOD_ACCOUNTS_SIGN_OUT,
    METHOD_ACCOUNTS_EPIC_ADD,
    METHOD_ACCOUNTS_EPIC_IMPORT,
    METHOD_ACCOUNTS_EPIC_REMOVE,
    METHOD_ACCOUNTS_EPIC_NEW_LAUNCHER,
    METHOD_ACHIEVEMENTS_PREVIEW,
    METHOD_BACKUP_NOW,
    METHOD_BACKUP_EXPORT,
    METHOD_BACKUP_IMPORT,
    METHOD_BACKUP_RESTORE,
    METHOD_BACKUP_DECLINE,
    METHOD_CLIENTS_FIX,
    METHOD_EXO_ACCOUNT_OVERVIEW,
    METHOD_EXO_ACCOUNT_CREATE,
    METHOD_EXO_ACCOUNT_SIGN_IN,
    METHOD_EXO_ACCOUNT_RESET,
    METHOD_EXO_ACCOUNT_CHANGE_PASSWORD,
    METHOD_EXO_ACCOUNT_NEW_RECOVERY_CODE,
    METHOD_EXO_ACCOUNT_SAVE_CODE,
    METHOD_EXO_ACCOUNT_CODE_CONFIRMED,
    METHOD_EXO_ACCOUNT_SESSIONS,
    METHOD_EXO_ACCOUNT_REVOKE,
    METHOD_EXO_ACCOUNT_EXPORT,
    METHOD_EXO_ACCOUNT_DELETE,
    METHOD_EXO_ACCOUNT_PROBE,
    METHOD_EXO_ACCOUNT_PASSKEY_STATUS,
    METHOD_EXO_ACCOUNT_PASSKEY_REGISTER,
    METHOD_EXO_ACCOUNT_PASSKEY_SIGN_IN,
    METHOD_EXO_ACCOUNT_PASSKEY_DELETE,
    METHOD_GIFTS_LIST,
    METHOD_GIFTS_STEAM_SIGN_IN,
    METHOD_GIFTS_STEAM_SIGN_OUT,
    METHOD_PLAYTIME_IMPORT_RIOT_EXPORT,
    METHOD_PLAYTIME_SET_RIOT_BASELINE,
    METHOD_REPLACED_SCAN,
    METHOD_REPLACED_REMOVE,
    METHOD_REPORT_PREVIEW,
    METHOD_REPORT_SEND,
    METHOD_SHELL_PICK_FOLDER,
    METHOD_SHELL_SHOW_STORE,
    METHOD_STEAM_ACCOUNT,
    METHOD_STEAM_SET_OFFLINE,
    METHOD_STEAM_SIGN_IN_QR,
    METHOD_STEAM_SIGN_IN_POLL,
    METHOD_STEAM_SIGN_IN_PASSWORD,
    METHOD_STEAM_SIGN_IN_GUARD,
    METHOD_STEAM_SIGN_IN_CANCEL,
    METHOD_STEAM_SIGN_OUT,
    METHOD_STORES_AUTH,
    METHOD_STORES_CHECK,
    METHOD_STORES_MATRIX,
    METHOD_STORES_PLAY_OFFLINE,
    METHOD_TELEMETRY_SENT,
    METHOD_TELEMETRY_DELETE,
    METHOD_SYSTEM_DRIVERS,
    METHOD_SYSTEM_DRIVES,
    METHOD_SYSTEM_HEALTH,
    METHOD_SYSTEM_INTERRUPTS,
    METHOD_SYSTEM_OVERLAYS,
    METHOD_SYSTEM_QOS,
    METHOD_SYSTEM_TUNE,
    METHOD_SYSTEM_TUNING,
    METHOD_WEB_PREFERENCES_GET,
    METHOD_WEB_PREFERENCES_SET,
    METHOD_YTMUSIC_ACT,
    METHOD_YTMUSIC_START,
    METHOD_YTMUSIC_VIEW,
];

#[derive(Debug)]
pub enum RpcError {
    Io(io::Error),
    Json(serde_json::Error),
    Wire(exo_wire::WireError),
    Host(String),
    Disconnected,
    Timeout,
}

impl fmt::Display for RpcError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(e) => write!(f, "pipe io: {e}"),
            Self::Json(e) => write!(f, "json: {e}"),
            Self::Wire(e) => write!(f, "wire: {e}"),
            Self::Host(msg) => f.write_str(msg),
            Self::Disconnected => f.write_str("host disconnected"),
            Self::Timeout => f.write_str("rpc timed out"),
        }
    }
}

impl std::error::Error for RpcError {}

impl From<io::Error> for RpcError {
    fn from(value: io::Error) -> Self {
        Self::Io(value)
    }
}

impl From<serde_json::Error> for RpcError {
    fn from(value: serde_json::Error) -> Self {
        Self::Json(value)
    }
}

impl From<exo_wire::WireError> for RpcError {
    fn from(value: exo_wire::WireError) -> Self {
        Self::Wire(value)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WirePreference {
    Auto,
    Binary,
    Json,
}

impl WirePreference {
    pub fn from_env() -> Self {
        match std::env::var("EXO_RPC_WIRE")
            .ok()
            .as_deref()
            .map(str::trim)
            .map(str::to_ascii_lowercase)
            .as_deref()
        {
            Some("json") => Self::Json,
            Some("binary") | Some("wire") | Some("exw1") => Self::Binary,
            _ => Self::Auto,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ActiveWireMode {
    Binary {
        use_schema_ids: bool,
        binary_hot_codecs: bool,
    },
    Json,
}

pub(crate) enum WirePayload {
    Json(Box<RawValue>),
    Binary { method_id: u16, bytes: Vec<u8> },
}

type PendingCallback = Box<dyn FnOnce(Result<WirePayload, RpcError>) + Send + 'static>;
type VideoFrameHandler =
    Arc<dyn Fn(DiscordVideoFrame) -> Result<(), DiscordVideoFrame> + Send + Sync>;

const OUTBOUND_QUEUE_CAP: usize = 256;
const EVENT_QUEUE_CAP: usize = 256;
const VIDEO_FRAME_QUEUE_CAP: usize = 8;

enum DispatchWork {
    Response(PendingCallback, Result<WirePayload, RpcError>),
    Event(Box<HostEvent>),
    VideoFrame(DiscordVideoFrame),
}

struct BoundedQueueState<T> {
    items: VecDeque<T>,
    closed: bool,
}

struct BoundedQueue<T> {
    cap: usize,
    state: Mutex<BoundedQueueState<T>>,
    cond: Condvar,
}

impl<T> BoundedQueue<T> {
    fn new(cap: usize) -> Self {
        let cap = cap.max(1);
        Self {
            cap,
            state: Mutex::new(BoundedQueueState {
                items: VecDeque::with_capacity(cap.min(64)),
                closed: false,
            }),
            cond: Condvar::new(),
        }
    }

    fn push_drop_oldest(&self, item: T) -> bool {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if guard.closed {
            return false;
        }
        if guard.items.len() >= self.cap {
            guard.items.pop_front();
        }
        guard.items.push_back(item);
        self.cond.notify_one();
        true
    }

    fn pop_blocking(&self) -> Option<T> {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if let Some(item) = guard.items.pop_front() {
                return Some(item);
            }
            if guard.closed {
                return None;
            }
            guard = self.cond.wait(guard).unwrap_or_else(|e| e.into_inner());
        }
    }

    fn close(&self) {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        guard.closed = true;
        guard.items.clear();
        self.cond.notify_all();
    }
}

struct DispatchQueueState {
    responses: VecDeque<(PendingCallback, Result<WirePayload, RpcError>)>,
    events: VecDeque<HostEvent>,
    video_frames: VecDeque<DiscordVideoFrame>,
    closed: bool,
}

struct DispatchQueue {
    event_cap: usize,
    video_cap: usize,
    state: Mutex<DispatchQueueState>,
    cond: Condvar,
}

impl DispatchQueue {
    fn new(event_cap: usize, video_cap: usize) -> Self {
        Self {
            event_cap: event_cap.max(1),
            video_cap: video_cap.max(1),
            state: Mutex::new(DispatchQueueState {
                responses: VecDeque::new(),
                events: VecDeque::with_capacity(event_cap.clamp(1, 64)),
                video_frames: VecDeque::with_capacity(video_cap.clamp(1, 16)),
                closed: false,
            }),
            cond: Condvar::new(),
        }
    }

    fn send(&self, work: DispatchWork) -> bool {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if guard.closed {
            return false;
        }
        match work {
            DispatchWork::Response(cb, res) => {
                guard.responses.push_back((cb, res));
            }
            DispatchWork::Event(ev) => {
                let ev = *ev;
                if matches!(ev, HostEvent::MusicPlayback(_))
                    && let Some(slot) = guard
                        .events
                        .iter_mut()
                        .rev()
                        .find(|e| matches!(e, HostEvent::MusicPlayback(_)))
                {
                    *slot = ev;
                    self.cond.notify_one();
                    return true;
                }
                if guard.events.len() >= self.event_cap {
                    guard.events.pop_front();
                }
                guard.events.push_back(ev);
            }
            DispatchWork::VideoFrame(frame) => {
                if guard.video_frames.len() >= self.video_cap {
                    guard.video_frames.pop_front();
                }
                guard.video_frames.push_back(frame);
            }
        }
        self.cond.notify_one();
        true
    }

    fn pop_blocking(&self) -> Option<DispatchWork> {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if let Some((cb, res)) = guard.responses.pop_front() {
                return Some(DispatchWork::Response(cb, res));
            }
            if let Some(ev) = guard.events.pop_front() {
                return Some(DispatchWork::Event(Box::new(ev)));
            }
            if let Some(frame) = guard.video_frames.pop_front() {
                return Some(DispatchWork::VideoFrame(frame));
            }
            if guard.closed {
                return None;
            }
            guard = self.cond.wait(guard).unwrap_or_else(|e| e.into_inner());
        }
    }

    fn close(&self) {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        guard.closed = true;
        self.cond.notify_all();
    }
}

#[derive(Deserialize)]
struct WireEnvelope<'a> {
    #[serde(borrow, default)]
    id: Option<Cow<'a, str>>,
    #[serde(default)]
    ok: Option<bool>,
    #[serde(borrow, default)]
    result: Option<&'a RawValue>,
    #[serde(borrow, default)]
    error: Option<Cow<'a, str>>,
    #[serde(borrow, default)]
    event: Option<Cow<'a, str>>,
    #[serde(borrow, default)]
    data: Option<&'a RawValue>,
    #[serde(borrow, default)]
    meta: Option<&'a RawValue>,
}

fn null_raw() -> Box<RawValue> {
    RawValue::from_string("null".to_string()).expect("valid null raw json")
}

pub fn load_host_contract(json: &str) -> Result<HostContract, serde_json::Error> {
    serde_json::from_str(json)
}

#[cfg(windows)]
type MediaPipeSlot = Arc<Mutex<Option<(String, Arc<pipe::PipeHandle>)>>>;

#[cfg(windows)]
pub struct RpcClient {
    pipe: Arc<pipe::PipeHandle>,
    outbound: Arc<BoundedQueue<Vec<u8>>>,
    pending: Arc<Mutex<HashMap<String, PendingCallback>>>,
    next_id: AtomicU64,
    closed: Arc<AtomicBool>,
    dispatch_tx: Arc<DispatchQueue>,
    video_handler: Arc<Mutex<Option<VideoFrameHandler>>>,
    media_pipe: MediaPipeSlot,
    wire_mode: ActiveWireMode,
}

#[cfg(windows)]
fn negotiate_wire(
    pipe: &pipe::PipeHandle,
    pref: WirePreference,
) -> Result<(ActiveWireMode, Vec<u8>), RpcError> {
    if pref == WirePreference::Json {
        return Ok((ActiveWireMode::Json, Vec::new()));
    }
    let write_ev = pipe::EventHandle::new()?;
    let read_ev = pipe::EventHandle::new()?;
    let hello = exo_wire::encode_hello_frame(exo_wire::HelloPayload::current());
    pipe.write_all_overlapped(write_ev.raw(), &hello)?;

    let timeout_ms = if pref == WirePreference::Binary {
        2000
    } else {
        200
    };
    let mut initial = Vec::with_capacity(64);
    let mut tmp = [0u8; 512];
    let ack_total = exo_wire::HEADER_LEN + exo_wire::HELLO_PAYLOAD_LEN;

    let first = match pipe.read_overlapped_timeout(&read_ev, &mut tmp, timeout_ms) {
        Ok(Some(0)) => return Err(RpcError::Disconnected),
        Ok(Some(n)) => {
            initial.extend_from_slice(&tmp[..n]);
            true
        }
        Ok(None) => false,
        Err(e) => return Err(RpcError::Io(e)),
    };

    if !first {
        if pref == WirePreference::Binary {
            return Err(RpcError::Timeout);
        }
        let _ = pipe.write_all_overlapped(write_ev.raw(), b"\n");
        return Ok((ActiveWireMode::Json, Vec::new()));
    }

    if initial[0] != b'E' {
        if pref == WirePreference::Binary {
            return Err(RpcError::Wire(exo_wire::WireError::BadMagic(0)));
        }
        let _ = pipe.write_all_overlapped(write_ev.raw(), b"\n");
        return Ok((ActiveWireMode::Json, initial));
    }

    while initial.len() < ack_total {
        match pipe.read_overlapped_timeout(&read_ev, &mut tmp, timeout_ms) {
            Ok(Some(0)) => return Err(RpcError::Disconnected),
            Ok(Some(n)) => {
                initial.extend_from_slice(&tmp[..n]);
                if pref == WirePreference::Auto && initial[0] != exo_wire::WIRE_MAGIC_BYTES[0] {
                    let _ = pipe.write_all_overlapped(write_ev.raw(), b"\n");
                    return Ok((ActiveWireMode::Json, initial));
                }
            }
            Ok(None) => {
                if pref == WirePreference::Binary {
                    return Err(RpcError::Timeout);
                }
                let _ = pipe.write_all_overlapped(write_ev.raw(), b"\n");
                return Ok((ActiveWireMode::Json, initial));
            }
            Err(e) => return Err(RpcError::Io(e)),
        }
    }

    if initial[0..4] != exo_wire::WIRE_MAGIC_BYTES {
        if pref == WirePreference::Binary {
            return Err(RpcError::Wire(exo_wire::WireError::BadMagic(0)));
        }
        let _ = pipe.write_all_overlapped(write_ev.raw(), b"\n");
        return Ok((ActiveWireMode::Json, initial));
    }

    let hdr_bytes: [u8; exo_wire::HEADER_LEN] =
        initial[0..exo_wire::HEADER_LEN].try_into().unwrap();
    let hdr = exo_wire::FrameHeader::decode(&hdr_bytes)?;
    if hdr.kind != exo_wire::FrameKind::HelloAck
        || hdr.payload_len as usize != exo_wire::HELLO_PAYLOAD_LEN
    {
        return Err(RpcError::Wire(exo_wire::WireError::InvalidKind(
            hdr.kind as u8,
        )));
    }
    let ack = exo_wire::HelloAckPayload::decode(&initial[exo_wire::HEADER_LEN..ack_total])?;
    if ack.negotiated_version != exo_wire::WIRE_VERSION {
        return Err(RpcError::Wire(exo_wire::WireError::UnsupportedVersion(
            ack.negotiated_version,
        )));
    }
    let use_schema_ids = ack.status == exo_wire::HELLO_STATUS_OK
        && (ack.flags & exo_wire::FLAG_USE_SCHEMA_IDS) != 0
        && ack.host_schema_hash == exo_wire::SCHEMA_HASH;
    let binary_hot_codecs = (ack.flags & exo_wire::FLAG_BINARY_HOT_CODECS) != 0;
    let leftover = initial[ack_total..].to_vec();
    Ok((
        ActiveWireMode::Binary {
            use_schema_ids,
            binary_hot_codecs,
        },
        leftover,
    ))
}

#[derive(Serialize)]
struct RpcRequest<'a, P: ?Sized> {
    version: u32,
    id: &'a str,
    method: &'a str,
    params: &'a P,
}

pub fn serialize_request<P: Serialize + ?Sized>(
    id: &str,
    method: &str,
    params: &P,
) -> Result<String, serde_json::Error> {
    serde_json::to_string(&RpcRequest {
        version: PROTOCOL_VERSION,
        id,
        method,
        params,
    })
}

fn serialize_request_line<P: Serialize + ?Sized>(
    id: &str,
    method: &str,
    params: &P,
) -> Result<Vec<u8>, serde_json::Error> {
    let mut bytes = serde_json::to_vec(&RpcRequest {
        version: PROTOCOL_VERSION,
        id,
        method,
        params,
    })?;
    bytes.push(b'\n');
    Ok(bytes)
}

#[cfg(windows)]
impl RpcClient {
    pub fn connect(
        pipe_name: &str,
        timeout: Duration,
        on_event: impl Fn(HostEvent) + Send + Sync + 'static,
    ) -> Result<Arc<Self>, RpcError> {
        Self::connect_with_preference(pipe_name, timeout, WirePreference::from_env(), on_event)
    }

    pub fn connect_with_preference(
        pipe_name: &str,
        timeout: Duration,
        preference: WirePreference,
        on_event: impl Fn(HostEvent) + Send + Sync + 'static,
    ) -> Result<Arc<Self>, RpcError> {
        let pipe = pipe::PipeHandle::connect(pipe_name, timeout)?;
        let (wire_mode, initial_bytes) = negotiate_wire(&pipe, preference)?;
        let outbound = Arc::new(BoundedQueue::<Vec<u8>>::new(OUTBOUND_QUEUE_CAP));
        let dispatch_tx = Arc::new(DispatchQueue::new(EVENT_QUEUE_CAP, VIDEO_FRAME_QUEUE_CAP));
        let pending: Arc<Mutex<HashMap<String, PendingCallback>>> =
            Arc::new(Mutex::new(HashMap::new()));
        let closed = Arc::new(AtomicBool::new(false));
        let on_event: Arc<dyn Fn(HostEvent) + Send + Sync> = Arc::new(on_event);
        let video_handler: Arc<Mutex<Option<VideoFrameHandler>>> = Arc::new(Mutex::new(None));
        let media_pipe: MediaPipeSlot = Arc::new(Mutex::new(None));

        let dispatch_events = Arc::clone(&on_event);
        let dispatch_video = Arc::clone(&video_handler);
        let dispatch_rx = Arc::clone(&dispatch_tx);
        std::thread::Builder::new()
            .name("exo-rpc-dispatch".into())
            .spawn(move || {
                while let Some(work) = dispatch_rx.pop_blocking() {
                    match work {
                        DispatchWork::Response(cb, res) => cb(res),
                        DispatchWork::Event(ev) => dispatch_events(*ev),
                        DispatchWork::VideoFrame(frame) => {
                            let handler = dispatch_video
                                .lock()
                                .unwrap_or_else(|e| e.into_inner())
                                .clone();
                            let unrouted = match handler {
                                Some(h) => h(frame).err(),
                                None => Some(frame),
                            };
                            if let Some(frame) = unrouted {
                                dispatch_events(HostEvent::DiscordVideoFrame(frame));
                            }
                        }
                    }
                }
            })?;

        let write_pipe = Arc::clone(&pipe);
        let write_closed = Arc::clone(&closed);
        let write_rx = Arc::clone(&outbound);
        std::thread::Builder::new()
            .name("exo-rpc-writer".into())
            .spawn(move || {
                let Ok(event) = pipe::EventHandle::new() else {
                    return;
                };
                while let Some(bytes) = write_rx.pop_blocking() {
                    if write_closed.load(Ordering::Acquire) {
                        break;
                    }
                    if write_pipe
                        .write_all_overlapped(event.raw(), &bytes)
                        .is_err()
                    {
                        break;
                    }
                }
            })?;

        let read_pipe = Arc::clone(&pipe);
        let read_pending = Arc::clone(&pending);
        let read_closed = Arc::clone(&closed);
        let read_dispatch = Arc::clone(&dispatch_tx);
        let read_media = Arc::clone(&media_pipe);
        std::thread::Builder::new()
            .name("exo-rpc-reader".into())
            .spawn(move || {
                let reason = match wire_mode {
                    ActiveWireMode::Binary { .. } => {
                        read_wire_loop(&read_pipe, initial_bytes, &read_pending, &read_dispatch)
                    }
                    ActiveWireMode::Json => {
                        read_loop(&read_pipe, initial_bytes, &read_pending, &read_dispatch)
                    }
                };
                read_closed.store(true, Ordering::Release);
                if let Some((_, mp)) = read_media.lock().unwrap_or_else(|e| e.into_inner()).take() {
                    mp.cancel();
                }
                let mut map = read_pending.lock().unwrap_or_else(|e| e.into_inner());
                for (_, cb) in map.drain() {
                    let _ =
                        read_dispatch.send(DispatchWork::Response(cb, Err(RpcError::Disconnected)));
                }
                drop(map);
                let _ = read_dispatch.send(DispatchWork::Event(Box::new(HostEvent::Disconnected(
                    reason,
                ))));
            })?;

        Ok(Arc::new(Self {
            pipe,
            outbound,
            pending,
            next_id: AtomicU64::new(1),
            closed,
            dispatch_tx,
            video_handler,
            media_pipe,
            wire_mode,
        }))
    }

    pub fn wire_mode(&self) -> ActiveWireMode {
        self.wire_mode
    }

    pub fn set_video_frame_handler(
        &self,
        handler: impl Fn(DiscordVideoFrame) -> Result<(), DiscordVideoFrame> + Send + Sync + 'static,
    ) {
        *self.video_handler.lock().unwrap_or_else(|e| e.into_inner()) = Some(Arc::new(handler));
    }

    pub fn connect_media_pipe(&self, media_pipe_name: &str, timeout: Duration) -> io::Result<()> {
        if self.closed.load(Ordering::Acquire) || media_pipe_name.trim().is_empty() {
            return Ok(());
        }
        {
            let guard = self.media_pipe.lock().unwrap_or_else(|e| e.into_inner());
            if let Some((ref current, _)) = *guard
                && current == media_pipe_name
            {
                return Ok(());
            }
        }
        let handle = pipe::PipeHandle::connect_read_only(media_pipe_name, timeout)?;
        {
            let mut guard = self.media_pipe.lock().unwrap_or_else(|e| e.into_inner());
            if let Some((_, old)) =
                guard.replace((media_pipe_name.to_string(), Arc::clone(&handle)))
            {
                old.cancel();
            }
        }
        let closed = Arc::clone(&self.closed);
        let video_handler = Arc::clone(&self.video_handler);
        let dispatch_tx = self.dispatch_tx.clone();
        let media_slot = Arc::clone(&self.media_pipe);
        let expected_name = media_pipe_name.to_string();
        std::thread::Builder::new()
            .name("exo-media-reader".into())
            .spawn(move || {
                media_read_loop(&handle, &closed, &video_handler, &dispatch_tx);
                let mut guard = media_slot.lock().unwrap_or_else(|e| e.into_inner());
                if guard
                    .as_ref()
                    .is_some_and(|(name, ptr)| name == &expected_name && Arc::ptr_eq(ptr, &handle))
                {
                    *guard = None;
                }
            })?;
        Ok(())
    }

    fn call_raw_json<P: Serialize>(
        &self,
        method: &str,
        params: &P,
        callback: PendingCallback,
    ) -> RequestId {
        let num_id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let id = num_id.to_string();
        let req_id = RequestId(id.clone());
        if self.closed.load(Ordering::Acquire) {
            callback(Err(RpcError::Disconnected));
            return req_id;
        }
        let payload = match self.wire_mode {
            ActiveWireMode::Binary {
                use_schema_ids,
                binary_hot_codecs,
            } => match encode_binary_request(
                num_id,
                method,
                params,
                use_schema_ids,
                binary_hot_codecs,
            ) {
                Ok(bytes) => bytes,
                Err(e) => {
                    callback(Err(RpcError::Json(e)));
                    return req_id;
                }
            },
            ActiveWireMode::Json => match serialize_request_line(&id, method, params) {
                Ok(bytes) => bytes,
                Err(e) => {
                    callback(Err(RpcError::Json(e)));
                    return req_id;
                }
            },
        };
        {
            let mut map = self.pending.lock().unwrap_or_else(|e| e.into_inner());
            map.insert(id.clone(), callback);
        }
        if !self.outbound.push_drop_oldest(payload) {
            let mut map = self.pending.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(cb) = map.remove(&id) {
                cb(Err(RpcError::Disconnected));
            }
        }
        req_id
    }

    pub fn call_raw<P: Serialize>(
        &self,
        method: &str,
        params: &P,
        callback: impl FnOnce(Result<Value, RpcError>) + Send + 'static,
    ) -> RequestId {
        self.call_raw_json(
            method,
            params,
            Box::new(move |res| {
                let typed = res.and_then(decode_wire_to_value);
                callback(typed);
            }),
        )
    }

    pub fn call<P: Serialize, R: DeserializeOwned + Send + 'static>(
        &self,
        method: &str,
        params: &P,
        callback: impl FnOnce(Result<R, RpcError>) + Send + 'static,
    ) -> RequestId {
        self.call_raw_json(
            method,
            params,
            Box::new(move |res| {
                let typed = res.and_then(decode_wire_typed::<R>);
                callback(typed);
            }),
        )
    }

    pub fn cancel(&self, id: &RequestId) -> bool {
        let removed = self
            .pending
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(id.as_str())
            .is_some();
        if removed && !self.closed.load(Ordering::Acquire) {
            let cancel_num = self.next_id.fetch_add(1, Ordering::Relaxed);
            match self.wire_mode {
                ActiveWireMode::Binary {
                    use_schema_ids,
                    binary_hot_codecs,
                } => {
                    if binary_hot_codecs && let Ok(target_num) = id.as_str().parse::<u64>() {
                        let body = wire_codec::encode_cancel_param(target_num);
                        let frame = exo_wire::encode_request_frame(
                            cancel_num,
                            METHOD_RPC_CANCEL,
                            use_schema_ids,
                            exo_wire::PayloadEncoding::BinaryTyped,
                            &body,
                        );
                        let _ = self.outbound.push_drop_oldest(frame);
                    } else if let Ok(json_body) =
                        serde_json::to_vec(&json!({ "requestId": id.as_str() }))
                    {
                        let frame = exo_wire::encode_request_frame(
                            cancel_num,
                            METHOD_RPC_CANCEL,
                            use_schema_ids,
                            exo_wire::PayloadEncoding::JsonUtf8,
                            &json_body,
                        );
                        let _ = self.outbound.push_drop_oldest(frame);
                    }
                }
                ActiveWireMode::Json => {
                    let cancel_id = format!("c{cancel_num}");
                    let envelope = json!({
                        "version": PROTOCOL_VERSION,
                        "id": cancel_id,
                        "method": METHOD_RPC_CANCEL,
                        "params": { "requestId": id.as_str() },
                    });
                    if let Ok(mut payload) = serde_json::to_vec(&envelope) {
                        payload.push(b'\n');
                        let _ = self.outbound.push_drop_oldest(payload);
                    }
                }
            }
        }
        removed
    }

    pub fn call_blocking<P: Serialize, R: DeserializeOwned + Send + 'static>(
        &self,
        method: &str,
        params: &P,
        timeout: Duration,
    ) -> Result<R, RpcError> {
        let (tx, rx) = mpsc::channel();
        let id = self.call(method, params, move |res| {
            let _ = tx.send(res);
        });
        match rx.recv_timeout(timeout) {
            Ok(res) => res,
            Err(_) => {
                self.cancel(&id);
                Err(RpcError::Timeout)
            }
        }
    }

    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::Acquire)
    }

    pub fn close(&self) {
        self.closed.store(true, Ordering::Release);
        self.outbound.close();
        self.dispatch_tx.close();
        self.pipe.cancel();
        if let Some((_, mp)) = self
            .media_pipe
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            mp.cancel();
        }
    }
}

#[cfg(windows)]
impl Drop for RpcClient {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(windows)]
fn media_read_loop(
    pipe: &pipe::PipeHandle,
    closed: &AtomicBool,
    video_handler: &Mutex<Option<VideoFrameHandler>>,
    dispatch_tx: &DispatchQueue,
) {
    let Ok(event) = pipe::EventHandle::new() else {
        return;
    };
    let mut header = [0u8; 8];
    let mut meta_buf = Vec::with_capacity(512);
    while !closed.load(Ordering::Acquire) {
        if pipe
            .read_exact_overlapped(event.raw(), &mut header)
            .is_err()
        {
            break;
        }
        let meta_len = u32::from_le_bytes([header[0], header[1], header[2], header[3]]) as usize;
        let frame_len = u32::from_le_bytes([header[4], header[5], header[6], header[7]]) as usize;
        if meta_len == 0 || meta_len > MAX_MEDIA_META_BYTES || frame_len > MAX_MEDIA_FRAME_BYTES {
            break;
        }
        meta_buf.resize(meta_len, 0);
        if pipe
            .read_exact_overlapped(event.raw(), &mut meta_buf)
            .is_err()
        {
            break;
        }
        let mut frame_buf = vec![0u8; frame_len];
        if frame_len > 0
            && pipe
                .read_exact_overlapped(event.raw(), &mut frame_buf)
                .is_err()
        {
            break;
        }
        if let Some(frame) = decode_binary_media_frame(&meta_buf, frame_buf) {
            let handler = video_handler
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .clone();
            let unrouted = match handler {
                Some(h) => h(frame).err(),
                None => Some(frame),
            };
            if let Some(frame) = unrouted {
                let _ = dispatch_tx.send(DispatchWork::VideoFrame(frame));
            }
        }
        if meta_buf.capacity() > 16 * 1024 {
            meta_buf.clear();
            meta_buf.shrink_to(512);
        }
    }
}

pub fn decode_binary_media_frame(
    meta_bytes: &[u8],
    frame_bytes: Vec<u8>,
) -> Option<DiscordVideoFrame> {
    let meta = serde_json::from_slice::<DiscordVideoFrameMeta>(meta_bytes).ok()?;
    Some(DiscordVideoFrame {
        source: meta.source,
        user_id: meta.user_id,
        timestamp: meta.timestamp,
        keyframe: meta.keyframe,
        rotation: meta.rotation,
        flip: meta.flip,
        codec: meta.codec.unwrap_or_else(|| "h264".into()),
        data: frame_bytes,
    })
}

fn encode_binary_request<P: Serialize>(
    req_id: u64,
    method: &str,
    params: &P,
    use_schema_ids: bool,
    binary_hot_codecs: bool,
) -> Result<Vec<u8>, serde_json::Error> {
    let json_bytes = serde_json::to_vec(params)?;
    if json_bytes == b"{}" || json_bytes == b"null" {
        return Ok(exo_wire::encode_request_frame(
            req_id,
            method,
            use_schema_ids,
            exo_wire::PayloadEncoding::Empty,
            &[],
        ));
    }
    if binary_hot_codecs
        && matches!(method, METHOD_GAME_GET | METHOD_GAME_EXTRAS)
        && let Ok(Value::Object(map)) = serde_json::from_slice::<Value>(&json_bytes)
        && map.len() == 1
        && let Some(Value::String(id)) = map.get("id")
    {
        let body = wire_codec::encode_game_id_param(id);
        return Ok(exo_wire::encode_request_frame(
            req_id,
            method,
            use_schema_ids,
            exo_wire::PayloadEncoding::BinaryTyped,
            &body,
        ));
    }
    Ok(exo_wire::encode_request_frame(
        req_id,
        method,
        use_schema_ids,
        exo_wire::PayloadEncoding::JsonUtf8,
        &json_bytes,
    ))
}

fn decode_wire_typed<R: DeserializeOwned + 'static>(payload: WirePayload) -> Result<R, RpcError> {
    use std::any::{Any, TypeId};
    match payload {
        WirePayload::Json(raw) => serde_json::from_str::<R>(raw.get()).map_err(RpcError::Json),
        WirePayload::Binary { method_id, bytes } => {
            if TypeId::of::<R>() == TypeId::of::<LibraryPayload>()
                && matches!(
                    method_id,
                    exo_wire::METHOD_ID_LIBRARY_GET | exo_wire::METHOD_ID_LIBRARY_REFRESH
                )
            {
                let val = wire_codec::decode_library_payload(&bytes)?;
                return Ok(*Box::<dyn Any>::downcast::<R>(Box::new(val)).unwrap());
            }
            if TypeId::of::<R>() == TypeId::of::<GameGetReply>()
                && method_id == exo_wire::METHOD_ID_GAME_GET
            {
                let val = wire_codec::decode_game_get_reply(&bytes)?;
                return Ok(*Box::<dyn Any>::downcast::<R>(Box::new(val)).unwrap());
            }
            if TypeId::of::<R>() == TypeId::of::<GameExtras>()
                && method_id == exo_wire::METHOD_ID_GAME_EXTRAS
            {
                let val = wire_codec::decode_game_extras(&bytes)?;
                return Ok(*Box::<dyn Any>::downcast::<R>(Box::new(val)).unwrap());
            }
            if TypeId::of::<R>() == TypeId::of::<ShellBootstrap>()
                && method_id == exo_wire::METHOD_ID_SHELL_BOOTSTRAP
            {
                let val = wire_codec::decode_shell_bootstrap(&bytes)?;
                return Ok(*Box::<dyn Any>::downcast::<R>(Box::new(val)).unwrap());
            }
            Err(RpcError::Wire(exo_wire::WireError::InvalidEncoding(
                exo_wire::PayloadEncoding::BinaryTyped as u8,
            )))
        }
    }
}

fn decode_wire_to_value(payload: WirePayload) -> Result<Value, RpcError> {
    match payload {
        WirePayload::Json(raw) => serde_json::from_str::<Value>(raw.get()).map_err(RpcError::Json),
        WirePayload::Binary { .. } => Err(RpcError::Wire(exo_wire::WireError::InvalidEncoding(
            exo_wire::PayloadEncoding::BinaryTyped as u8,
        ))),
    }
}

#[cfg(windows)]
fn read_wire_loop(
    pipe: &pipe::PipeHandle,
    initial_bytes: Vec<u8>,
    pending: &Mutex<HashMap<String, PendingCallback>>,
    dispatch_tx: &DispatchQueue,
) -> String {
    let event = match pipe::EventHandle::new() {
        Ok(e) => e,
        Err(e) => return e.to_string(),
    };
    let mut ring = Vec::with_capacity(64 * 1024);
    ring.extend_from_slice(&initial_bytes);
    let mut ring_pos = 0usize;
    let mut chunk = vec![0u8; 64 * 1024];

    loop {
        while ring.len() - ring_pos < exo_wire::HEADER_LEN {
            if ring_pos > 0 {
                ring.drain(..ring_pos);
                ring_pos = 0;
            }
            let n = match pipe.read_overlapped(event.raw(), &mut chunk) {
                Ok(0) => return "eof".into(),
                Ok(n) => n,
                Err(e) => return e.to_string(),
            };
            ring.extend_from_slice(&chunk[..n]);
        }

        let hdr_bytes: [u8; exo_wire::HEADER_LEN] = ring[ring_pos..ring_pos + exo_wire::HEADER_LEN]
            .try_into()
            .unwrap();
        let hdr = match exo_wire::FrameHeader::decode(&hdr_bytes) {
            Ok(h) => h,
            Err(e) => return format!("wire header error: {e}"),
        };
        let payload_len = hdr.payload_len as usize;
        if payload_len > MAX_INBOUND_BYTES {
            return "inbound wire frame exceeded limit".into();
        }
        let frame_total = exo_wire::HEADER_LEN + payload_len;
        while ring.len() - ring_pos < frame_total {
            if ring_pos > 0 {
                ring.drain(..ring_pos);
                ring_pos = 0;
            }
            let needed = frame_total - ring.len();
            let old_len = ring.len();
            let read_len = needed.max(chunk.len()).min(256 * 1024);
            if read_len > chunk.len() {
                chunk.resize(read_len, 0);
            }
            let n = match pipe.read_overlapped(event.raw(), &mut chunk[..read_len]) {
                Ok(0) => return "eof".into(),
                Ok(n) => n,
                Err(e) => return e.to_string(),
            };
            ring.extend_from_slice(&chunk[..n]);
            if ring.len() == old_len {
                return "eof".into();
            }
        }

        let payload = &ring[ring_pos + exo_wire::HEADER_LEN..ring_pos + frame_total];
        dispatch_wire_frame(hdr, payload, pending, dispatch_tx);
        ring_pos += frame_total;
        if ring_pos == ring.len() {
            ring.clear();
            ring_pos = 0;
            if ring.capacity() > 512 * 1024 {
                ring.shrink_to(64 * 1024);
            }
        }
    }
}

fn dispatch_wire_frame(
    hdr: exo_wire::FrameHeader,
    payload: &[u8],
    pending: &Mutex<HashMap<String, PendingCallback>>,
    dispatch_tx: &DispatchQueue,
) {
    match hdr.kind {
        exo_wire::FrameKind::ResponseOk => {
            let id = hdr.req_id.to_string();
            let cb = pending
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .remove(&id);
            let Some(cb) = cb else {
                return;
            };
            let res = if payload.is_empty() {
                Ok(WirePayload::Json(null_raw()))
            } else {
                match exo_wire::PayloadEncoding::from_u8(payload[0]) {
                    Ok(exo_wire::PayloadEncoding::Empty) => Ok(WirePayload::Json(null_raw())),
                    Ok(exo_wire::PayloadEncoding::JsonUtf8) => {
                        match std::str::from_utf8(&payload[1..]) {
                            Ok(s) => RawValue::from_string(s.to_owned())
                                .map(WirePayload::Json)
                                .map_err(RpcError::Json),
                            Err(_) => Err(RpcError::Wire(exo_wire::WireError::InvalidUtf8)),
                        }
                    }
                    Ok(exo_wire::PayloadEncoding::BinaryTyped) => Ok(WirePayload::Binary {
                        method_id: hdr.method_id,
                        bytes: payload[1..].to_vec(),
                    }),
                    Err(e) => Err(RpcError::Wire(e)),
                }
            };
            let _ = dispatch_tx.send(DispatchWork::Response(cb, res));
        }
        exo_wire::FrameKind::ResponseErr => {
            let id = hdr.req_id.to_string();
            let cb = pending
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .remove(&id);
            if let Some(cb) = cb {
                let err = std::str::from_utf8(payload)
                    .unwrap_or("Host request failed")
                    .to_owned();
                let _ = dispatch_tx.send(DispatchWork::Response(cb, Err(RpcError::Host(err))));
            }
        }
        exo_wire::FrameKind::Event => {
            let mut r = exo_wire::WireReader::new(payload);
            let (event_id, event_name) = if hdr.method_id == exo_wire::EVENT_ID_UNKNOWN {
                let Ok(name) = r.get_short_str() else {
                    return;
                };
                (exo_wire::event_id(name), name)
            } else {
                let Some(name) = exo_wire::event_name(hdr.method_id) else {
                    return;
                };
                (hdr.method_id, name)
            };
            let Ok(enc_byte) = r.get_u8() else {
                return;
            };
            let Ok(enc) = exo_wire::PayloadEncoding::from_u8(enc_byte) else {
                return;
            };
            let body = r.remaining_slice();
            match enc {
                exo_wire::PayloadEncoding::BinaryTyped => {
                    let ev = match event_id {
                        exo_wire::EVENT_ID_AUTOPILOT_LIVE => {
                            wire_codec::decode_autopilot_live(body)
                                .ok()
                                .map(HostEvent::AutopilotLive)
                        }
                        exo_wire::EVENT_ID_INSTALL_PROGRESS => {
                            wire_codec::decode_install_progress_frame(body)
                                .ok()
                                .map(HostEvent::InstallProgress)
                        }
                        exo_wire::EVENT_ID_LIBRARY_UPDATED => {
                            wire_codec::decode_library_payload(body)
                                .ok()
                                .map(HostEvent::LibraryUpdated)
                        }
                        _ => None,
                    };
                    if let Some(ev) = ev {
                        let _ = dispatch_tx.send(DispatchWork::Event(Box::new(ev)));
                    }
                }
                exo_wire::PayloadEncoding::Empty => match parse_event(event_name, "null", None) {
                    Ok(ev) => {
                        let _ = dispatch_tx.send(DispatchWork::Event(Box::new(ev)));
                    }
                    Err(frame) => {
                        let _ = dispatch_tx.send(DispatchWork::VideoFrame(frame));
                    }
                },
                exo_wire::PayloadEncoding::JsonUtf8 => {
                    let Ok(data_raw) = std::str::from_utf8(body) else {
                        return;
                    };
                    match parse_event(event_name, data_raw, None) {
                        Ok(ev) => {
                            let _ = dispatch_tx.send(DispatchWork::Event(Box::new(ev)));
                        }
                        Err(frame) => {
                            let _ = dispatch_tx.send(DispatchWork::VideoFrame(frame));
                        }
                    }
                }
            }
        }
        exo_wire::FrameKind::BinaryMedia => {
            let mut r = exo_wire::WireReader::new(payload);
            let Ok(meta) = r.get_bytes() else {
                return;
            };
            let frame_bytes = r.remaining_slice().to_vec();
            if let Some(frame) = decode_binary_media_frame(meta, frame_bytes) {
                let _ = dispatch_tx.send(DispatchWork::VideoFrame(frame));
            }
        }
        exo_wire::FrameKind::Request
        | exo_wire::FrameKind::Hello
        | exo_wire::FrameKind::HelloAck => {}
    }
}

#[cfg(windows)]
fn read_loop(
    pipe: &pipe::PipeHandle,
    initial_bytes: Vec<u8>,
    pending: &Mutex<HashMap<String, PendingCallback>>,
    dispatch_tx: &DispatchQueue,
) -> String {
    let event = match pipe::EventHandle::new() {
        Ok(e) => e,
        Err(e) => return e.to_string(),
    };
    let mut buf = vec![0u8; 32 * 1024];
    let mut line = Vec::with_capacity(4096);
    if !initial_bytes.is_empty() {
        let mut start = 0;
        for i in 0..initial_bytes.len() {
            if initial_bytes[i] != b'\n' {
                continue;
            }
            line.extend_from_slice(&initial_bytes[start..i]);
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            if !line.is_empty() {
                dispatch_line_to(&line, pending, dispatch_tx);
            }
            line.clear();
            start = i + 1;
        }
        if start < initial_bytes.len() {
            line.extend_from_slice(&initial_bytes[start..]);
        }
    }

    loop {
        let n = match pipe.read_overlapped(event.raw(), &mut buf) {
            Ok(0) => return "eof".into(),
            Ok(n) => n,
            Err(e) => return e.to_string(),
        };
        let mut start = 0;
        for i in 0..n {
            if buf[i] != b'\n' {
                continue;
            }
            if line.len() + (i - start) > MAX_INBOUND_BYTES {
                return "inbound message exceeded limit".into();
            }
            line.extend_from_slice(&buf[start..i]);
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            if !line.is_empty() {
                dispatch_line_to(&line, pending, dispatch_tx);
            }
            line.clear();
            if line.capacity() > 64 * 1024 {
                line.shrink_to(4096);
            }
            start = i + 1;
        }
        if start < n {
            if line.len() + (n - start) > MAX_INBOUND_BYTES {
                return "inbound message exceeded limit".into();
            }
            line.extend_from_slice(&buf[start..n]);
        }
    }
}

fn raw_fallback(name: &str, data_raw: &str) -> HostEvent {
    HostEvent::Raw {
        event: name.to_string(),
        data: serde_json::from_str::<Value>(data_raw).unwrap_or(Value::Null),
    }
}

fn parse_event(
    name: &str,
    data_raw: &str,
    meta_raw: Option<&str>,
) -> Result<HostEvent, DiscordVideoFrame> {
    match name {
        "library.updated" => Ok(serde_json::from_str::<LibraryPayload>(data_raw)
            .map(HostEvent::LibraryUpdated)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "install.progress" => Ok(serde_json::from_str::<InstallProgress>(data_raw)
            .map(HostEvent::InstallProgress)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "install.queue" => Ok(serde_json::from_str::<InstallQueueEvent>(data_raw)
            .map(HostEvent::InstallQueue)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "game.runState" => Ok(serde_json::from_str::<GameRunStateEvent>(data_raw)
            .map(HostEvent::GameRunState)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "launch.status" => Ok(serde_json::from_str::<LaunchStatusEvent>(data_raw)
            .map(HostEvent::LaunchStatus)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "app.updateProgress" => Ok(serde_json::from_str::<UpdateProgress>(data_raw)
            .map(HostEvent::UpdateProgress)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "app.updateState" => Ok(serde_json::from_str::<AppUpdateStateEvent>(data_raw)
            .map(HostEvent::AppUpdateState)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "exoAccount.changed" => Ok(HostEvent::ExoAccountChanged),
        "autopilot.report" => Ok(serde_json::from_str::<AutopilotReportEvent>(data_raw)
            .map(HostEvent::AutopilotReport)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "autopilot.live" => {
            if data_raw == "null" {
                Ok(HostEvent::AutopilotLive(None))
            } else {
                Ok(serde_json::from_str::<AutopilotLive>(data_raw)
                    .map(|live| HostEvent::AutopilotLive(Some(live)))
                    .unwrap_or_else(|_| raw_fallback(name, data_raw)))
            }
        }
        "shaders.changed" => Ok(HostEvent::ShadersChanged),
        "shaders.event" => Ok(serde_json::from_str::<ShadersEventPayload>(data_raw)
            .map(HostEvent::ShadersEvent)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "accounts.progress" => Ok(serde_json::from_str::<AccountsProgressEvent>(data_raw)
            .map(HostEvent::AccountsProgress)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "backup.stopped" => Ok(serde_json::from_str::<BackupStoppedEvent>(data_raw)
            .map(HostEvent::BackupStopped)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "components.failed" => Ok(serde_json::from_str::<ComponentsFailedEvent>(data_raw)
            .map(HostEvent::ComponentsFailed)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "gifts.changed" => Ok(serde_json::from_str::<StoreGiftsReply>(data_raw)
            .map(HostEvent::GiftsChanged)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "store.changed" => Ok(serde_json::from_str::<StoreChangedEvent>(data_raw)
            .map(HostEvent::StoreChanged)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "game.achievements" => Ok(serde_json::from_str::<GameAchievementsEvent>(data_raw)
            .map(HostEvent::GameAchievements)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "game.optimizedAgain" => Ok(serde_json::from_str::<GameOptimizeEvent>(data_raw)
            .map(HostEvent::GameOptimizedAgain)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "game.optimizeHeld" => Ok(serde_json::from_str::<GameOptimizeEvent>(data_raw)
            .map(HostEvent::GameOptimizeHeld)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "compress.progress" => Ok(serde_json::from_str::<CompressProgressEvent>(data_raw)
            .map(HostEvent::CompressProgress)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "store.sale" => Ok(serde_json::from_str::<StoreSaleEvent>(data_raw)
            .map(HostEvent::StoreSale)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "music.playback" => {
            fn present_option<'de, D, T>(d: D) -> Result<Option<Option<T>>, D::Error>
            where
                D: serde::Deserializer<'de>,
                T: Deserialize<'de>,
            {
                Option::<T>::deserialize(d).map(Some)
            }
            #[derive(Deserialize)]
            struct PlaybackWrap {
                #[serde(default, deserialize_with = "present_option")]
                playback: Option<Option<MusicPlayback>>,
            }
            let pb = if data_raw == "null" {
                None
            } else if let Ok(PlaybackWrap {
                playback: Some(inner),
            }) = serde_json::from_str::<PlaybackWrap>(data_raw)
            {
                inner
            } else {
                serde_json::from_str::<MusicPlayback>(data_raw).ok()
            };
            Ok(HostEvent::MusicPlayback(pb))
        }
        "music.volume" => Ok(serde_json::from_str::<MusicVolumeEvent>(data_raw)
            .map(HostEvent::MusicVolume)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "music.status" => Ok(serde_json::from_str::<MusicStatusEvent>(data_raw)
            .map(HostEvent::MusicStatus)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "capture.state" => Ok(serde_json::from_str::<CaptureLiveState>(data_raw)
            .map(HostEvent::CaptureState)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "capture.saved" => Ok(serde_json::from_str::<CaptureSavedEvent>(data_raw)
            .map(|ev| HostEvent::CaptureSaved(Box::new(ev)))
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "capture.reel" => Ok(serde_json::from_str::<CaptureReelEvent>(data_raw)
            .map(|ev| HostEvent::CaptureReel(Box::new(ev)))
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "capture.shrunk" => Ok(serde_json::from_str::<CaptureShrunkEvent>(data_raw)
            .map(|ev| HostEvent::CaptureShrunk(Box::new(ev)))
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "notes.open" => Ok(serde_json::from_str::<NotesOpenEvent>(data_raw)
            .map(HostEvent::NotesOpen)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "controller.state" => Ok(serde_json::from_str::<ControllerStateEvent>(data_raw)
            .map(HostEvent::ControllerState)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "hid.changed" => Ok(HostEvent::HidChanged),
        "hid.input" => Ok(serde_json::from_str::<HidInputEvent>(data_raw)
            .map(HostEvent::HidInput)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.state" => Ok(serde_json::from_str::<DiscordStateEvent>(data_raw)
            .map(HostEvent::DiscordState)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.signin" => Ok(serde_json::from_str::<DiscordSignInStep>(data_raw)
            .map(HostEvent::DiscordSignIn)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.changed" => Ok(serde_json::from_str::<DiscordChangedEvent>(data_raw)
            .map(HostEvent::DiscordChanged)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.members" => {
            #[derive(Deserialize)]
            struct MembersWire<'a> {
                #[serde(rename = "guildId", borrow)]
                guild_id: Cow<'a, str>,
            }
            Ok(serde_json::from_str::<MembersWire<'_>>(data_raw)
                .map(|m| HostEvent::DiscordMembers {
                    guild_id: m.guild_id.into_owned(),
                })
                .unwrap_or_else(|_| raw_fallback(name, data_raw)))
        }
        "discord.presence" => Ok(serde_json::from_str::<DiscordPresenceEvent>(data_raw)
            .map(HostEvent::DiscordPresence)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.unread" => Ok(serde_json::from_str::<DiscordUnreadEvent>(data_raw)
            .map(HostEvent::DiscordUnread)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.voice" => Ok(serde_json::from_str::<DiscordVoice>(data_raw)
            .map(HostEvent::DiscordVoice)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.speaking" => {
            #[derive(Deserialize)]
            struct SpeakingWire<'a> {
                #[serde(rename = "userId", borrow)]
                user_id: Cow<'a, str>,
                #[serde(default)]
                speaking: bool,
            }
            Ok(serde_json::from_str::<SpeakingWire<'_>>(data_raw)
                .map(|s| HostEvent::DiscordSpeaking {
                    user_id: s.user_id.into_owned(),
                    speaking: s.speaking,
                })
                .unwrap_or_else(|_| raw_fallback(name, data_raw)))
        }
        "discord.message" => Ok(serde_json::from_str::<DiscordMessageEvent>(data_raw)
            .map(|ev| HostEvent::DiscordMessage(Box::new(ev)))
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.reaction" => Ok(serde_json::from_str::<DiscordReactionEvent>(data_raw)
            .map(HostEvent::DiscordReaction)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.typing" => Ok(serde_json::from_str::<DiscordTypingEvent>(data_raw)
            .map(HostEvent::DiscordTyping)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.pollVote" => Ok(serde_json::from_str::<DiscordPollVoteEvent>(data_raw)
            .map(HostEvent::DiscordPollVote)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.interaction" => Ok(serde_json::from_str::<DiscordInteractionEvent>(data_raw)
            .map(|ev| HostEvent::DiscordInteraction(Box::new(ev)))
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.colors" => {
            #[derive(Deserialize)]
            struct ColorsWire<'a> {
                #[serde(rename = "guildId", borrow)]
                guild_id: Cow<'a, str>,
            }
            Ok(serde_json::from_str::<ColorsWire<'_>>(data_raw)
                .map(|c| HostEvent::DiscordColors {
                    guild_id: c.guild_id.into_owned(),
                })
                .unwrap_or_else(|_| raw_fallback(name, data_raw)))
        }
        "discord.open" => Ok(serde_json::from_str::<DiscordOpenEvent>(data_raw)
            .map(HostEvent::DiscordOpen)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.dmLine" => Ok(serde_json::from_str::<DiscordDmLineEvent>(data_raw)
            .map(HostEvent::DiscordDmLine)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.notify" => Ok(serde_json::from_str::<DiscordNotifyEvent>(data_raw)
            .map(|ev| HostEvent::DiscordNotify(Box::new(ev)))
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.notifyClear" => Ok(serde_json::from_str::<DiscordNotifyClearEvent>(data_raw)
            .map(HostEvent::DiscordNotifyClear)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "friends.badge" => Ok(serde_json::from_str::<FriendsBadgeReply>(data_raw)
            .map(|b| HostEvent::FriendsBadge { count: b.count })
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.video" => Ok(serde_json::from_str::<DiscordVideoEvent>(data_raw)
            .map(HostEvent::DiscordVideo)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.golive" => Ok(serde_json::from_str::<DiscordGoLiveStateEvent>(data_raw)
            .map(HostEvent::DiscordGoLive)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.golive.stats" => Ok(serde_json::from_str::<DiscordGoLiveStatsEvent>(data_raw)
            .map(HostEvent::DiscordGoLiveStats)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.golive.viewers" => {
            #[derive(Deserialize)]
            struct ViewersWire {
                #[serde(default)]
                ids: Vec<String>,
            }
            let ids = serde_json::from_str::<ViewersWire>(data_raw)
                .map(|v| v.ids)
                .unwrap_or_default();
            Ok(HostEvent::DiscordGoLiveViewers(ids))
        }
        "discord.call" => Ok(serde_json::from_str::<DiscordCallEvent>(data_raw)
            .map(HostEvent::DiscordCall)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.callSong" => Ok(serde_json::from_str::<DiscordCallSong>(data_raw)
            .map(HostEvent::DiscordCallSong)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.camera" => {
            #[derive(Deserialize)]
            struct CameraWire<'a> {
                #[serde(borrow)]
                state: Cow<'a, str>,
            }
            Ok(serde_json::from_str::<CameraWire<'_>>(data_raw)
                .map(|c| HostEvent::DiscordCamera {
                    state: c.state.into_owned(),
                })
                .unwrap_or_else(|_| raw_fallback(name, data_raw)))
        }
        "discord.keybind" => {
            #[derive(Deserialize)]
            struct KeybindWire<'a> {
                #[serde(borrow)]
                action: Cow<'a, str>,
            }
            Ok(serde_json::from_str::<KeybindWire<'_>>(data_raw)
                .map(|k| HostEvent::DiscordKeybind {
                    action: k.action.into_owned(),
                })
                .unwrap_or_else(|_| raw_fallback(name, data_raw)))
        }
        "discord.micLevel" => Ok(serde_json::from_str::<DiscordMicLevel>(data_raw)
            .map(HostEvent::DiscordMicLevel)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.tts" => Ok(serde_json::from_str::<DiscordTtsEvent>(data_raw)
            .map(HostEvent::DiscordTts)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.userSettings" => Ok(serde_json::from_str::<Value>(data_raw)
            .map(HostEvent::DiscordUserSettings)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "discord.video.keyframe" => {
            #[derive(Deserialize)]
            struct KeyframeWire<'a> {
                #[serde(borrow)]
                source: Cow<'a, str>,
            }
            Ok(serde_json::from_str::<KeyframeWire<'_>>(data_raw)
                .map(|k| HostEvent::DiscordVideoKeyframe {
                    source: k.source.into_owned(),
                })
                .unwrap_or_else(|_| raw_fallback(name, data_raw)))
        }
        "binaryFrame" => {
            let meta_str = meta_raw.unwrap_or("null");
            let b64 = serde_json::from_str::<Cow<'_, str>>(data_raw).ok();
            if let (Ok(meta), Some(b64_str)) =
                (serde_json::from_str::<DiscordVideoFrameMeta>(meta_str), b64)
                && let Some(bytes) = base64::decode(&b64_str)
            {
                Err(DiscordVideoFrame {
                    source: meta.source,
                    user_id: meta.user_id,
                    timestamp: meta.timestamp,
                    keyframe: meta.keyframe,
                    rotation: meta.rotation,
                    flip: meta.flip,
                    codec: meta.codec.unwrap_or_else(|| "h264".into()),
                    data: bytes,
                })
            } else {
                Ok(raw_fallback(name, data_raw))
            }
        }
        "devices.changed" => Ok(HostEvent::DevicesChanged),
        "apps.changed" => Ok(HostEvent::AppsChanged),
        "gpu.driver" => Ok(serde_json::from_str::<DriverProgress>(data_raw)
            .map(HostEvent::GpuDriver)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "gpu.driverInstall" => Ok(serde_json::from_str::<GpuDriverInstallEvent>(data_raw)
            .map(HostEvent::GpuDriverInstall)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "gpu.tuningChanged" => Ok(serde_json::from_str::<GpuTuningChangedEvent>(data_raw)
            .map(HostEvent::GpuTuningChanged)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "gpu.tuningTestProgress" => Ok(serde_json::from_str::<GpuStabilityProgress>(data_raw)
            .map(|p| HostEvent::GpuTuningTestProgress(Box::new(p)))
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "hdr.calibrationProgress" => Ok(serde_json::from_str::<HdrCalibrationProgress>(data_raw)
            .map(HostEvent::HdrCalibrationProgress)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "hdr.warning" => Ok(serde_json::from_str::<HdrWarningEvent>(data_raw)
            .map(HostEvent::HdrWarning)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "drivers.step" => Ok(serde_json::from_str::<DriverStep>(data_raw)
            .map(HostEvent::DriversStep)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "bios.progress" => {
            #[derive(Deserialize)]
            struct BiosProgressWire {
                #[serde(default)]
                share: f64,
            }
            Ok(serde_json::from_str::<BiosProgressWire>(data_raw)
                .map(|b| HostEvent::BiosProgress { share: b.share })
                .unwrap_or_else(|_| raw_fallback(name, data_raw)))
        }
        "runtimes.progress" => Ok(serde_json::from_str::<RuntimesProgress>(data_raw)
            .map(HostEvent::RuntimesProgress)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "runtimes.done" => Ok(serde_json::from_str::<RuntimesInstallReply>(data_raw)
            .map(HostEvent::RuntimesDone)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "shaders.rebuild" => Ok(serde_json::from_str::<ShaderRebuild>(data_raw)
            .map(HostEvent::ShadersRebuild)
            .unwrap_or_else(|_| raw_fallback(name, data_raw))),
        "shell.window" => Ok(raw_fallback(name, data_raw)),
        "app.open" => Ok(raw_fallback(name, data_raw)),
        "anticheat.missing" => Ok(raw_fallback(name, data_raw)),
        "stores.outage" => Ok(raw_fallback(name, data_raw)),
        "app.notify" => Ok(raw_fallback(name, data_raw)),
        "app.notifyClear" => Ok(raw_fallback(name, data_raw)),
        "keys.found" => Ok(raw_fallback(name, data_raw)),
        "versions.new" => Ok(raw_fallback(name, data_raw)),
        _ => Ok(raw_fallback(name, data_raw)),
    }
}

fn dispatch_line_to(
    raw: &[u8],
    pending: &Mutex<HashMap<String, PendingCallback>>,
    dispatch_tx: &DispatchQueue,
) {
    let Ok(env) = serde_json::from_slice::<WireEnvelope<'_>>(raw) else {
        return;
    };
    if let Some(id) = env.id.as_deref() {
        let cb = pending.lock().unwrap_or_else(|e| e.into_inner()).remove(id);
        if let Some(cb) = cb {
            let ok = env.ok.unwrap_or(false);
            let res = if ok {
                Ok(WirePayload::Json(
                    env.result.map(RawValue::to_owned).unwrap_or_else(null_raw),
                ))
            } else {
                let err = env
                    .error
                    .as_deref()
                    .unwrap_or("Host request failed")
                    .to_string();
                Err(RpcError::Host(err))
            };
            let _ = dispatch_tx.send(DispatchWork::Response(cb, res));
        }
        return;
    }
    if let Some(name) = env.event.as_deref() {
        let data_raw = env.data.map(RawValue::get).unwrap_or("null");
        let meta_raw = env.meta.map(RawValue::get);
        match parse_event(name, data_raw, meta_raw) {
            Ok(ev) => {
                let _ = dispatch_tx.send(DispatchWork::Event(Box::new(ev)));
            }
            Err(frame) => {
                let _ = dispatch_tx.send(DispatchWork::VideoFrame(frame));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const CONTRACT_JSON: &str = include_str!("../../../../ui/contracts/host-v1.json");

    #[test]
    fn host_contract_contains_every_rust_rpc_method() {
        let contract = load_host_contract(CONTRACT_JSON).expect("valid host-v1.json");
        assert_eq!(contract.version, PROTOCOL_VERSION);
        for method in USED_METHODS {
            assert!(
                contract.methods.contains_key(*method),
                "Rust calls undeclared host method: {method}"
            );
        }
    }

    #[test]
    fn art_base_rejects_traversal_and_unsafe_leaves() {
        let base = ArtBase {
            covers: "https://covers.exo-launcher.local/tok/".into(),
            covers_dir: Some(std::env::temp_dir().to_string_lossy().into_owned()),
            captures_dir: Some(std::env::temp_dir().to_string_lossy().into_owned()),
            ..Default::default()
        };
        assert!(
            base.resolve_local_cover("https://covers.exo-launcher.local/tok/..%2Fsecret.png")
                .is_none()
        );
        assert!(
            base.resolve_local_cover("https://evil.example/tok/a.png")
                .is_none()
        );
        assert!(
            base.resolve_capture_file("https://captures.exo-launcher.local/..%2F..%2Fwin.ini")
                .is_none()
        );
    }

    #[test]
    fn decodes_binary_video_frame_from_named_pipe_envelope() {
        let payload = br#"{"event":"binaryFrame","meta":{"source":"stream","userId":"200002","timestamp":1234.5,"keyframe":true,"codec":"h265"},"data":"AAABZ2Q="}"#;
        let pending = Mutex::new(HashMap::new());
        let queue = DispatchQueue::new(EVENT_QUEUE_CAP, VIDEO_FRAME_QUEUE_CAP);
        dispatch_line_to(payload, &pending, &queue);
        match queue.pop_blocking().expect("dispatched video frame") {
            DispatchWork::VideoFrame(f) => {
                assert_eq!(f.source, "stream");
                assert_eq!(f.user_id, "200002");
                assert_eq!(f.codec, "h265");
                assert!(f.keyframe);
                assert_eq!(f.data, vec![0x00, 0x00, 0x01, 0x67, 0x64]);
            }
            _ => panic!("expected VideoFrame dispatch work"),
        }
    }

    #[test]
    fn decodes_binary_media_pipe_frame_directly() {
        let meta = br#"{"source":"stream","userId":"77","timestamp":99.25,"keyframe":true,"rotation":90,"flip":true,"codec":"h264"}"#;
        let raw = vec![0x00, 0x00, 0x00, 0x01, 0x67];
        let frame = decode_binary_media_frame(meta, raw.clone()).expect("valid media frame");
        assert_eq!(frame.source, "stream");
        assert_eq!(frame.user_id, "77");
        assert!(frame.keyframe);
        assert_eq!(frame.rotation, 90);
        assert!(frame.flip);
        assert_eq!(frame.codec, "h264");
        assert_eq!(frame.data, raw);
    }

    #[test]
    fn parses_music_playback_flat_wrapped_and_null() {
        let flat = br#"{"event":"music.playback","data":{"status":"playing","title":"Track A","artist":"Artist A","provider":"spotify","source":"music"}}"#;
        let wrapped = br#"{"event":"music.playback","data":{"playback":{"status":"paused","title":"Track B"}}}"#;
        let wrapped_null = br#"{"event":"music.playback","data":{"playback":null}}"#;
        let bare_null = br#"{"event":"music.playback","data":null}"#;
        let pending = Mutex::new(HashMap::new());
        let queue = DispatchQueue::new(EVENT_QUEUE_CAP, VIDEO_FRAME_QUEUE_CAP);

        dispatch_line_to(flat, &pending, &queue);
        match queue.pop_blocking().unwrap() {
            DispatchWork::Event(ev) => match *ev {
                HostEvent::MusicPlayback(Some(pb)) => {
                    assert_eq!(pb.status, "playing");
                    assert_eq!(pb.title, "Track A");
                    assert_eq!(pb.artist, "Artist A");
                }
                _ => panic!("expected flat MusicPlayback(Some)"),
            },
            _ => panic!("expected flat MusicPlayback(Some)"),
        }

        dispatch_line_to(wrapped, &pending, &queue);
        match queue.pop_blocking().unwrap() {
            DispatchWork::Event(ev) => match *ev {
                HostEvent::MusicPlayback(Some(pb)) => {
                    assert_eq!(pb.status, "paused");
                    assert_eq!(pb.title, "Track B");
                }
                _ => panic!("expected wrapped MusicPlayback(Some)"),
            },
            _ => panic!("expected wrapped MusicPlayback(Some)"),
        }

        dispatch_line_to(wrapped_null, &pending, &queue);
        match queue.pop_blocking().unwrap() {
            DispatchWork::Event(ev) if matches!(*ev, HostEvent::MusicPlayback(None)) => {}
            _ => panic!("expected wrapped MusicPlayback(None)"),
        }

        dispatch_line_to(bare_null, &pending, &queue);
        match queue.pop_blocking().unwrap() {
            DispatchWork::Event(ev) if matches!(*ev, HostEvent::MusicPlayback(None)) => {}
            _ => panic!("expected bare null MusicPlayback(None)"),
        }
    }

    #[test]
    fn parses_all_newly_wired_host_events() {
        type EventCase = (&'static [u8], fn(HostEvent) -> bool);
        let cases: &[EventCase] = &[
            (
                br#"{"event":"music.volume","data":{"level":0.42,"source":"app"}}"#,
                |ev| matches!(ev, HostEvent::MusicVolume(v) if (v.level - 0.42).abs() < 1e-6 && v.source == "app"),
            ),
            (
                br#"{"event":"capture.state","data":{"present":true,"buffering":true,"recording":false}}"#,
                |ev| matches!(ev, HostEvent::CaptureState(s) if s.present && s.buffering && !s.recording),
            ),
            (
                br#"{"event":"capture.saved","data":{"item":{"id":"c1","name":"Clip 1","kind":"clip"},"edit":true}}"#,
                |ev| matches!(ev, HostEvent::CaptureSaved(s) if s.item.id == "c1" && s.edit),
            ),
            (
                br#"{"event":"capture.reel","data":{"state":"working","percent":55.0,"wait":false}}"#,
                |ev| matches!(ev, HostEvent::CaptureReel(r) if r.state == "working" && (r.percent - 55.0).abs() < 1e-6),
            ),
            (
                br#"{"event":"capture.shrunk","data":{"id":"c1","before":50000000,"after":12000000,"item":{"id":"c1","kind":"video","size":12000000}}}"#,
                |ev| matches!(ev, HostEvent::CaptureShrunk(s) if s.id == "c1" && s.before == 50_000_000 && s.after == 12_000_000 && s.item.is_some()),
            ),
            (
                br#"{"event":"notes.open","data":{"id":"note-1","message":null}}"#,
                |ev| matches!(ev, HostEvent::NotesOpen(n) if n.id.as_deref() == Some("note-1")),
            ),
            (
                br#"{"event":"controller.state","data":{"id":"pad-1","lx":128,"ly":127,"rx":200,"ry":50,"l2":10,"r2":255,"profile":2,"stopL":1,"stopR":2,"battery":80,"charging":true,"extras":64}}"#,
                |ev| matches!(ev, HostEvent::ControllerState(c) if c.id == "pad-1" && c.lx == 128 && c.r2 == 255 && c.profile == Some(2) && c.extras == 64 && c.charging),
            ),
            (
                br#"{"event":"hid.changed","data":{}}"#,
                |ev| matches!(ev, HostEvent::HidChanged),
            ),
            (
                br#"{"event":"hid.input","data":{"device":"dev1","reportId":5,"data":"AQID"}}"#,
                |ev| matches!(ev, HostEvent::HidInput(h) if h.device == "dev1" && h.report_id == 5 && h.data == "AQID"),
            ),
            (
                br#"{"event":"install.queue","data":{"queuedGameIds":["steam:10"],"pausedDownloads":[],"held":null}}"#,
                |ev| matches!(ev, HostEvent::InstallQueue(q) if q.queued_game_ids == vec!["steam:10"]),
            ),
            (
                br#"{"event":"game.runState","data":{"id":"steam:10","isRunning":true,"canStop":true}}"#,
                |ev| matches!(ev, HostEvent::GameRunState(r) if r.id == "steam:10" && r.is_running && r.can_stop),
            ),
            (
                br#"{"event":"launch.status","data":{"gameId":"steam:10","ok":true,"phase":"running","processId":4242}}"#,
                |ev| matches!(ev, HostEvent::LaunchStatus(l) if l.game_id == "steam:10" && l.phase == "running" && l.process_id == Some(4242)),
            ),
            (
                br#"{"event":"app.updateState","data":{"state":"downloading"}}"#,
                |ev| matches!(ev, HostEvent::AppUpdateState(u) if u.state == "downloading"),
            ),
            (
                br#"{"event":"exoAccount.changed","data":{}}"#,
                |ev| matches!(ev, HostEvent::ExoAccountChanged),
            ),
            (
                br#"{"event":"autopilot.report","data":{"game":"steam:10","title":"Halo"}}"#,
                |ev| matches!(ev, HostEvent::AutopilotReport(r) if r.game == "steam:10" && r.title == "Halo"),
            ),
            (
                br#"{"event":"autopilot.live","data":null}"#,
                |ev| matches!(ev, HostEvent::AutopilotLive(None)),
            ),
            (
                br#"{"event":"shaders.changed","data":{}}"#,
                |ev| matches!(ev, HostEvent::ShadersChanged),
            ),
            (
                br#"{"event":"store.changed","data":{"what":"wishlist"}}"#,
                |ev| matches!(ev, HostEvent::StoreChanged(s) if s.what == "wishlist"),
            ),
            (
                br#"{"event":"game.achievements","data":{"id":"steam:10","unlocked":12,"total":40}}"#,
                |ev| matches!(ev, HostEvent::GameAchievements(a) if a.id == "steam:10" && a.unlocked == 12 && a.total == 40),
            ),
            (
                br#"{"event":"game.optimizedAgain","data":{"title":"Cyberpunk 2077"}}"#,
                |ev| matches!(ev, HostEvent::GameOptimizedAgain(o) if o.title == "Cyberpunk 2077"),
            ),
            (
                br#"{"event":"game.optimizeHeld","data":{"title":"Cyberpunk 2077"}}"#,
                |ev| matches!(ev, HostEvent::GameOptimizeHeld(o) if o.title == "Cyberpunk 2077"),
            ),
            (
                br#"{"event":"compress.progress","data":{"id":"steam:10","compress":{"state":"compressing","percent":0.42,"saved":1024,"sampled":true,"changed":0}}}"#,
                |ev| matches!(ev, HostEvent::CompressProgress(c) if c.id == "steam:10" && c.compress.state == "compressing" && c.compress.saved == 1024),
            ),
            (
                br#"{"event":"store.sale","data":{"appId":"1091500","name":"Cyberpunk 2077","final":2999,"discount":50,"currency":"USD","price":"$29.99"}}"#,
                |ev| matches!(ev, HostEvent::StoreSale(s) if s.app_id == "1091500" && s.discount == 50),
            ),
        ];
        let pending = Mutex::new(HashMap::new());
        let queue = DispatchQueue::new(EVENT_QUEUE_CAP, VIDEO_FRAME_QUEUE_CAP);
        for (idx, (payload, check)) in cases.iter().enumerate() {
            dispatch_line_to(payload, &pending, &queue);
            let work = queue.pop_blocking().expect("event dispatched");
            match work {
                DispatchWork::Event(ev) => assert!(check(*ev), "case {idx} failed"),
                _ => panic!("case {idx} was not an Event"),
            }
        }
    }

    #[test]
    fn raw_value_response_dispatch_avoids_value_roundtrip() {
        let line = br#"{"version":1,"id":"7","ok":true,"result":{"ok":true,"artBase":{"covers":"https://covers.exo-launcher.local/"},"settings":{},"library":{"games":[],"count":0},"musicPlayback":{"ok":true,"playback":null},"discordStatus":{},"binaryMediaPipe":"exo-pipe-media"}}"#;
        let pending: Mutex<HashMap<String, PendingCallback>> = Mutex::new(HashMap::new());
        let queue = DispatchQueue::new(EVENT_QUEUE_CAP, VIDEO_FRAME_QUEUE_CAP);
        let (res_tx, res_rx) = mpsc::channel();
        pending.lock().unwrap().insert(
            "7".into(),
            Box::new(move |raw_res| {
                let boot = raw_res.and_then(decode_wire_typed::<ShellBootstrap>);
                let _ = res_tx.send(boot);
            }),
        );
        dispatch_line_to(line, &pending, &queue);
        match queue.pop_blocking().unwrap() {
            DispatchWork::Response(cb, res) => cb(res),
            _ => panic!("expected Response"),
        }
        let boot = res_rx.try_recv().unwrap().expect("decoded ShellBootstrap");
        assert!(boot.ok);
        assert_eq!(boot.binary_media_pipe.as_deref(), Some("exo-pipe-media"));
    }

    #[test]
    #[ignore]
    fn bench_dispatch_cost() {
        let mut games = String::from(r#"{"version":1,"id":"1","ok":true,"result":{"games":["#);
        for i in 0..250 {
            if i > 0 {
                games.push(',');
            }
            games.push_str(&format!(
                r#"{{"id":"steam:{i}","title":"Benchmark Game {i}","store":"steam","installed":true,"playtimeMinutes":{},"isRunning":false,"canStop":false,"cover":"https://covers.exo-launcher.local/tok/steam_{i}.jpg","hero":"https://covers.exo-launcher.local/tok/hero_{i}.jpg","logo":"https://covers.exo-launcher.local/tok/logo_{i}.png","status":"Ready","launchTarget":"{i}","genres":["Action","RPG"],"tags":["Singleplayer","Controller"],"variants":[]}}"#,
                i * 15
            ));
        }
        games.push_str(r#"],"count":250,"recent":["steam:1","steam:2"]}}"#);
        let lib_bytes = games.as_bytes();
        let mut reader_times = Vec::with_capacity(50);
        let mut total_times = Vec::with_capacity(50);
        let pending: Mutex<HashMap<String, PendingCallback>> = Mutex::new(HashMap::new());
        let queue = DispatchQueue::new(EVENT_QUEUE_CAP, VIDEO_FRAME_QUEUE_CAP);
        for _ in 0..50 {
            let (done_tx, done_rx) = mpsc::channel();
            pending.lock().unwrap().insert(
                "1".to_string(),
                Box::new(move |res: Result<WirePayload, RpcError>| {
                    let typed = res.and_then(decode_wire_typed::<LibraryPayload>);
                    let _ = done_tx.send(typed.unwrap().games.len());
                }),
            );
            let t0 = std::time::Instant::now();
            dispatch_line_to(lib_bytes, &pending, &queue);
            let reader_ms = t0.elapsed().as_secs_f64() * 1000.0;
            if let DispatchWork::Response(cb, res) = queue.pop_blocking().unwrap() {
                cb(res);
            }
            let count = done_rx.recv().unwrap();
            assert_eq!(count, 250);
            reader_times.push(reader_ms);
            total_times.push(t0.elapsed().as_secs_f64() * 1000.0);
        }
        reader_times.sort_by(|a, b| a.total_cmp(b));
        total_times.sort_by(|a, b| a.total_cmp(b));
        println!(
            "LIB_DISPATCH_READER bytes={} n=50 p50={:.3}ms p95={:.3}ms max={:.3}ms",
            lib_bytes.len(),
            reader_times[24],
            reader_times[47],
            reader_times[49]
        );
        println!(
            "LIB_DISPATCH_TOTAL bytes={} n=50 p50={:.3}ms p95={:.3}ms max={:.3}ms",
            lib_bytes.len(),
            total_times[24],
            total_times[47],
            total_times[49]
        );

        let raw_frame = vec![0xABu8; 64 * 1024];
        let b64 = base64::encode(&raw_frame);
        let frame_json = format!(
            r#"{{"event":"binaryFrame","meta":{{"source":"stream","userId":"200002","timestamp":1234.5,"keyframe":false,"codec":"h264"}},"data":"{b64}"}}"#
        );
        let frame_bytes = frame_json.as_bytes();
        let mut frame_json_times = Vec::with_capacity(120);
        for _ in 0..120 {
            let t0 = std::time::Instant::now();
            dispatch_line_to(frame_bytes, &pending, &queue);
            match queue.pop_blocking().unwrap() {
                DispatchWork::VideoFrame(f) => assert_eq!(f.data.len(), 64 * 1024),
                _ => panic!("expected VideoFrame"),
            }
            frame_json_times.push(t0.elapsed().as_secs_f64() * 1000.0);
        }
        frame_json_times.sort_by(|a, b| a.total_cmp(b));
        let sum60_json: f64 = frame_json_times.iter().take(60).sum();
        println!(
            "VIDEO_FRAME_JSON_FALLBACK bytes={} n=120 p50={:.3}ms p95={:.3}ms max={:.3}ms 60fps_cpu={:.2}ms/s",
            frame_bytes.len(),
            frame_json_times[59],
            frame_json_times[113],
            frame_json_times[119],
            sum60_json
        );

        let meta_bytes = br#"{"source":"stream","userId":"200002","timestamp":1234.5,"keyframe":false,"codec":"h264"}"#;
        let mut frame_bin_times = Vec::with_capacity(120);
        for _ in 0..120 {
            let payload = raw_frame.clone();
            let t0 = std::time::Instant::now();
            let f = decode_binary_media_frame(meta_bytes, payload).unwrap();
            assert_eq!(f.data.len(), 64 * 1024);
            frame_bin_times.push(t0.elapsed().as_secs_f64() * 1000.0);
        }
        frame_bin_times.sort_by(|a, b| a.total_cmp(b));
        let sum60_bin: f64 = frame_bin_times.iter().take(60).sum();
        println!(
            "VIDEO_FRAME_BINARY bytes={} n=120 p50={:.3}ms p95={:.3}ms max={:.3}ms 60fps_cpu={:.2}ms/s",
            meta_bytes.len() + raw_frame.len() + 8,
            frame_bin_times[59],
            frame_bin_times[113],
            frame_bin_times[119],
            sum60_bin
        );
    }

    #[test]
    fn bounded_queue_drops_oldest_when_full() {
        let q = BoundedQueue::new(3);
        for i in 0..5 {
            assert!(q.push_drop_oldest(i));
        }
        assert_eq!(q.pop_blocking(), Some(2));
        assert_eq!(q.pop_blocking(), Some(3));
        assert_eq!(q.pop_blocking(), Some(4));
        q.close();
        assert_eq!(q.pop_blocking(), None);
        assert!(!q.push_drop_oldest(9));
    }

    #[test]
    fn dispatch_queue_bounds_events_video_and_coalesces_music_playback() {
        let q = DispatchQueue::new(4, 2);
        for i in 0..10 {
            q.send(DispatchWork::Event(Box::new(HostEvent::DiscordPresence(
                DiscordPresenceEvent {
                    user_id: format!("u{i}"),
                    status: "online".into(),
                    ..Default::default()
                },
            ))));
        }
        for i in 0..5 {
            q.send(DispatchWork::Event(Box::new(HostEvent::MusicPlayback(
                Some(MusicPlayback {
                    title: format!("track-{i}"),
                    position_ms: Some(i * 1000),
                    ..Default::default()
                }),
            ))));
        }
        for i in 0..5 {
            q.send(DispatchWork::VideoFrame(DiscordVideoFrame {
                source: "stream".into(),
                user_id: format!("u{i}"),
                timestamp: i as f64,
                keyframe: false,
                rotation: 0,
                flip: false,
                codec: "h264".into(),
                data: vec![i as u8; 16],
            }));
        }
        let mut presences = Vec::new();
        let mut music = Vec::new();
        let mut frames = Vec::new();
        q.close();
        while let Some(work) = q.pop_blocking() {
            match work {
                DispatchWork::Event(ev) => match *ev {
                    HostEvent::DiscordPresence(p) => presences.push(p.user_id),
                    HostEvent::MusicPlayback(Some(m)) => music.push(m.title),
                    _ => {}
                },
                DispatchWork::VideoFrame(f) => frames.push(f.user_id),
                _ => {}
            }
        }
        assert_eq!(presences, vec!["u7", "u8", "u9"]);
        assert_eq!(music, vec!["track-4"]);
        assert_eq!(frames, vec!["u3", "u4"]);
    }

    #[test]
    fn parses_all_wp9_push_events() {
        assert!(matches!(
            parse_event("gpu.driver", r#"{"stage":"downloading","percent":42}"#, None),
            Ok(HostEvent::GpuDriver(p)) if p.stage == "downloading" && (p.percent - 42.0).abs() < 0.01
        ));
        assert!(matches!(
            parse_event("gpu.driverInstall", r#"{"phase":"restoring","version":"621.35","restored":["profile"]}"#, None),
            Ok(HostEvent::GpuDriverInstall(ev)) if ev.phase == "restoring" && ev.version.as_deref() == Some("621.35") && ev.restored == vec!["profile"]
        ));
        assert!(matches!(
            parse_event("drivers.step", r#"{"id":"drv1","working":"Installing"}"#, None),
            Ok(HostEvent::DriversStep(s)) if s.id == "drv1"
        ));
        assert!(matches!(
            parse_event("bios.progress", r#"{"share":0.75}"#, None),
            Ok(HostEvent::BiosProgress { share }) if (share - 0.75).abs() < 0.01
        ));
        assert!(matches!(
            parse_event("compress.progress", r#"{"id":"steam:730","compress":{"state":"compressing","percent":0.5}}"#, None),
            Ok(HostEvent::CompressProgress(c)) if c.id == "steam:730" && c.compress.state == "compressing"
        ));
        assert!(matches!(
            parse_event("runtimes.progress", r#"{"phase":"download","name":"Visual C++"}"#, None),
            Ok(HostEvent::RuntimesProgress(p)) if p.name.as_deref() == Some("Visual C++")
        ));
        assert!(matches!(
            parse_event("runtimes.done", r#"{"ok":true,"results":[{"id":"vcredist","name":"Visual C++","outcome":"installed"}]}"#, None),
            Ok(HostEvent::RuntimesDone(r)) if r.ok && r.results.len() == 1
        ));
        assert!(matches!(
            parse_event("shaders.event", r#"{"name":"progress","data":{"rev":2}}"#, None),
            Ok(HostEvent::ShadersEvent(s)) if s.name == "progress"
        ));
        assert!(matches!(
            parse_event("shaders.rebuild", r#"{"state":"running","ready":3,"total":8}"#, None),
            Ok(HostEvent::ShadersRebuild(r)) if r.ready == 3 && r.total == 8
        ));
    }

    #[test]
    fn parses_perf_tuning_and_hdr_events_and_dtos() {
        assert!(matches!(
            parse_event(
                "gpu.tuningChanged",
                r#"{"mode":"manual","statusLine":"Manual active · NVAPI","pausedAfterCrash":false,"queuedPending":false,"backend":"nvapi"}"#,
                None
            ),
            Ok(HostEvent::GpuTuningChanged(ev)) if ev.mode == "manual" && ev.backend == "nvapi"
        ));
        assert!(matches!(
            parse_event(
                "gpu.tuningTestProgress",
                r#"{"running":true,"kind":"stability","phase":"running","elapsedSec":15,"totalSec":60,"passes":30,"checksumOk":true,"currentStep":"Pass 30"}"#,
                None
            ),
            Ok(HostEvent::GpuTuningTestProgress(p)) if p.running && p.elapsed_sec == 15 && p.total_sec == 60
        ));
        assert!(matches!(
            parse_event(
                "hdr.calibrationProgress",
                r#"{"devicePath":"DISPLAY#1","step":3,"total":8,"slider":60,"measuredNits":612,"targetNits":600}"#,
                None
            ),
            Ok(HostEvent::HdrCalibrationProgress(p)) if p.step == 3 && p.measured_nits == Some(612)
        ));
        assert!(matches!(
            parse_event(
                "hdr.warning",
                r#"{"devicePath":"DISPLAY#1","kind":"brightness","message":"Brightness changed externally"}"#,
                None
            ),
            Ok(HostEvent::HdrWarning(w)) if w.kind == "brightness"
        ));

        let range: GpuTuningRange = serde_json::from_str(
            r#"{"min":-500,"max":500,"step":15,"default":0,"supported":true}"#,
        )
        .expect("range");
        assert!(range.supported);
        assert_eq!(range.min, -500);
        assert_eq!(range.max, 500);
        assert_eq!(range.step, 15);
        assert_eq!(range.default_val, 0);

        let hdr: HdrState = serde_json::from_str(
            r#"{"supported":true,"internal":true,"edidPeakNits":600,"dxgiPeakNits":420,"peakHealth":{"status":"mismatch","edidNits":600,"dxgiNits":420,"gapPercent":30,"warning":"Reports 30% below EDID"}}"#,
        )
        .expect("hdr");
        assert!(hdr.internal);
        assert_eq!(
            hdr.peak_health.as_ref().map(|h| h.status.as_str()),
            Some("mismatch")
        );

        let dc: DiscordClientSettings =
            serde_json::from_str(r#"{"evenVoices":true,"voiceTargetLufs":-18,"micAutoGain":true}"#)
                .expect("discord settings");
        assert_eq!(dc.voice_target_lufs, -18);
        assert!(dc.mic_auto_gain);
    }

    #[test]
    fn wire_frame_dispatch_handles_binary_response_event_error_and_media() {
        let pending: Mutex<HashMap<String, PendingCallback>> = Mutex::new(HashMap::new());
        let queue = DispatchQueue::new(EVENT_QUEUE_CAP, VIDEO_FRAME_QUEUE_CAP);

        let (extras_tx, extras_rx) = mpsc::channel();
        pending.lock().unwrap().insert(
            "11".into(),
            Box::new(move |res| {
                let _ = extras_tx.send(res.and_then(decode_wire_typed::<GameExtras>));
            }),
        );
        let mut ew = exo_wire::WireWriter::with_capacity(64);
        ew.put_u8(exo_wire::PayloadEncoding::BinaryTyped as u8);
        ew.put_bool(true);
        ew.put_opt_u32(Some(9));
        ew.put_opt_u32(Some(25));
        ew.put_bool(false);
        ew.put_opt_str(None);
        dispatch_wire_frame(
            exo_wire::FrameHeader {
                version: exo_wire::WIRE_VERSION,
                kind: exo_wire::FrameKind::ResponseOk,
                method_id: exo_wire::METHOD_ID_GAME_EXTRAS,
                req_id: 11,
                payload_len: ew.as_slice().len() as u32,
            },
            ew.as_slice(),
            &pending,
            &queue,
        );
        if let DispatchWork::Response(cb, res) = queue.pop_blocking().unwrap() {
            cb(res);
        }
        let extras = extras_rx.try_recv().unwrap().unwrap();
        assert!(extras.ok);
        assert_eq!(extras.achievements_unlocked, Some(9));
        assert_eq!(extras.achievements_total, Some(25));

        let (err_tx, err_rx) = mpsc::channel();
        pending.lock().unwrap().insert(
            "12".into(),
            Box::new(move |res| {
                let _ = err_tx.send(res.and_then(decode_wire_to_value));
            }),
        );
        dispatch_wire_frame(
            exo_wire::FrameHeader {
                version: exo_wire::WIRE_VERSION,
                kind: exo_wire::FrameKind::ResponseErr,
                method_id: exo_wire::METHOD_ID_GAME_GET,
                req_id: 12,
                payload_len: 9,
            },
            b"not found",
            &pending,
            &queue,
        );
        if let DispatchWork::Response(cb, res) = queue.pop_blocking().unwrap() {
            cb(res);
        }
        assert!(matches!(err_rx.try_recv().unwrap(), Err(RpcError::Host(m)) if m == "not found"));

        let mut aw = exo_wire::WireWriter::with_capacity(64);
        aw.put_u8(exo_wire::PayloadEncoding::BinaryTyped as u8);
        aw.put_bool(true);
        aw.put_str("steam:870780");
        aw.put_str("Control");
        aw.put_f64(10.0);
        aw.put_f64(144.0);
        aw.put_f64(110.0);
        aw.put_f64(90.0);
        aw.put_i32(0);
        aw.put_u16(0);
        dispatch_wire_frame(
            exo_wire::FrameHeader {
                version: exo_wire::WIRE_VERSION,
                kind: exo_wire::FrameKind::Event,
                method_id: exo_wire::EVENT_ID_AUTOPILOT_LIVE,
                req_id: 0,
                payload_len: aw.as_slice().len() as u32,
            },
            aw.as_slice(),
            &pending,
            &queue,
        );
        assert!(matches!(
            queue.pop_blocking().unwrap(),
            DispatchWork::Event(ev) if matches!(&*ev, HostEvent::AutopilotLive(Some(live)) if live.fps == 144.0)
        ));

        let mut mw = exo_wire::WireWriter::with_capacity(64);
        mw.put_str(r#"{"source":"screen:1","userId":"u1","timestamp":55,"keyframe":true,"rotation":0,"flip":false,"codec":"h264"}"#);
        mw.put_raw(&[0xaa, 0xbb, 0xcc]);
        dispatch_wire_frame(
            exo_wire::FrameHeader {
                version: exo_wire::WIRE_VERSION,
                kind: exo_wire::FrameKind::BinaryMedia,
                method_id: 0,
                req_id: 0,
                payload_len: mw.as_slice().len() as u32,
            },
            mw.as_slice(),
            &pending,
            &queue,
        );
        assert!(matches!(
            queue.pop_blocking().unwrap(),
            DispatchWork::VideoFrame(frame) if frame.data == [0xaa, 0xbb, 0xcc]
        ));
    }

    #[cfg(windows)]
    #[test]
    fn auto_wire_mode_falls_back_to_json_against_legacy_ndjson_server() {
        use std::ffi::OsStr;
        use std::os::windows::ffi::OsStrExt;
        use std::ptr::{null, null_mut};
        use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
        use windows_sys::Win32::Storage::FileSystem::{PIPE_ACCESS_DUPLEX, ReadFile, WriteFile};
        use windows_sys::Win32::System::Pipes::{
            ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_WAIT,
        };

        let pipe_name = format!(
            "exo-legacy-fallback-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        );
        let full = format!(r"\\.\pipe\{pipe_name}");
        let wide: Vec<u16> = OsStr::new(&full).encode_wide().chain(Some(0)).collect();

        let server_handle = unsafe {
            CreateNamedPipeW(
                wide.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                64 * 1024,
                64 * 1024,
                0,
                null(),
            )
        };
        assert_ne!(server_handle, INVALID_HANDLE_VALUE);
        let handle_addr = server_handle as usize;

        let server = std::thread::spawn(move || {
            let h = handle_addr as windows_sys::Win32::Foundation::HANDLE;
            unsafe {
                ConnectNamedPipe(h, null_mut());
            }
            let mut acc = Vec::new();
            let mut buf = [0u8; 4096];
            let mut lines_seen = 0;
            while lines_seen < 2 {
                let mut read = 0u32;
                let ok = unsafe {
                    ReadFile(h, buf.as_mut_ptr(), buf.len() as u32, &mut read, null_mut())
                };
                if ok == 0 || read == 0 {
                    break;
                }
                acc.extend_from_slice(&buf[..read as usize]);
                while let Some(pos) = acc.iter().position(|&b| b == b'\n') {
                    let line = acc[..pos].to_vec();
                    acc.drain(..=pos);
                    lines_seen += 1;
                    if let Ok(v) = serde_json::from_slice::<Value>(&line) {
                        let id = v.get("id").and_then(Value::as_str).unwrap_or("1");
                        let reply = format!(
                            "{{\"version\":1,\"id\":\"{id}\",\"ok\":true,\"result\":{{\"ok\":true,\"legacy\":true}}}}\n"
                        );
                        let mut written = 0u32;
                        unsafe {
                            WriteFile(
                                h,
                                reply.as_ptr(),
                                reply.len() as u32,
                                &mut written,
                                null_mut(),
                            );
                        }
                    }
                }
            }
            unsafe {
                CloseHandle(h);
            }
        });

        let client = RpcClient::connect_with_preference(
            &pipe_name,
            Duration::from_secs(3),
            WirePreference::Auto,
            |_| {},
        )
        .expect("connect with auto fallback");
        assert_eq!(client.wire_mode(), ActiveWireMode::Json);

        let reply: Value = client
            .call_blocking("system.about", &json!({}), Duration::from_secs(3))
            .expect("legacy json rpc succeeded");
        assert_eq!(reply.get("legacy").and_then(Value::as_bool), Some(true));

        client.close();
        let _ = server.join();
    }

    #[cfg(windows)]
    #[test]
    fn json_only_legacy_host_negotiates_down_and_serves_every_host_v1_method() {
        use std::ffi::OsStr;
        use std::os::windows::ffi::OsStrExt;
        use std::ptr::{null, null_mut};
        use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
        use windows_sys::Win32::Storage::FileSystem::{PIPE_ACCESS_DUPLEX, ReadFile, WriteFile};
        use windows_sys::Win32::System::Pipes::{
            ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_WAIT,
        };

        let pipe_name = format!(
            "exo-json-all-methods-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        );
        let full = format!(r"\\.\pipe\{pipe_name}");
        let wide: Vec<u16> = OsStr::new(&full).encode_wide().chain(Some(0)).collect();

        let server_handle = unsafe {
            CreateNamedPipeW(
                wide.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                256 * 1024,
                256 * 1024,
                0,
                null(),
            )
        };
        assert_ne!(server_handle, INVALID_HANDLE_VALUE);
        let handle_addr = server_handle as usize;
        let expected_methods = exo_wire::METHODS.len();

        let server = std::thread::spawn(move || {
            let h = handle_addr as windows_sys::Win32::Foundation::HANDLE;
            unsafe {
                ConnectNamedPipe(h, null_mut());
            }
            let mut acc = Vec::new();
            let mut buf = [0u8; 16 * 1024];
            let mut handled = 0usize;
            while handled < expected_methods {
                let mut read = 0u32;
                let ok = unsafe {
                    ReadFile(h, buf.as_mut_ptr(), buf.len() as u32, &mut read, null_mut())
                };
                if ok == 0 || read == 0 {
                    break;
                }
                acc.extend_from_slice(&buf[..read as usize]);
                while let Some(pos) = acc.iter().position(|&b| b == b'\n') {
                    let line = acc[..pos].to_vec();
                    acc.drain(..=pos);
                    if let Ok(v) = serde_json::from_slice::<Value>(&line) {
                        let Some(id) = v.get("id").and_then(Value::as_str) else {
                            continue;
                        };
                        let Some(method) = v.get("method").and_then(Value::as_str) else {
                            continue;
                        };
                        handled += 1;
                        let reply = serde_json::to_string(&json!({
                            "version": 1,
                            "id": id,
                            "ok": true,
                            "result": { "method": method }
                        }))
                        .unwrap()
                            + "\n";
                        let mut written = 0u32;
                        unsafe {
                            WriteFile(
                                h,
                                reply.as_ptr(),
                                reply.len() as u32,
                                &mut written,
                                null_mut(),
                            );
                        }
                    }
                }
            }
            unsafe {
                CloseHandle(h);
            }
            handled
        });

        let client = RpcClient::connect_with_preference(
            &pipe_name,
            Duration::from_secs(3),
            WirePreference::Auto,
            |_| {},
        )
        .expect("connect to json-only host");
        assert_eq!(client.wire_mode(), ActiveWireMode::Json);

        for &method in exo_wire::METHODS {
            let reply: Value = client
                .call_blocking(method, &json!({}), Duration::from_secs(3))
                .unwrap_or_else(|e| panic!("json fallback failed for {method}: {e}"));
            assert_eq!(
                reply.get("method").and_then(Value::as_str),
                Some(method),
                "wrong echoed method for {method}"
            );
        }

        client.close();
        let handled = server.join().expect("server thread");
        assert_eq!(handled, exo_wire::METHODS.len());
    }

    #[test]
    fn fuzz_corrupt_binary_hot_decoders_never_panic() {
        let queue = DispatchQueue::new(32, 8);
        let pending: Mutex<HashMap<String, PendingCallback>> = Mutex::new(HashMap::new());
        let mut seed: u64 = 0xd1b5_4a32_d192_ed03;
        let mut next_u64 = || {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            seed
        };

        let length_bombs: &[&[u8]] = &[
            &[0xFF, 0xFF, 0xFF, 0xFF],
            &[1, 1, 0xFF, 0xFF, 0xFF, 0xFF],
            &[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF],
            &[99, 0xFF, 0xFE, 0xFD],
        ];
        for &bomb in length_bombs {
            assert!(wire_codec::decode_game_get_reply(bomb).is_err());
            assert!(wire_codec::decode_library_payload(bomb).is_err());
            assert!(wire_codec::decode_game_extras(bomb).is_err());
            assert!(wire_codec::decode_shell_bootstrap(bomb).is_err());
            assert!(wire_codec::decode_autopilot_live(bomb).is_err());
            assert!(wire_codec::decode_install_progress_frame(bomb).is_err());
        }

        let kinds = [
            exo_wire::FrameKind::Request,
            exo_wire::FrameKind::ResponseOk,
            exo_wire::FrameKind::ResponseErr,
            exo_wire::FrameKind::Event,
            exo_wire::FrameKind::BinaryMedia,
            exo_wire::FrameKind::Hello,
            exo_wire::FrameKind::HelloAck,
        ];

        for case in 0..2048usize {
            let len = (next_u64() as usize) % 128;
            let mut buf = vec![0u8; len];
            for b in &mut buf {
                *b = (next_u64() & 0xFF) as u8;
            }
            let _ = wire_codec::decode_game_get_reply(&buf);
            let _ = wire_codec::decode_library_payload(&buf);
            let _ = wire_codec::decode_game_extras(&buf);
            let _ = wire_codec::decode_shell_bootstrap(&buf);
            let _ = wire_codec::decode_autopilot_live(&buf);
            let _ = wire_codec::decode_install_progress_frame(&buf);

            let hdr = exo_wire::FrameHeader {
                version: exo_wire::WIRE_VERSION,
                kind: kinds[case % kinds.len()],
                method_id: (next_u64() & 0xFFFF) as u16,
                req_id: case as u64,
                payload_len: buf.len() as u32,
            };
            dispatch_wire_frame(hdr, &buf, &pending, &queue);
        }
    }

    #[cfg(windows)]
    #[test]
    fn corrupt_binary_frame_on_live_pipe_drops_to_error_without_panic_or_hang() {
        use std::ffi::OsStr;
        use std::os::windows::ffi::OsStrExt;
        use std::ptr::{null, null_mut};
        use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
        use windows_sys::Win32::Storage::FileSystem::{PIPE_ACCESS_DUPLEX, ReadFile, WriteFile};
        use windows_sys::Win32::System::Pipes::{
            ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_WAIT,
        };

        let cases: &[(&str, Vec<u8>)] = &[
            (
                "bad_magic_garbage",
                b"NOT_EXW1_CORRUPT_GARBAGE_BYTES_0123456789".to_vec(),
            ),
            ("oversized_payload_len", {
                let mut h = exo_wire::FrameHeader {
                    version: exo_wire::WIRE_VERSION,
                    kind: exo_wire::FrameKind::ResponseOk,
                    method_id: exo_wire::METHOD_ID_GAME_GET,
                    req_id: 1,
                    payload_len: 4,
                }
                .encode()
                .to_vec();
                h[16..20].copy_from_slice(&u32::MAX.to_le_bytes());
                h
            }),
            ("invalid_encoding_byte", {
                let mut w = exo_wire::WireWriter::with_capacity(32);
                w.reserve_header();
                w.put_u8(99);
                w.put_raw(b"bad");
                w.finalize_frame(
                    exo_wire::FrameKind::ResponseOk,
                    exo_wire::METHOD_ID_GAME_GET,
                    1,
                );
                w.into_vec()
            }),
            ("invalid_utf8_json_payload", {
                let mut w = exo_wire::WireWriter::with_capacity(32);
                w.reserve_header();
                w.put_u8(exo_wire::PayloadEncoding::JsonUtf8 as u8);
                w.put_raw(&[0xFF, 0xFE, 0xFD, 0xFC]);
                w.finalize_frame(
                    exo_wire::FrameKind::ResponseOk,
                    exo_wire::METHOD_ID_GAME_GET,
                    1,
                );
                w.into_vec()
            }),
            ("truncated_payload_eof", {
                let mut h = exo_wire::FrameHeader {
                    version: exo_wire::WIRE_VERSION,
                    kind: exo_wire::FrameKind::ResponseOk,
                    method_id: exo_wire::METHOD_ID_GAME_GET,
                    req_id: 1,
                    payload_len: 1024,
                }
                .encode()
                .to_vec();
                h.extend_from_slice(&[1, 2, 3, 4]);
                h
            }),
        ];

        for (label, corrupt_bytes) in cases {
            let pipe_name = format!(
                "exo-corrupt-{label}-{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            );
            let full = format!(r"\\.\pipe\{pipe_name}");
            let wide: Vec<u16> = OsStr::new(&full).encode_wide().chain(Some(0)).collect();

            let server_handle = unsafe {
                CreateNamedPipeW(
                    wide.as_ptr(),
                    PIPE_ACCESS_DUPLEX,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                    1,
                    64 * 1024,
                    64 * 1024,
                    0,
                    null(),
                )
            };
            assert_ne!(
                server_handle, INVALID_HANDLE_VALUE,
                "pipe create for {label}"
            );
            let handle_addr = server_handle as usize;
            let payload_to_send = corrupt_bytes.clone();

            let server = std::thread::spawn(move || {
                let h = handle_addr as windows_sys::Win32::Foundation::HANDLE;
                unsafe {
                    ConnectNamedPipe(h, null_mut());
                }
                let mut buf = [0u8; 256];
                let mut read = 0u32;
                let _ = unsafe {
                    ReadFile(h, buf.as_mut_ptr(), buf.len() as u32, &mut read, null_mut())
                };
                let ack = exo_wire::encode_hello_ack_frame(exo_wire::HelloAckPayload {
                    negotiated_version: exo_wire::WIRE_VERSION,
                    status: exo_wire::HELLO_STATUS_OK,
                    flags: exo_wire::FLAG_USE_SCHEMA_IDS | exo_wire::FLAG_BINARY_HOT_CODECS,
                    max_frame_bytes: exo_wire::MAX_FRAME_BYTES as u32,
                    host_schema_hash: exo_wire::SCHEMA_HASH,
                });
                let mut written = 0u32;
                let _ = unsafe {
                    WriteFile(h, ack.as_ptr(), ack.len() as u32, &mut written, null_mut())
                };
                let _ = unsafe {
                    ReadFile(h, buf.as_mut_ptr(), buf.len() as u32, &mut read, null_mut())
                };
                let _ = unsafe {
                    WriteFile(
                        h,
                        payload_to_send.as_ptr(),
                        payload_to_send.len() as u32,
                        &mut written,
                        null_mut(),
                    )
                };
                unsafe {
                    CloseHandle(h);
                }
            });

            let client = RpcClient::connect_with_preference(
                &pipe_name,
                Duration::from_secs(3),
                WirePreference::Binary,
                |_| {},
            )
            .unwrap_or_else(|e| panic!("connect binary for {label}: {e}"));

            let started = std::time::Instant::now();
            let res: Result<Value, RpcError> = client.call_blocking(
                "game.get",
                &json!({ "id": "steam:1" }),
                Duration::from_secs(2),
            );
            assert!(
                res.is_err(),
                "expected error on corrupt frame case {label}, got {res:?}"
            );
            assert!(
                started.elapsed() < Duration::from_secs(2),
                "corrupt frame case {label} hung until timeout"
            );

            client.close();
            let _ = server.join();
        }
    }

    #[test]
    fn serialize_request_emits_protocol_envelope_directly() {
        #[derive(Serialize)]
        struct P<'a> {
            id: &'a str,
        }
        let raw = serialize_request("9", "game.get", &P { id: "steam:730" }).expect("serialize");
        let parsed: Value = serde_json::from_str(&raw).expect("parse");
        assert_eq!(parsed["version"], PROTOCOL_VERSION);
        assert_eq!(parsed["id"], "9");
        assert_eq!(parsed["method"], "game.get");
        assert_eq!(parsed["params"]["id"], "steam:730");
    }
}
