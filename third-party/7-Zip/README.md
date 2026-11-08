# 7-Zip (7za.exe)

Arca uses 7-Zip's standalone command-line program to create `.7z` archives and password-protected (AES-256) archives,
which the managed library it reads archives with (SharpCompress) cannot write. Reading never uses it.

- File: `7za.exe`, the 64-bit (`x64/`) copy from the official **7-Zip Extra 26.05** package by Igor Pavlov,
  `7z2605-extra.7z` from <https://github.com/ip7z/7zip/releases/tag/26.05>.
- SHA-256 of `7z2605-extra.7z`: `f27a8f103c84785026edcba1f67faf1357225df9d19ddb0b7760d55947141bc7`
- SHA-256 of `7za.exe`: `7ddf0a3bc451e166679296be630d6f0573eb386159c04184761c43020c484291`
- Licence: GNU LGPL, with parts under the BSD 3-clause and 2-clause licences; see `License.txt`, which ships next to
  `7za.exe` in every Arca release as that licence requires. The package contains no unRAR code.
- `License.txt` allows signing the exe with a third party's certificate, so the release signs it with Arca's.

To update: download the newer `7z<ver>-extra.7z` from the same releases page, replace `7za.exe` with its `x64/7za.exe`
and `License.txt` with its `License.txt`, and update the version and both hashes above.
