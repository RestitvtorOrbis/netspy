# netSpy Visible Branding

Status: decision-complete; implementation required now
Repository baseline: `24e3b7241` (`master`)
Specification date: 2026-09-04

## 1. Outcome

The desktop application will present itself unmistakably as **netSpy** while retaining dnSpy's compatibility-sensitive internals. The main window and application-owned dialogs will say `netSpy`; startup and About surfaces will use a new, independently authored netSpy mark; and Windows file properties will identify the application as netSpy. The About screen will describe netSpy's single-file focus and will continue to credit dnSpy/dnSpyEx and their contributors.

This is a visible-branding change, not a source-wide rename. In particular, the managed assembly remains `dnSpy`, launcher filenames remain `dnSpy.exe` / `dnSpy-x86.exe` / `dnSpy.Console.exe`, namespaces and projects remain under `dnSpy`, existing settings remain discoverable, and extensions keep their existing identities and contracts.

The user-visible spelling is normative and case-sensitive: **`netSpy`** (lowercase `net`, uppercase `S`, lowercase `py`).

## 2. Repository findings

### 2.1 Visible identity and compatibility identity are currently mixed

`dnSpy/dnSpy/MainApp/Constants.cs` currently exposes:

```csharp
public const string DnSpy = "dnSpy";
public const string DnSpyFile = DnSpy;
```

`Constants.DnSpy` supplies both visible window/dialog titles and the private `WM_COPYDATA` single-instance header. `Constants.DnSpyFile` supplies the LocalAppData startup-profile directory and MEF cache filename. A blind rename would therefore strand existing state and alter the inter-process protocol.

The narrow seam is to add `Constants.AppName = "netSpy"`, use it only for presentation, and retain the two existing `dnSpy` constants for compatibility. One special case exists in `App.xaml.cs`: the sender locates a same-product window by title prefix before sending the legacy wire header. The title-prefix check must use `AppName`, while `COPYDATASTRUCT_HEADER` and its validation must continue to use `DnSpy`.

Current visible consumers of the constant are:

- `MainWindow.xaml` and `AppWindow.GetDefaultTitle()`;
- `AskDlg.xaml` and `MsgBoxDlg.xaml`;
- unhandled-exception and settings-reset message boxes in `App.xaml.cs` and `StartUpClass.cs`;
- the large-document progress dialog in `Documents/DsDocumentLoader.cs`.

### 2.2 User-facing resource strings

The neutral resources contain visible dnSpy references for Explorer integration, loading, restart-after-language-change, license text, and update notifications. Satellite resources contain translated variants of some of the same strings. Resource **keys** such as `ExplorerOpenWithDnSpy` and `LoadingDnSpyPleaseWait` are generated API and will not be renamed; only their values change.

The About tab is generated in `MainApp/AboutScreen.cs`. It currently shows only `dnSpy`, framework/version, GPL text, update controls, loaded assemblies, and the existing `CREDITS.txt`. `MainApp/AboutCommands.cs` and `MainApp/UpdateService.cs` still target the upstream dnSpyEx GitHub repository.

No netSpy release-feed URL is present in the repository. This change must not present an upstream dnSpyEx release as a netSpy release. Update links and messages will therefore be labeled explicitly as **upstream dnSpyEx**. Their URL and version-comparison behavior remain unchanged until a separate release-channel decision is made.

### 2.3 Existing visual identity

- `dnSpy/dnSpy/Images/dnSpy.ico` and `dnSpy-x86.ico` are the native application icons.
- `dnSpy.csproj`, `dnSpy-x86.csproj`, and the common `MetroWindow` style refer to those icons.
- `MainWindow.xaml` and `DsLoaderControl.xaml` use the generic `DsImages.Assembly` glyph. The main-window use is the custom caption's system-menu glyph; the loader use is the prominent startup graphic.
- The custom `MetroWindow` caption does not render `Window.Icon`. Its template renders only a `DsImage` bound to `MetroWindow.SystemMenuImage`, whose default is an empty `ImageReference`. Removing the main window's explicit `SystemMenuImage` without changing that template would therefore leave the caption button blank even though the taskbar/native window icon changed.
- The four `.dntheme` files own user-selectable and accessibility-sensitive colors. Recoloring them globally would change editor semantics and user expectations far beyond branding.

The appropriate visual seam is a small reusable WPF vector mark on startup and About, a matching native icon, and a restrained cyan-to-violet accent rule on those two branding surfaces. The existing themes, editor classification colors, generic assembly/file glyphs, and high-contrast behavior remain untouched.

