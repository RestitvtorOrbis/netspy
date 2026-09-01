# Microsoft.NET.HostModel.Bundle

This directory contains the Windows x64 bundling subset of the official
`Microsoft.NET.HostModel` sources from the .NET runtime.

Source revision: `dotnet/runtime` tag `v10.0.11`, commit
`79d0c463f1b55624c874a11585f7e47731e8d675`.

The exact 15-file dependency closure, upstream SHA-256 values, five canonical
Windows-only patches, resulting source hashes, and adaptation rationale are
recorded in `UPSTREAM-PATCHES/README.md`. The vendored files retain their
upstream MIT headers. The upstream license is copied as `LICENSE.TXT`.

Only Windows PE bundling is included. Mach-O, ELF, apphost mutation, signing,
and unrelated HostModel APIs remain outside this library.
