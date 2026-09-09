# netSpy

netSpy is a dnSpyEx-based debugger and .NET assembly editor with support for inspecting and editing official .NET single-file bundles. It preserves the dnSpy editing and debugging workflow while adding a dedicated bundle subsystem.

dnSpyEx is the upstream base for netSpy: https://github.com/dnSpyEx/dnSpy.

- Debug .NET and Unity assemblies
- Edit .NET and Unity assemblies
- Light and dark themes

See below for more features

![debug-animated](images/debug-animated.gif)

![edit-code-animated](images/edit-code-animated.gif)

## Binaries

Download netSpy builds from this repository's [Releases](https://github.com/RestitvtorOrbis/netspy/releases). If a suitable release asset is not available, use the [Actions](https://github.com/RestitvtorOrbis/netspy/actions) fallback and download the matching `dnSpy-*` artifact from a successful build run.

Extract the complete archive directory together; do not copy only `dnSpy.exe`. For the reported Windows x64 case, choose `netSpy-net-win64.zip`, the self-contained x64 package. The other variants are:

- `netSpy-netframework.zip` requires .NET Framework 4.8.
- `netSpy-net.zip` requires the .NET 10 Desktop Runtime.
- `netSpy-net-win32.zip` is the self-contained x86 package.

The executable inside each netSpy archive remains `dnSpy.exe`. After extraction, launch it, open the official .NET single-file bundle, expand `Assemblies`, and select its main DLL or a type to inspect or decompile. This support does not claim that every method will decompile and does not cover third-party packers.

## Building

```PS
git clone --recursive https://github.com/dnSpyEx/dnSpy.git
cd dnSpy
# or dotnet build
./build.ps1 -NoMsbuild
```

To debug Unity games, you need this repo too: https://github.com/dnSpyEx/dnSpy-Unity-mono

# Debugger

- Debug .NET Framework, .NET and Unity game assemblies, no source code required
- Set breakpoints and step into any assembly
- Locals, watch, autos windows
- Variables windows support saving variables (eg. decrypted byte arrays) to disk or view them in the hex editor (memory window)
- Object IDs
- Multiple processes can be debugged at the same time
- Break on module load
- Tracepoints and conditional breakpoints
- Export/import breakpoints and tracepoints
- Optional Just My Code (JMC) stepping filters for system libraries
- Call stack, threads, modules, processes windows
- Break on thrown exceptions (1st chance)
- Variables windows support evaluating C# / Visual Basic expressions
- Dynamic modules can be debugged (but not dynamic methods due to CLR limitations)
- Output window logs various debugging events, and it shows timestamps by default :)
- Assemblies that decrypt themselves at runtime can be debugged, dnSpy will use the in-memory image. You can also force dnSpy to always use in-memory images instead of disk files.
- Bypasses for common debugger detection techniques
- Public API, you can write an extension or use the C# Interactive window to control the debugger

# Assembly Editor

- All metadata can be edited
- Edit methods and classes in C# or Visual Basic with IntelliSense, no source code required
- Add new methods, classes or members in C# or Visual Basic
- IL editor for low-level IL method body editing
- Low-level metadata tables can be edited. This uses the hex editor internally.

# Hex Editor

- Click on an address in the decompiled code to go to its IL code in the hex editor
- The reverse of the above, press F12 in an IL body in the hex editor to go to the decompiled code or other high-level representation of the bits. It's great to find out which statement a patch modified.
- Highlights .NET metadata structures and PE structures
- Tooltips show more info about the selected .NET metadata / PE field
- Go to position, file, RVA
- Go to .NET metadata token, method body, #Blob / #Strings / #US heap offset or #GUID heap index
- Follow references (Ctrl+F12)

# Other

- BAML decompiler and disassembler
- Blue, light and dark themes (and a dark high contrast theme)
- Bookmarks
- C# Interactive window can be used to script dnSpy
- Search assemblies for classes, methods, strings, etc
- Analyze class and method usage, find callers, etc
- Multiple tabs and tab groups
- References are highlighted, use Tab / Shift+Tab to move to the next reference
- Go to the entry point and module initializer commands
- Go to metadata token or metadata row commands
- Code tooltips (C# and Visual Basic)
- Export to project

# List of other open source libraries used by dnSpy

- [ILSpy decompiler engine](https://github.com/icsharpcode/ILSpy) (C# and Visual Basic decompilers)
- [Roslyn](https://github.com/dotnet/roslyn) (C# and Visual Basic compilers)
- [dnlib](https://github.com/0xd4d/dnlib) (.NET metadata reader/writer which can also read obfuscated assemblies)
- [VS MEF](https://github.com/microsoft/vs-mef) (Faster MEF equals faster startup)
- [ClrMD](https://github.com/microsoft/clrmd) (Access to lower level debugging info not provided by the CorDebug API)
- [Iced](https://github.com/icedland/iced) (x86/x64 disassembler)
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (JSON serializer & deserializer)
- [NuGet.Configuration](https://github.com/NuGet/NuGet.Client) (NuGet configuration file reader)

# Translating dnSpy

[Click here](https://crowdin.com/project/dnspy) if you want to help with translating dnSpy to your native language.

# Wiki

See the [Wiki](https://github.com/dnSpyEx/dnSpy/wiki) for build instructions and other documentation.

# License

dnSpy is licensed under [GPLv3](dnSpy/dnSpy/LicenseInfo/GPLv3.txt).

# [Credits](dnSpy/dnSpy/LicenseInfo/CREDITS.txt)