### 2.4 Assembly and launch packaging

The SDK currently derives `AssemblyTitle`, `AssemblyProduct`, and `AssemblyCompany` from the `dnSpy` project name. These values appear as `dnSpy` in Windows file properties. They can safely be overridden in `dnSpy.csproj` without changing `AssemblyName`.

Renaming the output executable is not narrow or compatibility-neutral:

- `build.ps1` explicitly relocates and patches `dnSpy.exe`, `dnSpy-x86.exe`, and `dnSpy.Console.exe` for .NET Framework, framework-dependent .NET, and self-contained packages;
- .NET apphosts embed the managed entry-assembly name;
- strong-name friend declarations and XAML pack URIs depend on the managed identity `dnSpy`;
- extensions and external launch scripts may depend on the current filenames.

Consequently this change updates file **metadata and icon**, but not file names. CI artifact names also remain unchanged in this ticket set so a branding edit does not overlap the active CI repair work. A future launcher/packaging migration may provide `netSpy.exe` through a compatibility shim, but must be separately specified and tested across all four package layouts.

### 2.5 Tests

There is no existing branding-specific test project. `Tests/dnSpy.Bundles.IntegrationTests` already references the product assembly and targets `net10.0-windows`, so one small `BrandingTests.cs` fixture there can validate constants and assembly metadata without introducing another test project. Visual appearance still requires a Windows smoke check; automated pixel/screenshot tests are not justified for this narrow change.

The following untracked BND-027 work predates this specification and is unrelated:

- `Tests/dnSpy.Bundles.IntegrationTests/BundleLogicalEquivalenceTests.cs`
- `Tests/dnSpy.Bundles.IntegrationTests/IntegrationFixtureLocator.cs`
- `Tests/dnSpy.Bundles.IntegrationTests/OrdinaryOpenSaveRegressionTests.cs`

Implementers and reviewers must neither edit nor stage those files.

At baseline their SHA-256 hashes are, respectively, `fa8966a0331d0192dbbaf1fa2d80018f64e50caf03e2e54d3ff54d046f55a81d`, `9923b1eb6932ff049358bd571639fa3bfc00a63eb936386a6581a35f67e06753`, and `16f7e10a075c7361223b87d3ff184c46d9fc723ff89f79d6a84d598486dbdb9f`. These hashes are the preservation contract until their owner handles them.

CI-001 and CI-002 are committed at this baseline; `Libraries/Microsoft.NET.HostModel.Bundle/packages.lock.json`, `docs/specs/ci-completion.md`, and `docs/specs/dotnet-single-file-bundles.md` are clean. They remain CI/BND-owned and no NSPY ticket may edit or stage them. CI-003 is awaiting separate push authorization, but the user's 2026-09-04 direction explicitly starts branding now and supersedes the former CI-003/BND-027/BND-028 prerequisite. Branding implementation must therefore coexist with, but must not absorb or modify, the three untracked BND-027 files.

### 2.6 Implementation audit at the current baseline

Visible branding is **not implemented** at `24e3b7241`. History contains no NSPY implementation commit. Product sources still use `Constants.DnSpy` for visible titles, the loader and About still use the generic assembly identity, application icon references still point to `dnSpy.ico`/`dnSpy-x86.ico`, file metadata has no BR-6 overrides, and README still leads with dnSpy. Repository-wide `netSpy` matches in compiled product code are contributor copyright headers, not visible branding. All four NSPY tickets below are therefore required; none may be marked skipped based on existing code.

## 3. Requirements

### BR-1: Canonical displayed name

1. Add `public const string AppName = "netSpy"` to the internal `dnSpy.MainApp.Constants` class, matching the accessibility pattern of its existing constants without creating a public assembly type.
2. Application-owned visible titles use `Constants.AppName`, including the initial and versioned main-window title, Ask/Message dialogs, unhandled-error caption, settings-reset prompt, and document-load progress dialog.
3. The normal title format remains structurally unchanged:

   ```text
   netSpy <informational-version> (<architecture>, <framework>[, Administrator][, Debug Build])
   ```

4. Do not replace historical/legal references where `dnSpy` names the upstream project, an assembly, a namespace, an extension API, a resource key, or a compatibility identifier.

### BR-2: Compatibility boundary

The following stay byte-for-byte/string-for-string compatible:

