# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jellyfin plugin that resolves BDMV (Blu-ray) folders into individual episodes using playlist-to-episode mappings from thediscdb.com. When Jellyfin scans a TV show library and encounters a BDMV folder inside a Season directory, this plugin intercepts the scan, computes a content hash from the disc's `.m2ts` files, looks up the disc on TheDiscDb's GraphQL API, and creates individual Episode items pointing to the correct `.m2ts` stream files.

## Build

```bash
dotnet build
```

The plugin targets `net9.0` and depends on `Jellyfin.Controller` and `Jellyfin.Model` 10.11.6 (compile-only, excluded at runtime). No solution file — build directly from the `.csproj`.

## Deploy to Test Jellyfin

Build the DLL and copy it into the test environment:

```bash
dotnet build
cp bin/Debug/net9.0/Jellyfin.Plugin.TheDiscDb.dll test/config/plugins/TheDiscDb/
```

The `test/` directory contains a full Jellyfin server config (database, library root, plugin config) for manual testing. The media library root is at `test/media/TV/`.

## Architecture

The plugin has four key components that form a pipeline:

1. **ContentHashCalculator** (`TheDiscDb/ContentHashCalculator.cs`) — Computes an MD5 hash from the sorted `.m2ts` file sizes in `BDMV/STREAM/`. This hash is TheDiscDb's disc fingerprint format (little-endian Int64 sizes, sorted by filename).

2. **TheDiscDbClient** (`TheDiscDb/TheDiscDbClient.cs`) — GraphQL client that queries `thediscdb.com/graphql` for disc metadata by content hash. Uses a two-tier cache: in-memory `ConcurrentDictionary` + on-disk JSON files in Jellyfin's cache directory. Cache TTL is configurable (default 168h/1 week).

3. **BdmvEpisodeResolver** (`Resolvers/BdmvEpisodeResolver.cs`) — Implements both `IItemResolver` and `IMultiItemResolver`. Two entry points:
   - `ResolvePath`: Intercepts BDMV-containing folders under a Series to return a `Season` (preventing Jellyfin's built-in EpisodeResolver from claiming the whole BDMV as one episode). Locks the Season to prevent metadata overwrites.
   - `ResolveMultiple`: When Jellyfin scans the Season's children, splits the BDMV into individual `Episode` and `Video` (extras) items. Resolves `.m2ts` files either by parsing the MPLS playlist binary or falling back to the SegmentMap from the API.

4. **BdmvEpisodePreRefreshProvider** (`Providers/BdmvEpisodePreRefreshProvider.cs`) — `ICustomMetadataProvider<Episode>` that runs after all other metadata providers. Restores correct season/episode numbering from the `TheDiscDb` provider ID string (format: `hash:playlist:SxxExx:Title`), since Jellyfin's filename parser and TMDB provider overwrite the resolver's values.

**MplsParser** (`Parsers/MplsParser.cs`) — Minimal binary parser for Blu-ray `.mpls` playlist files. Extracts clip filenames from PlayItem entries.

**Models** (`TheDiscDb/Models.cs`) — DTOs for the GraphQL response. The query navigates `mediaItems -> releases -> discs -> titles` and the client extracts the matching disc by content hash.

## Key Design Decisions

- The resolver API is synchronous but `TheDiscDbClient` is async — the resolver uses `.GetAwaiter().GetResult()` to bridge the gap.
- Episodes encode all TheDiscDb metadata in the provider ID string so the PreRefreshProvider can restore it without re-querying the API.
- Non-BDMV files in a Season folder are passed through as `ExtraFiles` so normal resolvers still handle them.
- `ImplicitUsings` is disabled — all `using` statements are explicit.


## reference material

- the jellyfin codebase is available at `/home/ben/Documents/programmingPlay/jellyfin`