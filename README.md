# Try F# on WebAssembly (Already migrated from netcoreapp3.1 to net10.0)

Official site: https://github.com/fsbolero/TryFSharpOnWasm

[![Build status](https://ci.appveyor.com/api/projects/status/mw21lo0uhu19fkfi?svg=true)](https://ci.appveyor.com/project/IntelliFactory/tryfsharponwasm)

This is the repository for the [Try F# on WebAssembly](https://tryfsharp.fsbolero.io) website.

Uses Bolero - F# Tools for Blazor, see [website](https://fsbolero.io/) and [repository](https://github.com/fsbolero/Bolero).

## Building this project

Open the solution file with Visual Studio 2026+ (need to be supported .net 10) and git clone the Bolero FCS project, make sure the project reference is correct, then rebuild the `WebFsc.Server`.

The server project `WebFsc.Server` is just here for developer convenience (hot reloading, MIME type for *.fsx); the actual deployed project is `WebFsc.Client`.

Bolero FCS is here:

https://github.com/ingted/Bolero.FCS.Build

This Bolero FCS is based on commit: 6396a18a707b29f552373b8ff5650c98beb9bcfc of https://github.com/dotnet/fsharp
Parallel compilation is removed...

<img width="1899" height="1028" alt="Image" src="https://github.com/user-attachments/assets/0df180b1-2d64-4e2f-b504-ca1f0db7e5fd" />

<img width="367" height="446" alt="Image" src="https://github.com/user-attachments/assets/53ad412c-76e4-4a13-a1b4-2a90a45a3fe1" />