- managed assembly names, especially `dnSpy`, `dnSpy-x86`, and `dnSpy.Console`;
- executable and PDB/config filenames;
- namespaces, project paths/names, XAML pack URIs, MEF contracts, GUIDs, and strong-name friend declarations;
- `Constants.DnSpy == "dnSpy"` and `Constants.DnSpyFile == "dnSpy"`;
- LocalAppData/startup profile paths and `dnSpy-mef-info.bin`;
- the `WM_COPYDATA` payload header `dnSpy` and its validation;
- command-line switches and parsing;
- settings filename/default location and Registry association mechanics.

Because a netSpy window no longer starts with `dnSpy`, only the single-instance **window-discovery title prefix** changes to `Constants.AppName + " "`. This allows two instances of the new build to find one another while retaining the existing message payload protocol.

### BR-3: Visible text and localization

1. In the neutral `.resx`, change product references to `netSpy` for:
   - Explorer `Open with ...`;
   - loading text;
   - language-restart text;
   - GPL license text.
2. In every satellite `.resx` that supplies one of those values, replace only the literal product token (`dnSpy`, `dnspy`, or `DnSpy`) with `netSpy`; preserve the translator's surrounding text and mnemonic markers.
3. Change the neutral About menu and tab values to `_About netSpy` and `About netSpy`. Satellite translations may retain their localized generic “About” label because the About content itself always starts with the canonical name.
4. Update the hard-coded Explorer failure to `Cannot locate netSpy!` (prefer formatting from `Constants.AppName` over another duplicated literal).
5. If the excluded `DevBuildWarning.cs` is touched, its product reference becomes netSpy but the dnSpyEx Actions URL is explicitly described as upstream. This file is optional because it is not compiled; it must not become a reason to expand scope.
6. Update UI remains connected to the existing dnSpyEx endpoint, but visible text must say `upstream dnSpyEx release`, never “new version of netSpy.” Exact neutral English copy:
   - button: `Check for upstream dnSpyEx updates`;
   - available prompt: `A new upstream dnSpyEx release is available: {0}. Do you want to open its download page?`;
   - latest result: `You are running a version based on the latest upstream dnSpyEx release.`;
   - info bar: `A new upstream dnSpyEx release is available: {0}`.
7. Existing translated update strings that explicitly claim a dnSpy product update are allowed to remain as historical upstream wording in this narrow change; they must not be mechanically changed to netSpy. A localization follow-up can translate the new upstream distinction.

### BR-4: About identity and provenance

The top of About must render, in this order:

```text
netSpy <informational-version> (<framework>)
A dnSpyEx-based .NET assembly editor, debugger, and single-file bundle explorer.
netSpy is free software licensed under GPLv3.
Based on dnSpyEx and dnSpy; original copyright and contributor credits follow.
```

The description and attribution are new neutral-resource values. Do not alter or truncate the existing loaded-file list or embedded `CREDITS.txt`. About/release/issues/wiki/source links continue to target `https://github.com/dnSpyEx/dnSpy/`, but their menu labels must include `Upstream dnSpyEx` so the destination is not misrepresented:

- `Latest _Upstream dnSpyEx Release`
- `Upstream dnSpyEx _Issues`
- `Upstream dnSpyEx _Wiki`
- `Upstream dnSpyEx _Source Code`

The existing GPLv3 text and original copyright lines remain in the distributed application. No claim of affiliation, endorsement, or original authorship is added.

### BR-5: Distinct visual mark

Add an original, project-owned netSpy mark with this fixed visual language:

- a 64-by-64 logical square with 12-unit rounded corners;
- opaque midnight background `#FF111827`;
- a connected, angular lowercase-`n`/network trace from `(14,45)` through `(14,25)`, `(32,14)`, `(50,25)`, `(50,45)`, drawn in cyan `#FF22D3EE`, width `6`, with round caps and joins;
- nodes centered at `(14,45)`, `(32,14)`, and `(50,45)`, radius `4`, filled violet `#FFA78BFA` with a 2-unit near-white `#FFF8FAFC` outline;
- no `dnSpy` artwork, letters, screenshots, or third-party logo geometry may be copied into the mark.

Implementation assets:

1. `MainApp/BrandMark.xaml` / `.xaml.cs`: a reusable, non-focusable WPF `UserControl` containing the vector geometry in a `Viewbox`. It must expose no public API and have an accessible automation name of `netSpy` when used in the loader.
2. `Images/netSpy.ico`: an ICO derived from the same geometry and containing at least 16, 24, 32, 48, 64, and 256-pixel 32-bit variants. It replaces the app icon references in `dnSpy.csproj`, `dnSpy-x86.csproj`, and the default `MetroWindow` style. Both architectures use the same mark; architecture remains text in the title.
3. `Branding/netSpy-logo.svg`: the human-reviewable vector source, with an SPDX `GPL-3.0-or-later` comment and the exact geometry/color constants above.
4. `Branding/README.md`: state that the mark was created for netSpy, is not copied from dnSpy/dnSpyEx artwork, and is distributed under GPL-3.0-or-later with this repository. Document the command/tool and version used to export the checked-in ICO so it can be reproduced. Do not add a new build-time package solely for icon generation.

