# Third-party notices

GameHours includes or links the following third-party software in its distributed package.

## Ludusavi

- Project: https://github.com/mtkennerly/ludusavi
- Version: `0.31.0`
- Revision: `8844d7b67e784909f4ef42f7bfb047b700fe7b15`
- License: MIT
- Copyright: Copyright (c) 2020 Matthew T. Kennerly (mtkennerly)

The GameHours SaveEngine links Ludusavi as a Rust library with its default `app` feature disabled. GameHours does not distribute Ludusavi's GUI/CLI application.

The complete package/license inventory for the Rust crates linked into the Windows SaveEngine binary is shipped as `THIRD-PARTY-RUST-LICENSES.txt` next to this notice.

## Ludusavi Manifest

- Project: https://github.com/mtkennerly/ludusavi-manifest
- Revision: `911eafe249166f6e115ab53a73f6c3532ed6e7b5`
- Upstream source: `data/manifest.yaml`
- Upstream SHA-256: `193A7B47A9DD80E9CA1C239D7CF78B50720902D4B0F9BC38D23949715E4B77BF`
- Distributed SHA-256: `87E69A3CE52F1170FF35FEDA47D0041E5BE21E4195478722684C8E119469896D`
- License: MIT
- Copyright: Copyright (c) 2020 Matthew T. Kennerly (mtkennerly)

GameHours distributes a deterministic sanitized snapshot of this pinned manifest as save-location data for offline Save Safety previews. The transformation removes every `launch` block because Ludusavi 0.31 save scanning does not consume it and upstream launch metadata can contain historical launcher credentials. Save-layout fields and store IDs remain in upstream YAML form; GameHours does not rewrite them into a private catalogue.

### MPL-2.0 transitive component

`option-ext` version `0.2.0` is an unchanged transitive dependency through `Ludusavi -> dirs -> dirs-sys -> option-ext` and is licensed under MPL-2.0. Its source is available from:

- https://crates.io/crates/option-ext/0.2.0
- https://github.com/soc/option-ext

GameHours does not copy or modify `option-ext` source in its MIT-owned files. The MPL applies to that covered component; GameHours remains a separate work. The full MPL-2.0 text distributed by the crate is included in `THIRD-PARTY-RUST-LICENSES.txt`.

MIT License

Copyright (c) 2020 Matthew T. Kennerly (mtkennerly)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
