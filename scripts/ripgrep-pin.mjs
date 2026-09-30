// The one ripgrep pin shared by dev setup (scripts/setup-ripgrep.mjs) and the packaged
// runtime (apps/desktop/scripts/stage-runtime.mjs): file_search must run the same rg in
// both, so the version and its checksum live here and nowhere else.
export const RIPGREP_VERSION = "15.2.0";
export const RIPGREP_ASSET = `ripgrep-${RIPGREP_VERSION}-x86_64-pc-windows-msvc.zip`;
export const RIPGREP_SHA256 =
	"71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5";
export const RIPGREP_URL = `https://github.com/BurntSushi/ripgrep/releases/download/${RIPGREP_VERSION}/${RIPGREP_ASSET}`;