Use `BrandMark` in place of `DsImages.Assembly` in `DsLoaderControl.xaml`. Add the same 64-pixel mark above the textual header in `AboutScreen.Write()` using its existing `AddUIElement` mechanism. Do not replace generic assembly glyphs elsewhere: those glyphs communicate document types, not product identity.

Remove `MainWindow.xaml`'s explicit generic `SystemMenuImage="{x:Static img:DsImages.Assembly}"` override. In the common `MetroWindow` control template, preserve the existing `DsImage` path whenever `SystemMenuImage.IsDefault` is false, and add a standard WPF `Image` fallback bound to the templated parent's `Icon` whenever `SystemMenuImage.IsDefault` is true. The two paths are mutually exclusive and occupy the same 16-by-16 caption slot. Because the style's `Icon` is changed to `../Images/netSpy.ico`, the main window's custom system-menu caption then displays the same netSpy icon as the native window/taskbar while any window that deliberately supplies an `ImageReference` retains the existing themed image-service behavior.

Do not change the `MetroWindow.SystemMenuImage` dependency property, its default, `DsImage`, `ImageReference`, or the image service. Do not create a `DsImages.netSpy` member or register the brand mark as a document-type image. The template fallback is solely presentation wiring from the already-existing `Window.Icon`; generic assembly/file glyph semantics remain unchanged.

Immediately below the mark on both loader and About, add a non-interactive 2-device-independent-pixel-high horizontal accent, at most 160 units wide, filled left-to-right from cyan `#FF22D3EE` to violet `#FFA78BFA`. It is decorative, excluded from keyboard focus and the accessibility tree, and must not replace dynamic theme brushes for text, controls, selection, or editor content. This is the complete color/appearance change outside the mark and icon.

High-contrast behavior: the mark's opaque background, cyan trace, and outlined nodes are intentionally self-contained and must retain at least 3:1 non-text contrast within the mark. The surrounding About/loader text continues using current dynamic theme resources. Do not alter `.dntheme` files or global color definitions.

### BR-6: Windows file metadata

Set the following only in the primary UI project `dnSpy/dnSpy/dnSpy.csproj`:

```xml
<AssemblyTitle>netSpy</AssemblyTitle>
<Product>netSpy</Product>
<Company>netSpy contributors</Company>
<Description>netSpy .NET assembly editor, debugger, and single-file bundle explorer</Description>
```

Keep `AssemblyName` implicit and therefore equal to `dnSpy`. Keep `DnSpyAssemblyVersion`, informational version, copyright, signing key, and all extension metadata unchanged. The x86 launcher references the primary app assembly and receives its own icon, but its managed identity is not renamed. Console branding is out of scope because this request targets the visible desktop application and changing its identity would affect automation.

### BR-7: Repository-facing description

Update only the README heading and opening paragraph so new users see `netSpy`, its single-file-bundle focus, and the dnSpyEx lineage immediately. Existing upstream links, build commands, feature lists, license link, credits, and repository paths remain valid and must not be globally rewritten.

Normative opening:

```markdown
# netSpy

netSpy is a dnSpyEx-based debugger and .NET assembly editor with support for inspecting and editing official .NET single-file bundles. It preserves the dnSpy editing and debugging workflow while adding a dedicated bundle subsystem.
```

Follow this with one sentence that dnSpyEx is the upstream base and link to `https://github.com/dnSpyEx/dnSpy`.

## 4. Exact contracts and implementation design

### 4.1 `Constants`

The class remains internal and its existing fields remain available:

```csharp
static class Constants {
    // Presentation only. Never use for persisted or wire identity.
    public const string AppName = "netSpy";

    // Compatibility identity used by IPC and historical internals.
    public const string DnSpy = "dnSpy";

    // Persisted/profile filename identity.
    public const string DnSpyFile = DnSpy;
}
```

No new public assembly contract, MEF export, GUID, interface, command, or setting is introduced.

### 4.2 Single-instance protocol

The contract after the change is deliberately asymmetric:

```csharp
const string COPYDATASTRUCT_HEADER = Constants.DnSpy;

// Window discovery only:
windowTitle.StartsWith(Constants.AppName + " ", StringComparison.Ordinal)
```

