fn main() {
    println!("cargo:rerun-if-changed=exo_apo.def");
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        let dir = std::env::var("CARGO_MANIFEST_DIR").unwrap_or_default();
        println!("cargo:rustc-cdylib-link-arg=/DEF:{dir}\\exo_apo.def");
    }
}
