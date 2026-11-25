use crate::security::validate_store_url;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SharedWebProfile {
    MediaDrm,
    AuthSignIn,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SharedWebSurfaceRequest {
    GogSignIn,
    StorePage {
        title: String,
        url: String,
        profile: SharedWebProfile,
    },
}

impl SharedWebSurfaceRequest {
    pub fn title(&self) -> &str {
        match self {
            Self::GogSignIn => "Sign in to GOG",
            Self::StorePage { title, .. } => title.as_str(),
        }
    }

    pub fn url(&self) -> &str {
        match self {
            Self::GogSignIn => "https://www.gog.com/account",
            Self::StorePage { url, .. } => url.as_str(),
        }
    }

    pub fn profile(&self) -> SharedWebProfile {
        match self {
            Self::GogSignIn => SharedWebProfile::AuthSignIn,
            Self::StorePage { profile, .. } => *profile,
        }
    }

    pub fn is_ephemeral_on_room_leave(&self) -> bool {
        true
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StoreOperation {
    LibraryScan,
    ManifestParse,
    CdnRaceAndDownload,
    DeltaUpdate,
    VerifyAndRepair,
    CloudSaveSync,
    OfficialLaunch,
    InteractiveSignIn,
    DrmCheckoutPage,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StoreExecutionRoute {
    NativeRust,
    SharedLazyWebView2(SharedWebSurfaceRequest),
}

pub fn route_store_operation(
    op: StoreOperation,
    store_label: &str,
    page_url: Option<&str>,
) -> Result<StoreExecutionRoute, String> {
    match op {
        StoreOperation::LibraryScan
        | StoreOperation::ManifestParse
        | StoreOperation::CdnRaceAndDownload
        | StoreOperation::DeltaUpdate
        | StoreOperation::VerifyAndRepair
        | StoreOperation::CloudSaveSync
        | StoreOperation::OfficialLaunch => Ok(StoreExecutionRoute::NativeRust),
        StoreOperation::InteractiveSignIn => {
            if store_label.eq_ignore_ascii_case("gog") && page_url.is_none() {
                return Ok(StoreExecutionRoute::SharedLazyWebView2(
                    SharedWebSurfaceRequest::GogSignIn,
                ));
            }
            let url =
                page_url.ok_or_else(|| "Interactive sign-in requires HTTPS URL".to_string())?;
            validate_store_url(url, false)?;
            Ok(StoreExecutionRoute::SharedLazyWebView2(
                SharedWebSurfaceRequest::StorePage {
                    title: format!("Sign in to {store_label}"),
                    url: url.to_string(),
                    profile: SharedWebProfile::AuthSignIn,
                },
            ))
        }
        StoreOperation::DrmCheckoutPage => {
            let url = page_url.ok_or_else(|| "DRM store page requires HTTPS URL".to_string())?;
            validate_store_url(url, false)?;
            Ok(StoreExecutionRoute::SharedLazyWebView2(
                SharedWebSurfaceRequest::StorePage {
                    title: store_label.to_string(),
                    url: url.to_string(),
                    profile: SharedWebProfile::MediaDrm,
                },
            ))
        }
    }
}