This must be covered by a source-level invariant test or a small extracted internal helper test. Do not change `COPYDATASTRUCT_dwData`, `COPYDATASTRUCT_result`, argument serialization, or receiver validation.

### 4.3 Brand mark composition

`BrandMark` is presentation-only and belongs in the main UI assembly. It does not use `dnSpy.Images`, add a contract assembly type, or register with the image service. The loader declares it directly in XAML. The About screen constructs it directly because it is in the same assembly. The native ICO is compiled via `ApplicationIcon` and included as the window icon resource.

The common `MetroWindow` template keeps `SystemMenuImage` authoritative when a caller supplies one and uses `Window.Icon` only for the default/empty reference. The required shape is conceptually:

```xml
<Grid Width="16" Height="16">
    <Image x:Name="nativeSystemMenuImage"
           Source="{Binding RelativeSource={RelativeSource TemplatedParent}, Path=Icon}"
           Stretch="Uniform" />
    <img:DsImage x:Name="referencedSystemMenuImage"
                 ImageReference="{Binding RelativeSource={RelativeSource TemplatedParent}, Path=SystemMenuImage}" />
</Grid>
```

The template defaults `nativeSystemMenuImage` to collapsed. A `DataTrigger` bound to `SystemMenuImage.IsDefault` with value `True` collapses `referencedSystemMenuImage` and shows `nativeSystemMenuImage`. No trigger changes the system-menu button command, hit testing, visibility, dimensions outside that 16-by-16 content slot, caption brushes, or supplied `ImageReference` rendering.

### 4.4 Test contract

Add `Tests/dnSpy.Bundles.IntegrationTests/BrandingTests.cs` with focused tests that establish:

- reflected `dnSpy.MainApp.Constants.AppName` equals `netSpy`;
- reflected `DnSpy` and `DnSpyFile` remain `dnSpy`;
- the product assembly simple name remains `dnSpy`;
- `AssemblyTitleAttribute`, `AssemblyProductAttribute`, `AssemblyCompanyAttribute`, and `AssemblyDescriptionAttribute` have the BR-6 values;
- neutral resources used by loader, Explorer, restart, About title/license/description/attribution, and upstream update messages contain the intended name/context;
- the legacy copy-data header remains tied to `Constants.DnSpy` while the window-title discovery is tied to `Constants.AppName`. If reflection cannot observe constants embedded by the compiler, implement this as a test that reads only the two relevant source files from a repository root resolved relative to the test assembly; do not add a production API solely for testing.
- source invariants prove `MainWindow.xaml` no longer assigns `DsImages.Assembly` to `SystemMenuImage`, and the common template contains both the non-default `SystemMenuImage`/`DsImage` path and the default-reference `Window.Icon` fallback. This is a structural source test, not a pixel or exact-template snapshot.

Do not test exact ICO bytes or take pixel snapshots. The project build proves the ICO is valid enough for the Windows resource compiler; the smoke test proves its visible wiring.

## 5. Assumptions

- `netSpy` is the final user-facing capitalization supplied by the user.
- No independent netSpy repository, homepage, issue tracker, or update feed has yet been selected.
- Maintaining existing settings, extensions, scripts, and single-instance behavior is more important than making internal binaries and namespaces match the new brand in this narrow change.
- Original icon authorship/licensing is not documented separately from the GPL repository. A new mark avoids both visual confusion and provenance ambiguity.
- English fallback for the two new About provenance sentences is acceptable; existing translated surrounding UI remains intact.
- CI-003 and BND-027/BND-028 remain separately owned work, but the user has explicitly removed them as prerequisites for this branding ticket set.

## 6. Non-goals

- Renaming namespaces, types, resource classes/keys, assemblies, solution projects, directories, pack URIs, MEF contracts, GUIDs, command-line switches, or strong-name identities.
- Renaming launchers, console binaries, config/PDB files, build output directories, or existing GitHub Actions artifact names.
- Migrating settings, LocalAppData folders, caches, Registry structures, MRU lists, or persisted documents.
- Changing the update endpoint or inventing a netSpy repository URL.
- Rebranding third-party libraries, extension assembly names, legal headers, `CREDITS.txt`, GPL text, upstream links, or historical documentation references.
- A global theme redesign, editor syntax-color changes, new default theme, background imagery, animation, splash window, installer, or shell-file icon registration.
- Screenshot/golden-image automation, full localization, marketing collateral, or release packaging migration.
- Any bundle parser, editor, save/rebuild, or GitHub Actions test repair.

