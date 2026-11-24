fn main() {
    #[cfg(target_env = "msvc")]
    println!("cargo:rustc-link-arg-bin=exo-ui=/STACK:8388608");
}
