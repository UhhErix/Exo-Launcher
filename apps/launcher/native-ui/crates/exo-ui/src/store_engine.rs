use exo_rpc::anticheat::AntiCheatClass;
use exo_stores::downloader::DownloadThrottle;
use exo_stores::security::StoreGamePolicy;
use exo_stores::webview_fallback::{
    SharedWebSurfaceRequest, StoreExecutionRoute, StoreOperation, route_store_operation,
};
use std::sync::Arc;

#[derive(Debug, Clone, Default)]
pub struct NativeStoreEngineBridge {
    pub active_web_fallback: Option<SharedWebSurfaceRequest>,
    pub download_throttle: Arc<DownloadThrottle>,
}

impl NativeStoreEngineBridge {
    pub fn route_store_page(
        &mut self,
        op: StoreOperation,
        store_label: &str,
        url: Option<&str>,
    ) -> Result<StoreExecutionRoute, String> {
        let route = route_store_operation(op, store_label, url)?;
        if let StoreExecutionRoute::SharedLazyWebView2(req) = &route {
            self.active_web_fallback = Some(req.clone());
        }
        Ok(route)
    }

    pub fn leave_store_room(&mut self) {
        if self
            .active_web_fallback
            .as_ref()
            .is_some_and(SharedWebSurfaceRequest::is_ephemeral_on_room_leave)
        {
            self.active_web_fallback = None;
        }
    }

    pub fn sync_running_game_throttle(&self, running_class: Option<AntiCheatClass>) {
        match running_class {
            Some(cls) => {
                let pol = StoreGamePolicy::for_class(cls);
                if pol.pause_downloads_while_running {
                    self.download_throttle.set_game_running(true, 0);
                } else {
                    self.download_throttle
                        .set_game_running(true, 8 * 1024 * 1024);
                }
            }
            None => {
                self.download_throttle.set_game_running(false, 0);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::Ordering;

    use super::*;

    #[test]
    fn native_store_engine_routes_drm_to_shared_lazy_webview_and_pauses_for_unknown_anticheat() {
        let mut bridge = NativeStoreEngineBridge::default();
        assert_eq!(
            bridge
                .route_store_page(StoreOperation::LibraryScan, "Steam", None)
                .unwrap(),
            StoreExecutionRoute::NativeRust
        );
        assert!(bridge.active_web_fallback.is_none());

        let route = bridge
            .route_store_page(
                StoreOperation::DrmCheckoutPage,
                "Steam Store",
                Some("https://store.steampowered.com/app/730/"),
            )
            .unwrap();
        assert!(matches!(route, StoreExecutionRoute::SharedLazyWebView2(_)));
        assert!(bridge.active_web_fallback.is_some());

        bridge.leave_store_room();
        assert!(bridge.active_web_fallback.is_none());

        bridge.sync_running_game_throttle(Some(AntiCheatClass::Unknown));
        assert!(
            bridge
                .download_throttle
                .paused_for_game
                .load(Ordering::SeqCst)
        );
        bridge.sync_running_game_throttle(None);
        assert!(
            !bridge
                .download_throttle
                .paused_for_game
                .load(Ordering::SeqCst)
        );
    }
}