## 7. Dependency-ordered implementation tickets

```text
NSPY-001 Visible identity boundary and metadata
    -> NSPY-002 About/resources/provenance
        -> NSPY-003 Original visual mark and native icon
            -> NSPY-004 Branding regression tests and README
```

The sequence is strict so each change is independently reviewable. Each ticket receives exactly one approved local commit, including that ticket's ledger-row update in this file. Do not combine tickets into one commit. Before each commit, the index may contain only that ticket's owned files plus `docs/specs/netspy-ui-branding.md`; the three protected BND-027 files must remain untracked and hash-identical.

### NSPY-001 — Separate visible identity from compatibility identity

Depends on: none. Start from baseline `24e3b7241` with only the three protected untracked BND-027 files present.

Scope:

- Add `Constants.AppName` without modifying `DnSpy` or `DnSpyFile`.
- Switch every application-owned title/caption consumer listed in BR-1 to `AppName`.
- Change only the window-discovery title prefix to `AppName`; preserve the `WM_COPYDATA` header.
- Add BR-6 metadata and keep the primary assembly simple name `dnSpy`.

Owned files:

- `dnSpy/dnSpy/MainApp/Constants.cs`
- `dnSpy/dnSpy/MainApp/AppWindow.cs`
- `dnSpy/dnSpy/MainApp/App.xaml.cs`
- `dnSpy/dnSpy/MainApp/StartUpClass.cs`
- `dnSpy/dnSpy/MainApp/MainWindow.xaml`
- `dnSpy/dnSpy/MainApp/AskDlg.xaml`
- `dnSpy/dnSpy/MainApp/MsgBoxDlg.xaml`
- `dnSpy/dnSpy/Documents/DsDocumentLoader.cs`
- `dnSpy/dnSpy/dnSpy.csproj`

Acceptance:

- Main and application-owned dialog titles display netSpy.
- Same-build single-instance forwarding still works.
- Compatibility values and assembly identity remain dnSpy.
- Windows generated assembly metadata has the exact BR-6 values.
- `dotnet build dnSpy/dnSpy/dnSpy.csproj -c Release -f net10.0-windows --no-restore` succeeds after restore.

### NSPY-002 — Rebrand visible resources and clarify provenance

Depends on: NSPY-001.

Scope:

- Update neutral and relevant satellite values per BR-3 without renaming keys.
- Add About description/attribution resources and render exact BR-4 content.
- Change About/upstream link labels and update messages to identify dnSpyEx as upstream.
- Change the Explorer failure caption.
- Preserve URLs, release behavior, credits, and legal content.

Owned files:

- `dnSpy/dnSpy/Properties/dnSpy.Resources.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.cs.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.de.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.es-ES.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.fa.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.fr.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.hu.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.it.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.pt-BR.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.pt-PT.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.ru.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.tr.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.uk.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.vi.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.zh-CN.resx`
- `dnSpy/dnSpy/Properties/dnSpy.Resources.Designer.cs`, regenerated for the two new neutral keys
- `dnSpy/dnSpy/MainApp/AboutScreen.cs`
- `dnSpy/dnSpy/MainApp/Settings/WindowsExplorerIntegration.cs`

Acceptance:

- Startup, Explorer, restart, About, and application update text follow BR-3/BR-4.
- Existing translations retain their surrounding translated content.
- Clicking upstream links reaches the same dnSpyEx URLs as before.
- Credits and GPL attribution remain present.
- No user-visible string calls an upstream dnSpyEx release a netSpy release.

### NSPY-003 — Add the netSpy visual mark

Depends on: NSPY-002.

Scope:

- Create the exact original SVG/vector/native icon assets and provenance note in BR-5.
- Use the vector mark in loader and About.
- Replace application/window native icon references for AnyCPU and x86 desktop launchers, and implement the BR-5 default-reference caption fallback in the existing common `MetroWindow` template.
- Do not change `.dntheme` files, global color resources, the image service, or generic document glyphs.

Owned files:

- `dnSpy/dnSpy/Branding/netSpy-logo.svg`
- `dnSpy/dnSpy/Branding/README.md`
- `dnSpy/dnSpy/Images/netSpy.ico`
- `dnSpy/dnSpy/MainApp/BrandMark.xaml`
- `dnSpy/dnSpy/MainApp/BrandMark.xaml.cs`
- `dnSpy/dnSpy/MainApp/DsLoaderControl.xaml`
- `dnSpy/dnSpy/MainApp/MainWindow.xaml`
- `dnSpy/dnSpy/MainApp/AboutScreen.cs`
- `dnSpy/dnSpy/Themes/wpf.styles.templates.xaml`
- `dnSpy/dnSpy/dnSpy.csproj`
- `dnSpy/dnSpy-x86/dnSpy-x86.csproj`

