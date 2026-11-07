# Third-party notices

## Shipped application dependency

**Iced 1.21.0** — MIT license, Copyright (c) 2018-present iced project and contributors. Used for independent x86-64 decoding/encoding and formatting. Source: https://github.com/icedland/iced . Exact distributed license is included in [third-party/Iced-LICENSE.txt](third-party/Iced-LICENSE.txt).

**.NET / WPF runtime** — Microsoft and .NET contributors, MIT and accompanying third-party terms. The self-contained distribution includes runtime code. SDK-distributed license/notices are retained under `third-party/`; runtime pack license/notices are also collected during packaging where available. See https://github.com/dotnet/runtime and https://github.com/dotnet/wpf . The NuGet lock files record application/package versions.

## Development-only dependencies

xUnit (Apache-2.0), xUnit Visual Studio runner (Apache-2.0), Microsoft.NET.Test.Sdk / VSTest (MIT), and their NuGet dependencies are used for tests and are not dependencies of the shipped executable. GitHub Actions use the referenced action authors' licenses; no action code is embedded in the application.

## References, not incorporated components

Upstream ps4debug-NG and GoldHEN cheat repository are GPL-3; PS4 Cheats Maker and Ghidra are Apache-2.0. PS4CheaterNeo, GoldHEN and Reaper checkouts did not provide a clear repository-wide redistribution license in the inspected tree. No source, payload binaries or original trainers from these projects are included. See [docs/REFERENCES.md](docs/REFERENCES.md) for reviewed revisions/files and the distinction between protocol/format facts and source reuse.

This project is independent and is not endorsed by Sony, GoldHEN or the reference project maintainers. Examples are artificial and do not redistribute a game's memory dump or save data.
