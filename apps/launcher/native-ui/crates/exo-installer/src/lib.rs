pub mod fetch;
pub mod payload;
pub mod shell_reg;
pub mod swap;
pub mod ui_d2d;

use std::fmt;

#[derive(Debug)]
pub enum InstallError {
    Io(std::io::Error),
    Delta(exo_delta::DeltaError),
    Win32(String),
    InvalidArg(String),
    SafetyRefusal(String),
    Network(String),
    Health(String),
}

impl fmt::Display for InstallError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for InstallError {}

impl From<std::io::Error> for InstallError {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

impl From<exo_delta::DeltaError> for InstallError {
    fn from(e: exo_delta::DeltaError) -> Self {
        Self::Delta(e)
    }
}