Acceptance:

- App/taskbar/native window icon, custom main-window caption, loader, and About share the new mark.
- The custom caption obtains the mark from `Window.Icon` only when `SystemMenuImage` is the default reference. The NSPY-004 structural test verifies the mutually exclusive trigger, bindings, and `DsImage` precedence; the Windows smoke check verifies the rendered main-window result.
- Loader and About contain only the bounded cyan-to-violet accent rule described by BR-5; no global theme color changes are introduced.
- The old icon is no longer referenced by desktop app/window builds but remains tracked for history/compatibility unless a separate cleanup is approved.
- All required ICO sizes are present.
- Mark geometry and colors match BR-5 and remain legible in dark, light, blue, and Windows high-contrast modes.
- No third-party asset or additional build-time dependency is introduced.

### NSPY-004 — Add regression coverage and repository-facing identity

Depends on: NSPY-003.

Scope:

- Add the focused section 4.4 tests to the existing integration test project.
- Update only the README heading/opening described by BR-7.
- Run scoped tests, normal build, metadata checks, and Windows smoke acceptance.

Owned files:

- `Tests/dnSpy.Bundles.IntegrationTests/BrandingTests.cs`
- `README.md`
- this specification's ledger only

Acceptance:

- Tests detect accidental re-coupling of visible and compatibility identity.
- Tests detect metadata or critical neutral-copy regression.
- README leads with netSpy and retains explicit dnSpyEx lineage.
- No BND/CI-owned file is modified or staged by this ticket; the three protected untracked BND-027 files remain byte-identical to the hashes in section 2.5.
- All final verification in section 9 passes.

## 8. Ticket ledger

| Ticket | Status | Commit | Evidence / notes |
|---|---|---|---|
| NSPY-001 | approved | `feat(NSPY-001): separate visible application identity` | All nine owned title/metadata files independently reviewed; visible captions and window discovery use `AppName`, while `DnSpy`, `DnSpyFile`, the copy-data header, assembly name, launchers, and persisted identities remain unchanged. The exact `net10.0-windows --no-restore` product build passes with 0 warnings/errors; source invariants, protected BND-027 hashes, and `git diff --check` pass. |
| NSPY-002 | approved | `feat(NSPY-002): clarify netSpy identity and provenance` | Neutral and all 14 enumerated satellite resources, deterministic designer members, About ordering/provenance, Explorer failure text, and upstream dnSpyEx update/link labels independently reviewed. Existing URLs, update behavior, translated surrounding text, loaded-file list, GPL content, and credits remain intact. The exact product build passes with 0 warnings/errors; protected hashes and `git diff --check` pass. |
| NSPY-003 | pending | — | — |
| NSPY-004 | pending | — | — |

Allowed statuses are `pending`, `in progress`, `approved`, and `skipped`. A ticket is `approved` only after implementation verification and independent review. Each approved ticket receives one focused local commit whose subject contains its ticket ID. The specification/ledger update may accompany the relevant ticket commit after the initial dedicated specification commit.

## 9. Verification

Run from the repository root. Use PowerShell 7 and the pinned SDK on Windows for the normal product build.

### 9.1 Scoped automated checks

```powershell
dotnet restore Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj
dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj `
  -c Release -f net10.0-windows `
  --filter 'FullyQualifiedName~BrandingTests'
```

### 9.2 Product build

```powershell
pwsh -NoProfile -File .\build.ps1 net
```

Before a final release, the repository's full normal matrix must also pass on Windows:

```powershell
pwsh -NoProfile -File .\build.ps1 all
```

### 9.3 Metadata and identity assertions

After `build.ps1 net`:

```powershell
$assemblyPath = Resolve-Path '.\dnSpy\dnSpy\bin\Release\net10.0-windows\bin\dnSpy.dll'
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Name
if ($assemblyName -ne 'dnSpy') { throw "Managed identity changed: $assemblyName" }

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath)
if ($versionInfo.ProductName -ne 'netSpy') { throw "Unexpected product: $($versionInfo.ProductName)" }
if ($versionInfo.FileDescription -ne 'netSpy') { throw "Unexpected title: $($versionInfo.FileDescription)" }

@(
  '.\dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.exe',
  '.\dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy-x86.exe',
  '.\dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.Console.exe'
) | ForEach-Object {
  if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) { throw "Missing compatibility launcher: $_" }
}
```

