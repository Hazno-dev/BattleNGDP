# BattleNGDP

**BattleNGDP** is an open-source tool designed for the downloading, decryption, and parsing of files from Blizzard's
Next Generation Data Pipeline ([NGDP](https://wowdev.wiki/NGDP)).


---

**License: TBD**

---

## Features

**BattleNGDP** supports many features from [CASC](https://wowdev.wiki/CASC) and TACT
([Trusted Application Content Transfer](https://wowdev.wiki/TACT)):

- Remote loading of TACT products
    - Loads and caches build information from [BlizzTrack](https://blizztrack.com/docs).
    - Loads and caches build information from [TACT Channels](https://wowdev.wiki/TACT_Channels).
    - Currently, no support for [Ribbit](https://wowdev.wiki/Ribbit) is planned.
- Parsing of numerous TACT formats:
    - Install Manifest.
    - VFS Manifest (s).
    - Encoding Manifest.
    - Download Manifest.
    - Index Manifest.
    - Archives & .index files.
    - Build/CDN Configs
    - Keyring files
    - Armadillo files (`.ak`)
- TVFS ([TACT Virtual File System](https://wowdev.wiki/TVFS))
    - Listing of files, and extraction of files sourced from VFS manifests.
- BLTE ([Block table Encoding](https://wowdev.wiki/BLTE))
    - Extraction of data from BLTE chunks, with Zlib, frame and Encrypted type support.
    - Partial support for BLTE-S (split) chunks, where-by chunks are located in separate locations on the CDN.
- Containerless Builds
    - Listing of files, and extraction of files on builds with `build-file-db` SQL entries (i.e. Witcher 3 Remastered [
      `Lyra`])
    - Decryption of E-key encrypted remote files using Keyrings.
- Keyring Files.
    - Allows for decryption of BLTE and E-Key encrypted files.
    - Automatically downloads and caches Keyring keys locally.
- Armadillo Keys.
    - Including automatic loading and detection of local Armadillo keys.
- Asset and Build caching
    - Downloads and caches files from Blizzards remote CDNs (uses a fixed list of CDN entries).
    - Remote assets and BattleNGDP files are cached locally in the users local application data folder
      (`%AppData%/../Local/Hazno/BattleNGDP` on Windows)
    - TACT product assets (i.e. Data/Config/Archives) are stored under a shared root, allowing for faster loading across
      versions

---

## Usage

Use the builds from `Releases` or compile using .NET 10 (or, just use Rider/Visual Studio 2026).

### Launching

```
BattleNGDP <TACT ID> -f/--fast
    
    [Required]  <TACT ID>   Product TACT Name (i.e. `pro` for Overwatch, `fenris` for Diablo IV)
    [Optional]  -f/--f      Skip GET requests from CDNs if the HEAD request fails
```

### Runners

#### Build Selector

```
  Build Selector  Select a product build
> 74043    XX::XX 0.3.1 [124-132]0_3_1 - RC 1 9d970599d36ce8b76a6875702073b642::7b0286f8449d6862d6a5dc2fc3d3594c (114 Files)
  73883    91e4ace9282344d61013955ec882cca5::7b0286f8449d6862d6a5dc2fc3d3594c
  73883    91e4ace9282344d61013955ec882cca5::37153a58e9928be1d3049e5fac7f6557
  73764    4e888c791d6c01ad61d258ffa460b661::32ba5f1f6ffdcd6a85fcd7fa86a5d417
```

Select a known build for a given TACT product. Loads build/CDN configs and attempts to load all supported manifests.

Identifies builds using `Build::CDN` hashes and version numbers. Builds which have already been loaded before will show
derived build information from the build config (if present). File counts currently *only* use the install manifest.

#### Install Files

```
> Install Files   Read and grab install files.
? Select file to download:
> X.X KiB     X\Y\foo.bin
  X.X KiB     X\Y\bar.exe
(X items, X/X pages)
```

Extract files into the local NGDP application folder. Automatically handles Armadillo encryption.

This currently only supports files derived from the install manifest, and from containerless `build-file-db` databases.

If `[NOT FOUND]` is present, it indicates index/encoding files are missing. Sometimes these assets can still be found as
EKeys *can* directly resolve to a loose file, but this is typically only for very large files (50-100 MB+ standalone
files). If the installer fails to locate the archive, then it's likely the index/encoding/etc manifest required has been
scrubbed from the CDN already so no archive can be obtained.

#### Build Checker

```
> Build Checker   Check all stored builds for product
[LoadConfigs] Loading configs for X v123
[LoadConfigs] [*] Loading build config...
[LoadConfigs] [*] Loading cdn config...
[LoadConfigs] [*] Loading install manifest...
[LoadConfigs] Loaded configs!
[LoadConfigs]
[LoadProductConfig] [*] Loading product config...
[LoadConfigs] Loading configs for X v122
[LoadConfigs] [*] Loading build config...
[LoadConfigs] [*] Loading cdn config...
[LoadConfigs] [*] Loading install manifest...
[LoadConfigs] Loaded configs!
[LoadConfigs]
```

Loops through all identified builds for a product and attempts to load the data. Useful for identifying valid//known
builds.

#### Install TVFS Files

```
> Install TVFS Files Read and grab TVFS files.
? File path to download: X:Foo/bar.txt
```

Enter the file path of a TVFS file to extract. Use this in conjunction with `Dump Filelist` to determine the correct
paths.

Requires TVFS files to be present.

#### Dump Filelist

```
> Dump Filelist   Read and dump install/TVFS filelist.
[Execute] Writing files to X:\XX\XX\XX\XX\Hazno\BattleNGDP\X\Install\X\filelist.txt
```

Dump a list of all derived files from the Install VFS manifests into a newline seperated list.

---

## Remarks

This project has been made possible thanks to numerous community projects, including:

- BlizzTrack - https://blizztrack.com/
    - API for product definition history.
- World of Warcraft Developer Wiki - https://wowdev.wiki/
- Cascette Tools - https://github.com/wowemulation-dev/cascette-py
- CascLib - https://github.com/ladislav-zezula/CascLib

---

## Disclaimer

This project is not affiliated with Blizzard Entertainment, Inc. All trademarks referenced herein are the properties of
their respective owners. 2026 Blizzard Entertainment, Inc. All rights reserved.