Verify the icon frame inventory using a Windows icon inspector selected during NSPY-003 and record the exact reproducible command in `Branding/README.md`. Required sizes are `16,24,32,48,64,256`, all 32-bit.

### 9.4 Source invariants

```powershell
$protectedBnd027 = [ordered]@{
  'Tests/dnSpy.Bundles.IntegrationTests/BundleLogicalEquivalenceTests.cs' = 'fa8966a0331d0192dbbaf1fa2d80018f64e50caf03e2e54d3ff54d046f55a81d'
  'Tests/dnSpy.Bundles.IntegrationTests/IntegrationFixtureLocator.cs' = '9923b1eb6932ff049358bd571639fa3bfc00a63eb936386a6581a35f67e06753'
  'Tests/dnSpy.Bundles.IntegrationTests/OrdinaryOpenSaveRegressionTests.cs' = '16f7e10a075c7361223b87d3ff184c46d9fc723ff89f79d6a84d598486dbdb9f'
}
foreach ($entry in $protectedBnd027.GetEnumerator()) {
  if (-not (Test-Path -LiteralPath $entry.Key -PathType Leaf)) {
    throw "Protected BND-027 file is missing: $($entry.Key)"
  }
  $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $entry.Key).Hash.ToLowerInvariant()
  if ($actual -cne $entry.Value) {
    throw "Protected BND-027 file changed: $($entry.Key)"
  }
  if (git ls-files --error-unmatch -- $entry.Key 2>$null) {
    throw "Protected BND-027 file was staged or committed: $($entry.Key)"
  }
}

if (rg -n '<AssemblyName>netSpy|namespace netSpy|assembly=netSpy' dnSpy Extensions Tests) {
  throw 'Internal identity was renamed'
}

rg -n 'Constants\.(AppName|DnSpy|DnSpyFile)|COPYDATASTRUCT_HEADER' `
  dnSpy/dnSpy/MainApp/Constants.cs dnSpy/dnSpy/MainApp/App.xaml.cs

git diff --check
git status --short
```

The three protected BND-027 files are expected to remain untracked throughout branding delivery. Their presence is not a blocker; any content change or staged state is. No other branding-unrelated change may be present or absorbed into an NSPY commit.

### 9.5 Windows UI smoke acceptance

On a Windows runner or workstation, launch the framework-dependent .NET build and record pass/fail for:

1. The taskbar, native window icon, and custom-caption system-menu button all show the new network-`n` mark; the caption button remains clickable and opens the normal system menu.
2. The loader shows the mark and `Loading netSpy. Please wait...`.
3. The main title begins `netSpy` and retains architecture/framework qualifiers.
4. Help → About is titled `About netSpy`, displays the mark, the four BR-4 lines, loaded files, GPL text, and original credits.
5. The upstream update button/menu labels say dnSpyEx and their links still open the dnSpyEx repository.
6. Ask, message, error, and long-document progress dialogs use a netSpy caption when exercised.
7. Enable Explorer integration and verify the context menu says `Open with netSpy`; then disable it and verify the same entry is removed.
8. Start a second instance with a file argument and verify the first netSpy window receives/opens it under the existing single-instance behavior.
9. Switch among dark, light, blue, and Windows high-contrast modes and verify the mark remains recognizable and surrounding text remains readable.

## 10. Known limitations after delivery

- Distributed launcher filenames and internal assembly names still say dnSpy by design.
- Update checks still compare against and download from upstream dnSpyEx; the UI identifies that fact.
- New About provenance sentences use neutral English fallback until translators provide satellite values.
- Old dnSpy icon files remain in source history and may remain tracked, though no desktop branding surface references them.
- Console/help output, extension names, internal strings used as content-type or settings identifiers, and historical/legal source comments retain dnSpy.
- No installer, independent netSpy release feed, code-signing identity, or packaging migration is established by this work.

## 11. Delivery boundary

This ticket set starts immediately at `24e3b7241`; CI-003 and BND-027/BND-028 are not prerequisites under the user's explicit override. Each NSPY ticket receives one focused local commit and updates only its own ledger row. Branding work must not change the workflow, build-script repair, bundle implementation/specification, HostModel lock file, or the three protected BND-027 files. The scoped tests, normal Windows product build, identity checks, and UI smoke list in section 9 are the branding acceptance gates. A later final-candidate GitHub Actions run remains valuable integration evidence, but remote CI-003 authorization or unfinished BND tickets cannot block completion of NSPY-001 through NSPY-004.
